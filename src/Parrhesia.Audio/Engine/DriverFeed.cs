using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Parrhesia.Audio.Engine;

/// <summary>
/// Статистика control-устройства ParrhesiaFeed (зеркало PFEED_STATS из feed.h).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct ParrhesiaFeedStats
{
    public ulong WrittenBytes;
    public ulong DeliveredBytes;
    public ulong DroppedBytes;
    public ulong UnderrunBytes;
    public int ReaderActive;
    public int FormatMismatch;

    /// <summary>Байты кабельного цикла (render In → фид; В1).</summary>
    public ulong LoopBytes;

    /// <summary>Открытых user mode-хэндлов фида (0 → кабель активен).</summary>
    public int Writers;

    /// <summary>Отброшено кабелем по водяному знаку (кап задержки; В1).</summary>
    public ulong CableDropped;
}

/// <summary>
/// Транспорт до ядро-фида виртуального микрофона
/// (\\.\ParrhesiaFeed_<suffix>; legacy-имя без суффикса — для старых
/// драйверов до переустановки). Пишет PCM-блоки через IOCTL_PFEED_WRITE;
/// драйвер отдаёт их потоку захвата. Канонический формат — PCM signed
/// 32-bit, 48000 Гц, 2 канала (см. feed.h).
/// </summary>
public sealed class DriverFeed : IDisposable
{
    /// <summary>Legacy-путь (драйвер до М2-переустановки).</summary>
    public const string DevicePath = "\\\\.\\ParrhesiaFeed";

    /// <summary>Путь инстанса (lanes): \\.\ParrhesiaFeed_&lt;suffix&gt;.</summary>
    public static string PathFor(string suffix) =>
        "\\\\.\\ParrhesiaFeed_" + suffix;

    /// <summary>Фактический путь этого экземпляра.</summary>
    public string Path { get; private set; }

    /// <summary>Частота фида, Гц (должна совпадать с feed.h).</summary>
    public const int Rate = 48000;

    /// <summary>Каналы фида (должны совпадать с feed.h).</summary>
    public const int Channels = 2;

    /// <summary>Бит на сэмпл (должен совпадать с feed.h).</summary>
    public const int Bits = 32;

    /// <summary>Размер кадра PCM в байтах.</summary>
    public const int FrameBytes = Channels * (Bits / 8);

    private const uint FileDeviceUnknown = 0x00000022;
    private const uint MethodBuffered = 0;
    private const uint FileWriteData = 0x0002;
    private const uint FileReadData = 0x0001;

    private static readonly uint IoctlWrite = CtlCode(FileDeviceUnknown, 0x800, MethodBuffered, FileWriteData);
    private static readonly uint IoctlGetStats = CtlCode(FileDeviceUnknown, 0x801, MethodBuffered, FileReadData);

    private SafeFileHandle? _handle;
    private IntPtr _writeBuffer;
    private int _writeBufferSize;

    public bool IsOpen => _handle is { IsInvalid: false, IsClosed: false };

    private static uint CtlCode(uint deviceType, uint function, uint method, uint access) =>
        (deviceType << 16) | (access << 14) | (function << 2) | method;

    /// <summary>suffix — из VirtualEndpointResolver.TryGetFeedSuffix (null → legacy-путь).</summary>
    public DriverFeed(string? suffix = null)
    {
        Path = string.IsNullOrEmpty(suffix) ? DevicePath : PathFor(suffix);
    }

    /// <summary>
    /// Открывает фид: сначала путь инстанса, при отказе — legacy
    /// (драйвер ещё не переустановлен после М2). Бросает
    /// <see cref="IOException"/> с текстом ошибки Win32, если не открылся ни один.
    /// </summary>
    public void Open()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsOpen)
        {
            return;
        }

        // Суффиксный путь первым; legacy — второй попыткой (только если отличается).
        var candidates = Path == DevicePath
            ? new[] { DevicePath }
            : new[] { Path, DevicePath };

        IOException? last = null;
        foreach (var candidate in candidates)
        {
            var handle = NativeMethods.CreateFileW(
                candidate,
                NativeMethods.GenericWrite | NativeMethods.GenericRead,
                (uint)FileShare.ReadWrite,
                IntPtr.Zero,
                FileMode.Open,
                0,
                IntPtr.Zero);

            if (!handle.IsInvalid)
            {
                _handle = handle;
                Path = candidate;
                return;
            }

            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            last = new IOException(
                $"Не удалось открыть {candidate} (ошибка {error}) — драйвер Parrhesia не установлен или фид не готов");
        }

        throw last!;
    }

    /// <summary>Пишет блок PCM из массива. Длина кратна кадру. false — фид не открыт.</summary>
    public bool Write(byte[] block, int offset, int count)
    {
        if (!IsOpen || count == 0)
        {
            return false;
        }

        if ((count % FrameBytes) != 0)
        {
            throw new ArgumentException($"Длина блока {count} не кратна кадру {FrameBytes}");
        }

        EnsureWriteBuffer(count);
        Marshal.Copy(block, offset, _writeBuffer, count);
        return NativeMethods.DeviceIoControl(
            _handle!,
            IoctlWrite,
            _writeBuffer,
            (uint)count,
            IntPtr.Zero,
            0,
            out _,
            IntPtr.Zero);
    }

    /// <summary>Читает счётчики драйвера. false — фид не открыт.</summary>
    public bool TryGetStats(out ParrhesiaFeedStats stats)
    {
        stats = default;
        if (!IsOpen)
        {
            return false;
        }

        var size = Marshal.SizeOf<ParrhesiaFeedStats>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            var ok = NativeMethods.DeviceIoControl(
                _handle!,
                IoctlGetStats,
                IntPtr.Zero,
                0,
                buffer,
                (uint)size,
                out var returned,
                IntPtr.Zero);
            if (!ok || returned < size)
            {
                return false;
            }

            stats = Marshal.PtrToStructure<ParrhesiaFeedStats>(buffer);
            return true;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>
    /// Конвертация float-сэмплов графа (−1..1) в PCM signed 32-bit little-endian.
    /// dst.Length должен быть src.Length * 4.
    /// </summary>
    public static void ConvertBlock(ReadOnlySpan<float> src, Span<byte> dst)
    {
        if (dst.Length < src.Length * sizeof(int))
        {
            throw new ArgumentException("Буфер назначения меньше исходного");
        }

        var ints = MemoryMarshal.Cast<byte, int>(dst[..(src.Length * sizeof(int))]);
        for (var i = 0; i < src.Length; i++)
        {
            var sample = src[i];
            if (sample > 1f)
            {
                sample = 1f;
            }
            else if (sample < -1f)
            {
                sample = -1f;
            }

            // -1..1 включительно → Int32; 1f отсекается, чтобы не было переполнения.
            ints[i] = sample >= 1f
                ? int.MaxValue
                : (int)(sample * 2147483648f);
        }
    }

    private void EnsureWriteBuffer(int size)
    {
        if (_writeBuffer != IntPtr.Zero && _writeBufferSize >= size)
        {
            return;
        }

        if (_writeBuffer != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_writeBuffer);
            _writeBuffer = IntPtr.Zero;
        }

        _writeBuffer = Marshal.AllocHGlobal(size);
        _writeBufferSize = size;
    }

    private bool _disposed;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _handle?.Dispose();
        _handle = null;
        if (_writeBuffer != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_writeBuffer);
            _writeBuffer = IntPtr.Zero;
            _writeBufferSize = 0;
        }
    }

    private static class NativeMethods
    {
        public const uint GenericRead = 0x80000000;
        public const uint GenericWrite = 0x40000000;

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern SafeFileHandle CreateFileW(
            string lpFileName,
            uint dwDesiredAccess,
            uint dwShareMode,
            IntPtr lpSecurityAttributes,
            FileMode dwCreationDisposition,
            uint dwFlagsAndAttributes,
            IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DeviceIoControl(
            SafeFileHandle hDevice,
            uint dwIoControlCode,
            IntPtr lpInBuffer,
            uint nInBufferSize,
            IntPtr lpOutBuffer,
            uint nOutBufferSize,
            out uint lpBytesReturned,
            IntPtr lpOverlapped);
    }
}
