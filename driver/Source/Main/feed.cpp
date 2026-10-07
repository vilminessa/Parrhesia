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

//=============================================================================
// Globals
//=============================================================================

CParrhesiaFeed g_Feed;

static PDEVICE_OBJECT  g_FeedDevice = NULL;
static UNICODE_STRING  g_FeedSymlink = RTL_CONSTANT_STRING(PFEED_SYMLINK_NAME);
static BOOLEAN         g_FeedSymlinkCreated = FALSE;

// Оригинальные обработчики PortCls (до заворачивания).
static PDRIVER_DISPATCH g_OrigCreate = NULL;
static PDRIVER_DISPATCH g_OrigCleanup = NULL;
static PDRIVER_DISPATCH g_OrigClose = NULL;
static PDRIVER_DISPATCH g_OrigDeviceControl = NULL;

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
    m_Owner = NULL;
    m_FormatMismatch = 0;

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
        RtlZeroMemory(dst + available, len - available);
        m_ReadPos += (len - available);
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
    KeReleaseSpinLock(&m_Lock, oldIrql);
}

//=============================================================================
// Control-устройство: диспетчеры
//=============================================================================

static BOOLEAN IsFeedDevice(_In_ PDEVICE_OBJECT DeviceObject)
{
    return (g_FeedDevice != NULL && DeviceObject == g_FeedDevice) ? TRUE : FALSE;
}

#pragma code_seg("PAGE")
static NTSTATUS FeedCreateClose(_In_ PDEVICE_OBJECT DeviceObject, _In_ PIRP Irp)
{
    PAGED_CODE();

    if (IsFeedDevice(DeviceObject))
    {
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

    if (IsFeedDevice(DeviceObject))
    {
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

    if (!IsFeedDevice(DeviceObject))
    {
        // KS- и прочие IOCTL идут в FDO — передаём PortCls без изменений.
        return g_OrigDeviceControl(DeviceObject, Irp);
    }

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
            g_Feed.Write((const BYTE *)buffer, inLen);
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
            g_Feed.GetStats(&stats);
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

    NTSTATUS status = g_Feed.Init();
    if (!NT_SUCCESS(status))
    {
        DPF(D_ERROR, ("[Feed] Init failed, status = %X", status));
        return status;
    }

    UNICODE_STRING deviceName = RTL_CONSTANT_STRING(PFEED_DEVICE_NAME);
    status = IoCreateDevice(
        DriverObject,
        0,
        &deviceName,
        FILE_DEVICE_UNKNOWN,
        FILE_DEVICE_SECURE_OPEN,
        FALSE,
        &g_FeedDevice);
    if (!NT_SUCCESS(status))
    {
        DPF(D_ERROR, ("[Feed] IoCreateDevice failed, status = %X", status));
        g_Feed.Free();
        g_FeedDevice = NULL;
        return status;
    }

    status = IoCreateSymbolicLink(&g_FeedSymlink, &deviceName);
    if (!NT_SUCCESS(status))
    {
        DPF(D_ERROR, ("[Feed] IoCreateSymbolicLink failed, status = %X", status));
        IoDeleteDevice(g_FeedDevice);
        g_FeedDevice = NULL;
        g_Feed.Free();
        return status;
    }
    g_FeedSymlinkCreated = TRUE;

    // Заворачиваем диспетчеры: свои — только для control-устройства,
    // остальным DO достаётся сохранённый обработчик PortCls.
    g_OrigCreate = DriverObject->MajorFunction[IRP_MJ_CREATE];
    g_OrigCleanup = DriverObject->MajorFunction[IRP_MJ_CLEANUP];
    g_OrigClose = DriverObject->MajorFunction[IRP_MJ_CLOSE];
    g_OrigDeviceControl = DriverObject->MajorFunction[IRP_MJ_DEVICE_CONTROL];

    DriverObject->MajorFunction[IRP_MJ_CREATE] = FeedCreateClose;
    DriverObject->MajorFunction[IRP_MJ_CLOSE] = FeedCreateClose;
    DriverObject->MajorFunction[IRP_MJ_CLEANUP] = FeedCleanup;
    DriverObject->MajorFunction[IRP_MJ_DEVICE_CONTROL] = FeedDeviceControl;

    g_FeedDevice->Flags |= DO_BUFFERED_IO;
    g_FeedDevice->Flags &= ~DO_DEVICE_INITIALIZING;

    DPF(D_TERSE, ("[Feed] control device ready"));
    return STATUS_SUCCESS;
}
#pragma code_seg()

#pragma code_seg("PAGE")
void Feed_Cleanup()
{
    PAGED_CODE();

    if (g_FeedSymlinkCreated)
    {
        IoDeleteSymbolicLink(&g_FeedSymlink);
        g_FeedSymlinkCreated = FALSE;
    }

    if (g_FeedDevice != NULL)
    {
        IoDeleteDevice(g_FeedDevice);
        g_FeedDevice = NULL;
    }

    g_Feed.Free();
}
#pragma code_seg()
