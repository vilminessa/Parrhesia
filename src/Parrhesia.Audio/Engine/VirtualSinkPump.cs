using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.Wave;

namespace Parrhesia.Audio.Engine;

/// <summary>
/// Насос виртуального вывода: снимает блоки с графа (GraphWaveProvider),
/// конвертирует float → PCM32 и пишет в DriverFeed по расписанию
/// с дрейф-компенсацией (целевое время блока нарастает монотонно).
/// Работает до Stop(); поток фоновый.
/// </summary>
public sealed class VirtualSinkPump : IDisposable
{
    /// <summary>Длительность одного блока, мс (100 блоков/с, кольцо драйвера гасит джиттер).</summary>
    public const int BlockMs = 10;

    private readonly IWaveProvider _provider;
    private readonly DriverFeed _feed;
    private readonly float[] _floatBuffer;
    private readonly byte[] _byteBuffer;
    private readonly int _blockFrames;
    private readonly int _blockBytes;

    private Thread? _thread;
    private volatile bool _running;
    private long _blocksWritten;
    private long _writeFailures;
    private long _behindResets;

    public VirtualSinkPump(IWaveProvider provider, DriverFeed feed)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _feed = feed ?? throw new ArgumentNullException(nameof(feed));

        var format = provider.WaveFormat;
        if (format.SampleRate != DriverFeed.Rate || format.Channels != DriverFeed.Channels)
        {
            throw new ArgumentException(
                $"Формат графа ({format.SampleRate} Гц/{format.Channels} к) ≠ формату фида ({DriverFeed.Rate}/{DriverFeed.Channels})");
        }

        _blockFrames = DriverFeed.Rate * BlockMs / 1000;
        _blockBytes = _blockFrames * DriverFeed.FrameBytes;
        _floatBuffer = new float[_blockFrames * DriverFeed.Channels];
        _byteBuffer = new byte[_blockBytes];
    }

    public long BlocksWritten => Interlocked.Read(ref _blocksWritten);
    public long WriteFailures => Interlocked.Read(ref _writeFailures);
    public long BehindResets => Interlocked.Read(ref _behindResets);

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_running)
        {
            return;
        }

        _running = true;
        _thread = new Thread(RunLoop)
        {
            Name = "Parrhesia.VirtualSinkPump",
            IsBackground = true,
            Priority = ThreadPriority.AboveNormal,
        };
        _thread.Start();
    }

    public void Stop()
    {
        _running = false;
        var thread = _thread;
        _thread = null;
        if (thread is { IsAlive: true })
        {
            // Помпа не блокируется в IO — выйти должна почти сразу.
            if (!thread.Join(TimeSpan.FromSeconds(2)))
            {
                // Последний шанс: поток фоновый, процесс доживёт без него.
            }
        }
    }

    private void RunLoop()
    {
        var period = (double)Stopwatch.Frequency * BlockMs / 1000;
        double next = Stopwatch.GetTimestamp();

        while (_running)
        {
            var read = _provider.Read(MemoryMarshal.AsBytes(_floatBuffer.AsSpan()));
            if (read <= 0)
            {
                // Граф ничего не дал — короткая пауза, чтобы не крутиться вхолостую.
                Thread.Sleep(1);
                next += period;
                continue;
            }

            var frames = read / (DriverFeed.Channels * sizeof(float));
            DriverFeed.ConvertBlock(
                _floatBuffer.AsSpan(0, frames * DriverFeed.Channels),
                _byteBuffer.AsSpan(0, frames * DriverFeed.FrameBytes));

            if (!_feed.Write(_byteBuffer, 0, frames * DriverFeed.FrameBytes))
            {
                Interlocked.Increment(ref _writeFailures);
            }
            else
            {
                Interlocked.Increment(ref _blocksWritten);
            }

            // Дрейф-компенсация: целевое время растёт ровно на BlockMs,
            // факт-время определяет паузу; сильное отставание = новый базис.
            next += period;
            var now = Stopwatch.GetTimestamp();
            var behind = next - now;
            if (behind <= 0)
            {
                if (behind < -period * 4)
                {
                    Interlocked.Increment(ref _behindResets);
                    next = now;
                }
                continue;
            }

            var sleepMs = (int)(behind * 1000 / Stopwatch.Frequency);
            if (sleepMs > 0)
            {
                Thread.Sleep(sleepMs);
            }
            else
            {
                Thread.Yield();
            }
        }
    }

    private bool _disposed;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
    }
}
