using System.Diagnostics;
using Parrhesia.Audio.Processing;

namespace Parrhesia.Audio.Buffers;

/// <summary>
/// Кольцевой буфер float32 для передачи сэмплов между потоками (SPSC):
/// один пишущий поток (захват) — один читающий (рендер). Без мьютексов:
/// позиции публикуются через Volatile, на x64 запись long атомарна.
/// При нехватке места новые сэмплы отбрасываются, при нехватке данных
/// выдаётся тишина — оба события считаются в статистике.
/// </summary>
public sealed class SampleRing : ISampleInput
{
    /// <summary>
    /// Порог «спячки»: потребитель не читал дольше этого времени (источник
    /// никуда не идёт — нет маршрута до работающего выхода). Пропуски
    /// записи в спящем кольце НЕ считаются xrun — это не деградация тракта,
    /// а неиспользуемый источник (иначе статус копит миллионы «переполнений»).
    /// </summary>
    public const int DefaultSleepAfterMs = 5000;

    private readonly float[] _buffer;
    private readonly int _mask;
    private readonly int _sleepAfterMs;

    private long _write;
    private long _read;
    private long _overflowSamples;
    private long _underrunSamples;
    private long _lastReadTick;

    public SampleRing(int capacitySamples, int sleepAfterMs = DefaultSleepAfterMs)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacitySamples, 2);
        if ((capacitySamples & (capacitySamples - 1)) != 0)
        {
            throw new ArgumentException("Ёмкость должна быть степенью двойки.", nameof(capacitySamples));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(sleepAfterMs, 1);

        _buffer = new float[capacitySamples];
        _mask = capacitySamples - 1;
        _sleepAfterMs = sleepAfterMs;
        _lastReadTick = Environment.TickCount64;
    }

    public int Capacity => _buffer.Length;

    /// <summary>Сэмплов готово к чтению.</summary>
    public int Available => (int)(Volatile.Read(ref _write) - Volatile.Read(ref _read));

    public long OverflowSamples => Volatile.Read(ref _overflowSamples);

    public long UnderrunSamples => Volatile.Read(ref _underrunSamples);

    /// <summary>true — потребитель молчит дольше порога (источник никуда не идёт).</summary>
    public bool Sleeping =>
        Environment.TickCount64 - Volatile.Read(ref _lastReadTick) >= _sleepAfterMs;

    /// <summary>Обнулить счётчики xrun (не трогает данные).</summary>
    public void ResetStatistics()
    {
        Interlocked.Exchange(ref _overflowSamples, 0);
        Interlocked.Exchange(ref _underrunSamples, 0);
    }

    /// <summary>Записывает, сколько влезло. Возвращает число принятых сэмплов.</summary>
    public int Write(ReadOnlySpan<float> source)
    {
        var sleeping = Sleeping; // одна оценка на запись — не смешивать с тиком чтения
        var write = Volatile.Read(ref _write);
        var read = Volatile.Read(ref _read);
        var space = _buffer.Length - (int)(write - read);
        if (space <= 0)
        {
            if (!sleeping)
            {
                Interlocked.Add(ref _overflowSamples, source.Length);
            }

            return 0;
        }

        var count = Math.Min(source.Length, space);
        var start = (int)write & _mask;
        var firstChunk = Math.Min(count, _buffer.Length - start);
        source[..firstChunk].CopyTo(_buffer.AsSpan(start));
        if (count > firstChunk)
        {
            source.Slice(firstChunk, count - firstChunk).CopyTo(_buffer);
        }

        Volatile.Write(ref _write, write + count);
        if (count < source.Length && !sleeping)
        {
            Interlocked.Add(ref _overflowSamples, source.Length - count);
        }

        return count;
    }

    /// <summary>Читает ровно <c>destination.Length</c> сэмплов, остаток — тишина.</summary>
    public int Read(Span<float> destination)
    {
        Volatile.Write(ref _lastReadTick, Environment.TickCount64);

        var read = Volatile.Read(ref _read);
        var write = Volatile.Read(ref _write);
        var available = (int)(write - read);
        var count = Math.Min(destination.Length, available);

        if (count > 0)
        {
            var start = (int)read & _mask;
            var firstChunk = Math.Min(count, _buffer.Length - start);
            _buffer.AsSpan(start, firstChunk).CopyTo(destination);
            if (count > firstChunk)
            {
                _buffer.AsSpan(0, count - firstChunk).CopyTo(destination.Slice(firstChunk));
            }
        }

        if (count < destination.Length)
        {
            destination[count..].Clear();
            Interlocked.Add(ref _underrunSamples, destination.Length - count);
        }

        Volatile.Write(ref _read, read + count);
        return count;
    }
}
