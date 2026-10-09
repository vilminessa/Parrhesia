/*++

Module Name:

    feed.h

Abstract:

    Parrhesia Feed — кольцевой буфер приёма PCM для виртуального микрофона
    (Parrhesia Out). User mode пишет через IOCTL_PFEED_WRITE (METHOD_BUFFERED),
    поток захвата читает в WriteBytes вместо генерации тишины.

    Канонический формат фида: 48000 Гц, 2 канала, PCM signed 32-bit
    (MicArrayPinDataRangesRawStream: MICARRAY_RAW_* = PCM32@48k;
    дефолтный формат пина = MicArrayPinSupportedDeviceFormats[0]).
    Pump в user mode конвертирует float графа в Int32 перед записью.

--*/

#ifndef _PARRHESIA_FEED_H_
#define _PARRHESIA_FEED_H_

// Канонический формат (дублируется в C#: Parrhesia.Audio DriverFeed).
#define PFEED_RATE          48000
#define PFEED_CHANNELS      2
#define PFEED_BITS          32
#define PFEED_FRAME_BYTES   (PFEED_CHANNELS * (PFEED_BITS / 8))   // 8
#define PFEED_BYTES_PER_SEC (PFEED_RATE * PFEED_FRAME_BYTES)       // 384000
#define PFEED_RING_BYTES    (1u << 18)                             // 256 КБ ≈ 0.68 с

// Водяной знак кабельного цикла: при уровне ≥ ~85 мс входящий блок
// отбрасывается (CableDropped) — задержка кабеля каплена, набег уровня
// (дрейф часов render/capture) не превращается в секунды и целоблочные
// дропы посреди звука.
#define PFEED_CABLE_HIGH_BYTES   32768u                            // ≈85 мс @384 КБ/с

#define PFEED_DEVICE_NAME   L"\\Device\\ParrhesiaFeed"
#define PFEED_SYMLINK_NAME  L"\\DosDevices\\ParrhesiaFeed"
#define PFEED_USER_PATH     "\\\\.\\ParrhesiaFeed"

// Инстансы (lanes): каждое устройство адаптера создаёт свой feed
// \\.\ParrhesiaFeed_<suffix> (suffix — instance-id девnode c '\' → '_',
// например ROOT_MEDIA_0001 — глобально уникален).
#define PFEED_MAX_INSTANCES 8
#define PFEED_NAME_CCH      96

#define PFEED_DEVICE_TYPE   FILE_DEVICE_UNKNOWN

#define IOCTL_PFEED_WRITE      CTL_CODE(PFEED_DEVICE_TYPE, 0x800, METHOD_BUFFERED, FILE_WRITE_DATA)
#define IOCTL_PFEED_GET_STATS  CTL_CODE(PFEED_DEVICE_TYPE, 0x801, METHOD_BUFFERED, FILE_READ_DATA)

// Статистика фида. Поля LONG — volatile-счётчики выравнивания под C#-маппинг.
typedef struct _PFEED_STATS
{
    ULONGLONG WrittenBytes;    // принято от user mode (IOCTL_PFEED_WRITE)
    ULONGLONG DeliveredBytes;  // отдано потоку захвата
    ULONGLONG DroppedBytes;    // отброшено: кольцо заполнено
    ULONGLONG UnderrunBytes;   // отдано тишины: данных не хватило
    LONG      ReaderActive;    // 1 — поток захвата держит фид
    LONG      FormatMismatch;  // 1 — формат потока не совпал с каноническим
    ULONGLONG LoopBytes;       // принято кабельным циклом (render → фид)
    LONG      Writers;         // открыто WRITE-хэндлов фида (0 → кабель активен)
    ULONGLONG CableDropped;    // отброшено кабелем: уровень > PFEED_CABLE_HIGH (кап задержки)
    ULONGLONG LevelBytes;      // размер кольца, Б — ПРЯМАЯ задержка кабеля (мс = /384000)
} PFEED_STATS, *PPFEED_STATS;

class CParrhesiaFeed
{
public:
    // Конструктора нет намеренно: глобальные объекты с пользовательским
    // конструктором требуют .CRT-секции (LNK4210 — ошибка для драйверов).
    // Инициализация — в Init().

    NTSTATUS Init();
    void     Free();

    // Приём блока от user mode (PASSIVE_LEVEL). Длина должна быть кратна кадру.
    void Write(_In_reads_bytes_(len) const BYTE *src, _In_ ULONG len);

    // Кабельный цикл (В1): блок render-пина In → кольцо фида → Out. src/len —
    // в формате рендер-потока (bitsPerSample:16/24/32, каналы и частота уже
    // проверены вызывающим против PFEED_*). Конверт в PCM32 на лету, ≤DISPATCH.
    void WriteCable(_In_reads_bytes_(len) const BYTE *src, _In_ ULONG len, _In_ ULONG bitsPerSample);

    // Учёт хэндлов: Writers считается ТОЛЬКО по хэндлам с write-доступом
    // (M-волна) — диагностика открывает фид GENERIC_READ и НЕ замирает кабель.
    // NoteHandleCleanup идемпотентен (CLOSE без CLEANUP-флага — no-op).
    void NoteHandleCreate(_In_ VOID *fileObject, _In_ BOOLEAN writer);
    void NoteHandleCleanup(_In_ VOID *fileObject);

    BOOLEAN HasWriters();

    // Выдача блока потоку захвата (≤ DISPATCH_LEVEL). При нехватке — тишина.
    void Read(_Out_writes_bytes_(len) BYTE *dst, _In_ ULONG len);

    // Захват фида потоком: TRUE — поток может читать (владелец один).
    BOOLEAN TryClaim(_In_ const void *owner);
    void     Release(_In_ const void *owner);

    void GetStats(_Out_ PPFEED_STATS stats);

    BOOLEAN IsClaimedBy(_In_ const void *owner);

private:
    KSPIN_LOCK          m_Lock;
    BYTE               *m_Buffer;
    ULONGLONG           m_WritePos;    // монотонные позиции в байтах
    ULONGLONG           m_ReadPos;
    ULONGLONG           m_Written;
    ULONGLONG           m_Delivered;
    ULONGLONG           m_Dropped;
    ULONGLONG           m_Underrun;
    ULONGLONG           m_Loop;        // байты кабельного цикла (render → фид)
    ULONGLONG           m_CableDrop;   // отброшено кабелем по водяному знаку
    const void         *m_Owner;
    LONG                m_FormatMismatch;
    LONG                m_Writers;     // write-хэндлов (список ниже)

    // FileObject'ы write-хэндлов (для корректного декремента на CLEANUP).
    static const LONG   MaxWriterHandles = 8;
    VOID               *m_WriterFO[MaxWriterHandles];
};

// ===== Инстансы (per-adapter feed; см. М2-lanes) =====

// Создаёт control-устройство \\.\ParrhesiaFeed_<suffix> для адаптера.
// suffix — безопасное имя (без '\'), например "ROOT_MEDIA_0001".
// outIndex — слот (для Feed_At/Feed_DestroyInstance); ошибка не фатальна
// для работы драйвера (фид просто не готов).
NTSTATUS Feed_CreateInstance(
    _In_ PDRIVER_OBJECT DriverObject,
    _In_ const WCHAR* suffix,
    _Out_ INT* outIndex);

// Удаляет устройство/симлинк слота и кольцо. index=-1 — no-op.
void Feed_DestroyInstance(_In_ INT index);

// Кольцо фида по слоту; невалидный index → NULL (вызывающий обязан
// проверять: потоки читают тишину, IOCTL — отклоняется).
CParrhesiaFeed* Feed_At(_In_ INT index);

// Создаёт control-устройство (LEGACY-имя \\.\ParrhesiaFeed) и заворачивает
// диспетчер (CREATE/CLOSE/CLEANUP/DEVICE_CONTROL) с сохранением обработчиков
// PortCls для остальных device object'ов. Ошибка не фатальна (фид не готов).
NTSTATUS Feed_Initialize(_In_ PDRIVER_OBJECT DriverObject);

// Убивает все инстансы (вызывается из DriverUnload).
void Feed_Cleanup();

// ===== Диагностика (софт-ключ устройства: Device Parameters\ParrhesiaDiag) =====
// Битовая маска пройденных этапов StartDevice/Init + статус фида + suffix —
// вскрывает место обрыва без подключённого отладчика (М2/поддержка).
// ДУБЛЬ: те же события пишутся в сервис-ключ
// (Services\VirtualAudioDriver: T_<метка>, S<ptr>_<бит>, F<ptr>=status) —
// переживает отказ device-ключа.
void Feed_DiagSet(_In_ PDEVICE_OBJECT DeviceObject, _In_ ULONG Bit);
void Feed_DiagSetStatus(_In_ PDEVICE_OBJECT DeviceObject, _In_ NTSTATUS Status);

// Метка-событие в сервис-ключ (DriverEntry/AddDevice — точки без DeviceObject).
void Feed_DiagLog(_In_ const WCHAR* Tag);

// Отладочное REG_SZ-значение в сервис-ключ (имя+строка).
void Feed_DiagLogString(_In_ const WCHAR* Name, _In_ const WCHAR* Value, _In_ ULONG Chars);

#endif // _PARRHESIA_FEED_H_
