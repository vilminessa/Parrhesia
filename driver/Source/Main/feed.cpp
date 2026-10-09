/*++

Module Name:

    feed.cpp

Abstract:

    Parrhesia Feed — реализация кольцевого буфера и control-устройства
    \\.\ParrhesiaFeed (см. feed.h).

    Диспетчеры заворачиваются поверх обработчиков PortCls: IRP для
    control-устройства (DeviceObject == g_FeedDevice) обрабатываем сами,
    остальные передаём сохранённым оригинальным обработчикам.

--*/

#include "definitions.h"
#include "feed.h"
#include <ntstrsafe.h>

//=============================================================================
// Globals
//=============================================================================

// Слоты инстансов (per-adapter). Хранение статическое: CParrhesiaFeed без
// конструктора (см. feed.h), вся память обнуляется загрузчиком.
typedef struct _FEED_SLOT
{
    CParrhesiaFeed Feed;
    PDEVICE_OBJECT Device;
    UNICODE_STRING DeviceName;
    UNICODE_STRING Symlink;
    BOOLEAN        SymlinkCreated;
    BOOLEAN        Used;
    WCHAR          DeviceNameBuffer[PFEED_NAME_CCH];
    WCHAR          SymlinkBuffer[PFEED_NAME_CCH];
} FEED_SLOT;

static FEED_SLOT g_Feeds[PFEED_MAX_INSTANCES];

// Оригинальные обработчики PortCls (до заворачивания).
static PDRIVER_DISPATCH g_OrigCreate = NULL;
static PDRIVER_DISPATCH g_OrigCleanup = NULL;
static PDRIVER_DISPATCH g_OrigClose = NULL;
static PDRIVER_DISPATCH g_OrigDeviceControl = NULL;
static BOOLEAN g_FeedDispatchHooked = FALSE;

// Дубль диагностики в сервис-ключ (определение ниже, перед Feed_DiagSet*).
static void DiagLogToService(_In_ const WCHAR* NameFormat, _In_ ULONG Arg1, _In_ ULONG Arg2, _In_ DWORD Value);

//=============================================================================
// CParrhesiaFeed
//=============================================================================

NTSTATUS CParrhesiaFeed::Init()
{
    PAGED_CODE();

    // Инициализация выполняется здесь, а не в конструкторе (см. feed.h).
    KeInitializeSpinLock(&m_Lock);
    m_Buffer = NULL;
    m_WritePos = m_ReadPos = 0;
    m_Written = m_Delivered = m_Dropped = m_Underrun = 0;
    m_Loop = 0;
    m_CableDrop = 0;
    m_Owner = NULL;
    m_FormatMismatch = 0;
    m_Writers = 0;
    for (LONG i = 0; i < MaxWriterHandles; i++)
    {
        m_WriterFO[i] = NULL;
    }

    BYTE *buffer = (BYTE *)ExAllocatePool2(POOL_FLAG_NON_PAGED, PFEED_RING_BYTES, VIRTUALAUDIODRIVER_POOLTAG);
    if (buffer == NULL)
    {
        return STATUS_INSUFFICIENT_RESOURCES;
    }

    KIRQL oldIrql;
    KeAcquireSpinLock(&m_Lock, &oldIrql);
    m_Buffer = buffer;
    KeReleaseSpinLock(&m_Lock, oldIrql);

    return STATUS_SUCCESS;
}

void CParrhesiaFeed::Free()
{
    PAGED_CODE();

    BYTE *buffer;
    KIRQL oldIrql;
    KeAcquireSpinLock(&m_Lock, &oldIrql);
    buffer = m_Buffer;
    m_Buffer = NULL;
    m_Owner = NULL;
    KeReleaseSpinLock(&m_Lock, oldIrql);

    if (buffer != NULL)
    {
        ExFreePoolWithTag(buffer, VIRTUALAUDIODRIVER_POOLTAG);
    }
}

void CParrhesiaFeed::Write(const BYTE *src, ULONG len)
{
    if (len == 0 || (len % PFEED_FRAME_BYTES) != 0)
    {
        return;
    }

    KIRQL oldIrql;
    KeAcquireSpinLock(&m_Lock, &oldIrql);

    if (m_Buffer == NULL)
    {
        KeReleaseSpinLock(&m_Lock, oldIrql);
        return;
    }

    ULONGLONG avail = m_WritePos - m_ReadPos;
    if (len > (ULONGLONG)(PFEED_RING_BYTES - avail))
    {
        // Кольцо заполнено — блок отбрасывается целиком (счётчик переполнения).
        m_Dropped += len;
        KeReleaseSpinLock(&m_Lock, oldIrql);
        return;
    }

    ULONG writeOffset = (ULONG)(m_WritePos % PFEED_RING_BYTES);
    ULONG firstChunk = PFEED_RING_BYTES - writeOffset;
    if (firstChunk > len)
    {
        firstChunk = len;
    }
    RtlCopyMemory(m_Buffer + writeOffset, src, firstChunk);
    if (len > firstChunk)
    {
        RtlCopyMemory(m_Buffer, src + firstChunk, len - firstChunk);
    }

    m_WritePos += len;
    m_Written += len;

    KeReleaseSpinLock(&m_Lock, oldIrql);
}

void CParrhesiaFeed::NoteHandleCreate(VOID *fileObject, BOOLEAN writer)
{
    if (!writer || fileObject == NULL)
    {
        return; // read-only хэндл (диагностика) кабель не замирает
    }

    KIRQL oldIrql;
    KeAcquireSpinLock(&m_Lock, &oldIrql);

    BOOLEAN known = FALSE;
    LONG free = -1;
    for (LONG i = 0; i < MaxWriterHandles; i++)
    {
        if (m_WriterFO[i] == fileObject)
        {
            known = TRUE;
            break;
        }

        if (m_WriterFO[i] == NULL && free < 0)
        {
            free = i;
        }
    }

    if (!known && free >= 0)
    {
        // Переполнение слота не поднимает Writers: лучше оставить кабель
        // активным при экзотических >8 параллельных писателях, чем получить
        // залипший флаг без пары для декремента.
        m_WriterFO[free] = fileObject;
        m_Writers++;
    }

    KeReleaseSpinLock(&m_Lock, oldIrql);
}

void CParrhesiaFeed::NoteHandleCleanup(VOID *fileObject)
{
    if (fileObject == NULL)
    {
        return;
    }

    KIRQL oldIrql;
    KeAcquireSpinLock(&m_Lock, &oldIrql);
    for (LONG i = 0; i < MaxWriterHandles; i++)
    {
        if (m_WriterFO[i] == fileObject)
        {
            m_WriterFO[i] = NULL;
            if (m_Writers > 0)
            {
                m_Writers--;
            }

            break;
        }
    }

    KeReleaseSpinLock(&m_Lock, oldIrql);
}

BOOLEAN CParrhesiaFeed::HasWriters()
{
    KIRQL oldIrql;
    KeAcquireSpinLock(&m_Lock, &oldIrql);
    BOOLEAN result = (m_Writers > 0) ? TRUE : FALSE;
    KeReleaseSpinLock(&m_Lock, oldIrql);
    return result;
}

// Кабель In→Out: рендер-сэмплы в формате потока → PCM32 в кольцо.
// src/len — источник (битность bitsPerSample, каналы уже проверены
// вызывающим: PFEED_RATE/PFEED_CHANNELS). Конверт побайтовый в кольцо
// (без промежуточного буфера), знаковое расширение:16→<<16,24→<<8,32→as-is.
void CParrhesiaFeed::WriteCable(const BYTE *src, ULONG len, ULONG bitsPerSample)
{
    if (src == NULL || len == 0 ||
        (bitsPerSample != 16 && bitsPerSample != 24 && bitsPerSample != 32))
    {
        return;
    }

    ULONG srcFrameBytes = PFEED_CHANNELS * (bitsPerSample / 8);
    if ((len % srcFrameBytes) != 0)
    {
        return;
    }

    ULONG samples = len / srcFrameBytes;
    ULONG outBytes = samples * PFEED_FRAME_BYTES;
    if (outBytes == 0)
    {
        return;
    }

    KIRQL oldIrql;
    KeAcquireSpinLock(&m_Lock, &oldIrql);

    if (m_Buffer == NULL)
    {
        KeReleaseSpinLock(&m_Lock, oldIrql);
        return;
    }

    // Водяной знак: уровень выше капа (~85 мс) — блок отбрасывается,
    // задержка кабеля не растёт безгранично (см. PFEED_CABLE_HIGH_BYTES).
    ULONGLONG levelNow = m_WritePos - m_ReadPos;
    if (levelNow >= PFEED_CABLE_HIGH_BYTES)
    {
        m_CableDrop += outBytes;
        KeReleaseSpinLock(&m_Lock, oldIrql);
        return;
    }

    ULONGLONG avail = m_WritePos - m_ReadPos;
    if (outBytes > (ULONGLONG)(PFEED_RING_BYTES - avail))
    {
        // Кольцо заполнено (драйвер-источник быстрее захвата) — дроп.
        m_Dropped += outBytes;
        KeReleaseSpinLock(&m_Lock, oldIrql);
        return;
    }

    ULONG writeOffset = (ULONG)(m_WritePos % PFEED_RING_BYTES);
    const BYTE *s = src;

    for (ULONG i = 0; i < samples; i++, s += srcFrameBytes)
    {
        LONG sample;
        if (bitsPerSample == 16)
        {
            sample = ((const SHORT *)s)[0];
            sample <<= 16;
        }
        else if (bitsPerSample == 24)
        {
            sample = (LONG)(s[0] | ((ULONG)s[1] << 8) | ((ULONG)s[2] << 16));
            if (sample & 0x00800000)
            {
                sample |= (LONG)0xFF000000;
            }
            sample <<= 8;
        }
        else //32
        {
            sample = ((const LONG *)s)[0];
        }

        // Побайтовая запись в кольцо с оборотом (кадр8 байт).
        ULONG pos = writeOffset;
        for (ULONG b = 0; b < PFEED_FRAME_BYTES; b++)
        {
            m_Buffer[pos] = ((const BYTE *)&sample)[b];
            pos++;
            if (pos == PFEED_RING_BYTES)
            {
                pos = 0;
            }
        }
        writeOffset = pos;
    }

    m_WritePos += outBytes;
    m_Loop += outBytes;

    KeReleaseSpinLock(&m_Lock, oldIrql);
}

void CParrhesiaFeed::Read(BYTE *dst, ULONG len)
{
    if (len == 0)
    {
        return;
    }

    KIRQL oldIrql;
    KeAcquireSpinLock(&m_Lock, &oldIrql);

    if (m_Buffer == NULL)
    {
        KeReleaseSpinLock(&m_Lock, oldIrql);
        RtlZeroMemory(dst, len);
        return;
    }

    ULONGLONG avail = m_WritePos - m_ReadPos;
    ULONG available = (avail >= len) ? len : (ULONG)avail;

    if (available < len)
    {
        // Данных не хватило — остаток тишиной, позиция чтения догоняет.
        m_Underrun += (len - available);
    }

    if (available > 0)
    {
        ULONG readOffset = (ULONG)(m_ReadPos % PFEED_RING_BYTES);
        ULONG firstChunk = PFEED_RING_BYTES - readOffset;
        if (firstChunk > available)
        {
            firstChunk = available;
        }
        RtlCopyMemory(dst, m_Buffer + readOffset, firstChunk);
        if (available > firstChunk)
        {
            RtlCopyMemory(dst + firstChunk, m_Buffer, available - firstChunk);
        }
        m_ReadPos += available;
    }

    if (available < len)
    {
        // Недостача — тишиной. Позиция чтения НЕ обгоняет фронт записи:
        // иначе уровень (WritePos-ReadPos) уходит в минус, watermark в
        // WriteCable видит ULONGLONG-underflow как «переполнение» и
        // дропает весь вход (журнал M-волны: «сброшено по капу»).
        RtlZeroMemory(dst + available, len - available);
        ULONGLONG target = m_ReadPos + (len - available);
        m_ReadPos = (target > m_WritePos) ? m_WritePos : target;
    }

    m_Delivered += len;

    KeReleaseSpinLock(&m_Lock, oldIrql);
}

BOOLEAN CParrhesiaFeed::TryClaim(const void *owner)
{
    KIRQL oldIrql;
    KeAcquireSpinLock(&m_Lock, &oldIrql);

    BOOLEAN claimed;
    if (m_Buffer == NULL)
    {
        claimed = FALSE;
    }
    else if (m_Owner == NULL || m_Owner == owner)
    {
        if (m_Owner == NULL)
        {
            // Новый владелец начинает со свежих данных (устаревшие не отдаём).
            m_ReadPos = m_WritePos;
        }
        m_Owner = owner;
        claimed = TRUE;
    }
    else
    {
        claimed = FALSE;
    }

    KeReleaseSpinLock(&m_Lock, oldIrql);
    return claimed;
}

void CParrhesiaFeed::Release(const void *owner)
{
    KIRQL oldIrql;
    KeAcquireSpinLock(&m_Lock, &oldIrql);
    if (m_Owner == owner)
    {
        m_Owner = NULL;
    }
    KeReleaseSpinLock(&m_Lock, oldIrql);
}

BOOLEAN CParrhesiaFeed::IsClaimedBy(const void *owner)
{
    KIRQL oldIrql;
    KeAcquireSpinLock(&m_Lock, &oldIrql);
    BOOLEAN result = (m_Owner == owner) ? TRUE : FALSE;
    KeReleaseSpinLock(&m_Lock, oldIrql);
    return result;
}

void CParrhesiaFeed::GetStats(PPFEED_STATS stats)
{
    KIRQL oldIrql;
    KeAcquireSpinLock(&m_Lock, &oldIrql);
    stats->WrittenBytes = m_Written;
    stats->DeliveredBytes = m_Delivered;
    stats->DroppedBytes = m_Dropped;
    stats->UnderrunBytes = m_Underrun;
    stats->ReaderActive = (m_Owner != NULL) ? 1 : 0;
    stats->FormatMismatch = m_FormatMismatch;
    stats->LoopBytes = m_Loop;
    stats->Writers = m_Writers;
    stats->CableDropped = m_CableDrop;
    stats->LevelBytes = (m_WritePos >= m_ReadPos) ? (m_WritePos - m_ReadPos) : 0;
    KeReleaseSpinLock(&m_Lock, oldIrql);
}

//=============================================================================
// Слоты инстансов
//=============================================================================

static INT FeedIndexOfDevice(_In_ PDEVICE_OBJECT DeviceObject)
{
    if (DeviceObject == NULL)
    {
        return -1;
    }

    for (INT i = 0; i < PFEED_MAX_INSTANCES; i++)
    {
        if (g_Feeds[i].Used && g_Feeds[i].Device == DeviceObject)
        {
            return i;
        }
    }

    return -1;
}

CParrhesiaFeed* Feed_At(_In_ INT index)
{
    if (index < 0 || index >= PFEED_MAX_INSTANCES || !g_Feeds[index].Used)
    {
        return NULL;
    }

    return &g_Feeds[index].Feed;
}

#pragma code_seg("PAGE")
NTSTATUS Feed_CreateInstance(
    _In_ PDRIVER_OBJECT DriverObject,
    _In_ const WCHAR* suffix,
    _Out_ INT* outIndex)
{
    PAGED_CODE();

    *outIndex = -1;
    if (suffix == NULL || *suffix == L'\0' || DriverObject == NULL)
    {
        return STATUS_INVALID_PARAMETER;
    }

    INT slot = -1;
    for (INT i = 0; i < PFEED_MAX_INSTANCES; i++)
    {
        if (!g_Feeds[i].Used)
        {
            slot = i;
            break;
        }
    }

    if (slot < 0)
    {
        DPF(D_ERROR, ("[Feed] нет свободных слотов инстансов"));
        return STATUS_INSUFFICIENT_RESOURCES;
    }

    FEED_SLOT* s = &g_Feeds[slot];

    NTSTATUS status = s->Feed.Init();
    if (!NT_SUCCESS(status))
    {
        DPF(D_ERROR, ("[Feed] Init failed, status = %X", status));
        return status;
    }

    status = RtlStringCchPrintfW(
        s->DeviceNameBuffer, PFEED_NAME_CCH, L"\\Device\\ParrhesiaFeed_%s", suffix);
    if (NT_SUCCESS(status))
    {
        status = RtlStringCchPrintfW(
            s->SymlinkBuffer, PFEED_NAME_CCH, L"\\DosDevices\\ParrhesiaFeed_%s", suffix);
    }

    if (!NT_SUCCESS(status))
    {
        DPF(D_ERROR, ("[Feed] имя инстанса не построено, status = %X", status));
        s->Feed.Free();
        return status;
    }

    RtlInitUnicodeString(&s->DeviceName, s->DeviceNameBuffer);
    RtlInitUnicodeString(&s->Symlink, s->SymlinkBuffer);

    status = IoCreateDevice(
        DriverObject,
        0,
        &s->DeviceName,
        FILE_DEVICE_UNKNOWN,
        FILE_DEVICE_SECURE_OPEN,
        FALSE,
        &s->Device);
    if (!NT_SUCCESS(status))
    {
        DPF(D_ERROR, ("[Feed] IoCreateDevice(%ws) failed, status = %X", s->DeviceNameBuffer, status));
        s->Feed.Free();
        s->Device = NULL;
        return status;
    }

    status = IoCreateSymbolicLink(&s->Symlink, &s->DeviceName);
    if (!NT_SUCCESS(status))
    {
        DPF(D_ERROR, ("[Feed] IoCreateSymbolicLink(%ws) failed, status = %X", s->SymlinkBuffer, status));
        IoDeleteDevice(s->Device);
        s->Device = NULL;
        s->Feed.Free();
        return status;
    }

    s->SymlinkCreated = TRUE;
    s->Used = TRUE;
    s->Device->Flags |= DO_BUFFERED_IO;
    s->Device->Flags &= ~DO_DEVICE_INITIALIZING;

    *outIndex = slot;
    DPF(D_TERSE, ("[Feed] instance ready: %ws (slot %d)", s->SymlinkBuffer, slot));
    return STATUS_SUCCESS;
}

void Feed_DestroyInstance(_In_ INT index)
{
    PAGED_CODE();

    if (index < 0 || index >= PFEED_MAX_INSTANCES || !g_Feeds[index].Used)
    {
        return;
    }

    FEED_SLOT* s = &g_Feeds[index];
    if (s->SymlinkCreated)
    {
        IoDeleteSymbolicLink(&s->Symlink);
        s->SymlinkCreated = FALSE;
    }

    if (s->Device != NULL)
    {
        IoDeleteDevice(s->Device);
        s->Device = NULL;
    }

    s->Feed.Free();
    s->Used = FALSE;
    DPF(D_TERSE, ("[Feed] instance destroyed: slot %d", index));
}
#pragma code_seg()

#pragma code_seg("PAGE")
void Feed_DiagSet(_In_ PDEVICE_OBJECT DeviceObject, _In_ ULONG Bit)
{
    PAGED_CODE();

    if (DeviceObject == NULL || Bit == 0)
    {
        return;
    }

    // Только сервис-ключ: software key устройства недоступен из этого
    // контекста (эмпирически IoOpenDeviceRegistryKey отказывает; журнал М2).
    // Формат: S<ptr-low>_<bit> = 1.
    DiagLogToService(
        L"S%x_%x",
        (ULONG)((ULONG_PTR)DeviceObject & 0xFFFFFFFFu),
        Bit,
        1);
}

#pragma code_seg("PAGE")
void Feed_DiagSetStatus(_In_ PDEVICE_OBJECT DeviceObject, _In_ NTSTATUS Status)
{
    PAGED_CODE();

    if (DeviceObject == NULL)
    {
        return;
    }

    // Только сервис-ключ (см. Feed_DiagSet): F<ptr-low> = NTSTATUS.
    DiagLogToService(
        L"F%x",
        (ULONG)((ULONG_PTR)DeviceObject & 0xFFFFFFFFu),
        0,
        (DWORD)Status);
}

#pragma code_seg("PAGE")
void Feed_DiagLog(_In_ const WCHAR* Tag)
{
    PAGED_CODE();

    if (Tag == NULL)
    {
        return;
    }

    WCHAR name[128];
    if (!NT_SUCCESS(RtlStringCchPrintfW(name, 128, L"T_%s", Tag)))
    {
        return;
    }

    UNICODE_STRING valueName;
    RtlInitUnicodeString(&valueName, name);

    UNICODE_STRING path;
    RtlInitUnicodeString(&path,
        L"\\Registry\\Machine\\System\\CurrentControlSet\\Services\\VirtualAudioDriver");
    OBJECT_ATTRIBUTES oa;
    InitializeObjectAttributes(&oa, &path, OBJ_CASE_INSENSITIVE | OBJ_KERNEL_HANDLE, NULL, NULL);

    HANDLE key = NULL;
    if (!NT_SUCCESS(ZwOpenKey(&key, KEY_SET_VALUE, &oa)))
    {
        return;
    }

    DWORD one = 1;
    ZwSetValueKey(key, &valueName, 0, REG_DWORD, &one, sizeof(one));
    ZwClose(key);
}

// Пишет DWORD-значение в сервис-ключ (дубль device-ключа диагностики).
static void DiagLogToService(_In_ const WCHAR* NameFormat, _In_ ULONG Arg1, _In_ ULONG Arg2, _In_ DWORD Value)
{
    WCHAR name[64];
    if (!NT_SUCCESS(RtlStringCchPrintfW(name, 64, NameFormat, Arg1, Arg2)))
    {
        return;
    }

    UNICODE_STRING valueName;
    RtlInitUnicodeString(&valueName, name);

    UNICODE_STRING path;
    RtlInitUnicodeString(&path,
        L"\\Registry\\Machine\\System\\CurrentControlSet\\Services\\VirtualAudioDriver");
    OBJECT_ATTRIBUTES oa;
    InitializeObjectAttributes(&oa, &path, OBJ_CASE_INSENSITIVE | OBJ_KERNEL_HANDLE, NULL, NULL);

    HANDLE key = NULL;
    if (!NT_SUCCESS(ZwOpenKey(&key, KEY_SET_VALUE, &oa)))
    {
        return;
    }

    ZwSetValueKey(key, &valueName, 0, REG_DWORD, &Value, sizeof(Value));
    ZwClose(key);
}

#pragma code_seg("PAGE")
void Feed_DiagLogString(_In_ const WCHAR* Name, _In_ const WCHAR* Value, _In_ ULONG Chars)
{
    PAGED_CODE();

    if (Name == NULL || Value == NULL)
    {
        return;
    }

    UNICODE_STRING valueName;
    RtlInitUnicodeString(&valueName, Name);

    UNICODE_STRING path;
    RtlInitUnicodeString(&path,
        L"\\Registry\\Machine\\System\\CurrentControlSet\\Services\\VirtualAudioDriver");
    OBJECT_ATTRIBUTES oa;
    InitializeObjectAttributes(&oa, &path, OBJ_CASE_INSENSITIVE | OBJ_KERNEL_HANDLE, NULL, NULL);

    HANDLE key = NULL;
    if (!NT_SUCCESS(ZwOpenKey(&key, KEY_SET_VALUE, &oa)))
    {
        return;
    }

    ZwSetValueKey(key, &valueName, 0, REG_SZ, (PVOID)Value, Chars * sizeof(WCHAR));
    ZwClose(key);
}

//=============================================================================
// Control-устройство: диспетчеры
//=============================================================================

#pragma code_seg("PAGE")
static NTSTATUS FeedCreateClose(_In_ PDEVICE_OBJECT DeviceObject, _In_ PIRP Irp)
{
    PAGED_CODE();

    const INT feedIndex = FeedIndexOfDevice(DeviceObject);
    if (feedIndex >= 0)
    {
        const UCHAR major = IoGetCurrentIrpStackLocation(Irp)->MajorFunction;
        if (major == IRP_MJ_CREATE)
        {
            // Kабель уступает только WRITE-хэндлам (приложение-писатель);
            // read-only (диагностика UI) кабель НЕ замирает — M-волна.
            PIO_STACK_LOCATION irpSp = IoGetCurrentIrpStackLocation(Irp);
            const ACCESS_MASK access = irpSp->Parameters.Create.SecurityContext->DesiredAccess;
            const BOOLEAN writer = (access & (FILE_WRITE_DATA | FILE_APPEND_DATA | GENERIC_WRITE)) != 0;
            g_Feeds[feedIndex].Feed.NoteHandleCreate(irpSp->FileObject, writer);
        }
        // Декремент — в CLEANUP (ровно один раз на хэндл, до CLOSE).

        Irp->IoStatus.Status = STATUS_SUCCESS;
        Irp->IoStatus.Information = 0;
        IoCompleteRequest(Irp, IO_NO_INCREMENT);
        return STATUS_SUCCESS;
    }

    // Один шаблон на CREATE и CLOSE: выбираем оригинал по фактическому коду.
    if (IoGetCurrentIrpStackLocation(Irp)->MajorFunction == IRP_MJ_CLOSE)
    {
        return g_OrigClose(DeviceObject, Irp);
    }
    return g_OrigCreate(DeviceObject, Irp);
}
#pragma code_seg()

#pragma code_seg("PAGE")
static NTSTATUS FeedCleanup(_In_ PDEVICE_OBJECT DeviceObject, _In_ PIRP Irp)
{
    PAGED_CODE();

    const INT feedIndex = FeedIndexOfDevice(DeviceObject);
    if (feedIndex >= 0)
    {
        // Ровно один CLEANUP на хэндл — здесь снимаем write-учёт (идемпотентно).
        g_Feeds[feedIndex].Feed.NoteHandleCleanup(
            IoGetCurrentIrpStackLocation(Irp)->FileObject);
        Irp->IoStatus.Status = STATUS_SUCCESS;
        Irp->IoStatus.Information = 0;
        IoCompleteRequest(Irp, IO_NO_INCREMENT);
        return STATUS_SUCCESS;
    }

    return g_OrigCleanup(DeviceObject, Irp);
}
#pragma code_seg()

#pragma code_seg("PAGE")
static NTSTATUS FeedDeviceControl(_In_ PDEVICE_OBJECT DeviceObject, _In_ PIRP Irp)
{
    PAGED_CODE();

    const INT feedIndex = FeedIndexOfDevice(DeviceObject);
    if (feedIndex < 0)
    {
        // KS- и прочие IOCTL идут в FDO — передаём PortCls без изменений.
        return g_OrigDeviceControl(DeviceObject, Irp);
    }

    CParrhesiaFeed* feed = &g_Feeds[feedIndex].Feed;
    PIO_STACK_LOCATION irpSp = IoGetCurrentIrpStackLocation(Irp);
    NTSTATUS status = STATUS_INVALID_DEVICE_REQUEST;
    ULONG_PTR information = 0;

    switch (irpSp->Parameters.DeviceIoControl.IoControlCode)
    {
    case IOCTL_PFEED_WRITE:
    {
        ULONG inLen = irpSp->Parameters.DeviceIoControl.InputBufferLength;
        PVOID buffer = Irp->AssociatedIrp.SystemBuffer;

        if (buffer == NULL || inLen == 0)
        {
            status = STATUS_INVALID_PARAMETER;
        }
        else if ((inLen % PFEED_FRAME_BYTES) != 0)
        {
            status = STATUS_INVALID_PARAMETER;
        }
        else
        {
            feed->Write((const BYTE *)buffer, inLen);
            status = STATUS_SUCCESS;
            information = inLen;
        }
        break;
    }

    case IOCTL_PFEED_GET_STATS:
    {
        ULONG outLen = irpSp->Parameters.DeviceIoControl.OutputBufferLength;
        PVOID buffer = Irp->AssociatedIrp.SystemBuffer;

        if (buffer == NULL || outLen < sizeof(PFEED_STATS))
        {
            status = STATUS_BUFFER_TOO_SMALL;
        }
        else
        {
            PFEED_STATS stats = {};
            feed->GetStats(&stats);
            RtlCopyMemory(buffer, &stats, sizeof(stats));
            status = STATUS_SUCCESS;
            information = sizeof(stats);
        }
        break;
    }

    default:
        // Не наш код — возможно, PortCls ждёт свой IOCTL на другом DO.
        return g_OrigDeviceControl(DeviceObject, Irp);
    }

    Irp->IoStatus.Status = status;
    Irp->IoStatus.Information = information;
    IoCompleteRequest(Irp, IO_NO_INCREMENT);
    return status;
}
#pragma code_seg()

//=============================================================================
// Feed_Initialize / Feed_Cleanup
//=============================================================================

#pragma code_seg("PAGE")
NTSTATUS Feed_Initialize(_In_ PDRIVER_OBJECT DriverObject)
{
    PAGED_CODE();

    // Устройства инстансов создаёт адаптер (Feed_CreateInstance);
    // здесь — только заворачивание диспетчера поверх PortCls.
    if (g_FeedDispatchHooked)
    {
        return STATUS_SUCCESS;
    }

    g_OrigCreate = DriverObject->MajorFunction[IRP_MJ_CREATE];
    g_OrigCleanup = DriverObject->MajorFunction[IRP_MJ_CLEANUP];
    g_OrigClose = DriverObject->MajorFunction[IRP_MJ_CLOSE];
    g_OrigDeviceControl = DriverObject->MajorFunction[IRP_MJ_DEVICE_CONTROL];

    DriverObject->MajorFunction[IRP_MJ_CREATE] = FeedCreateClose;
    DriverObject->MajorFunction[IRP_MJ_CLOSE] = FeedCreateClose;
    DriverObject->MajorFunction[IRP_MJ_CLEANUP] = FeedCleanup;
    DriverObject->MajorFunction[IRP_MJ_DEVICE_CONTROL] = FeedDeviceControl;
    g_FeedDispatchHooked = TRUE;

    DPF(D_TERSE, ("[Feed] dispatch hooked (instances: per-adapter)"));
    return STATUS_SUCCESS;
}
#pragma code_seg()

#pragma code_seg("PAGE")
void Feed_Cleanup()
{
    PAGED_CODE();

    for (INT i = 0; i < PFEED_MAX_INSTANCES; i++)
    {
        Feed_DestroyInstance(i);
    }
}
#pragma code_seg()
