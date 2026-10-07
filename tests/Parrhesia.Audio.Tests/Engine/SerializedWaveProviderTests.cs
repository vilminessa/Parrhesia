using NAudio.Wave;
using Parrhesia.Audio.Engine;

namespace Parrhesia.Audio.Tests.Engine;

/// <summary>Сериализация параллельных потребителей GraphProcessor (Ф6-B2).</summary>
public class SerializedWaveProviderTests
{
    [Fact]
    public void WaveFormat_Passthrough()
    {
        var expected = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
        var inner = new StubProvider(expected);
        var wrapped = new SerializedWaveProvider(inner, new object());

        Assert.Equal(expected, wrapped.WaveFormat);
    }

    [Fact]
    public async Task ParallelReads_NeverOverlap()
    {
        // Два RT-потребителя (помпа + монитор) на одном GraphProcessor:
        // общий замок обязан сериализовать Read — иначе гонка по буферам ядра.
        var inner = new ConcurrentProbeProvider();
        var gate = new object();
        var first = new SerializedWaveProvider(inner, gate);
        var second = new SerializedWaveProvider(inner, gate);

        var tasks = new[]
        {
            Task.Run(() => ReadLoop(first)),
            Task.Run(() => ReadLoop(second)),
        };
        await Task.WhenAll(tasks);

        Assert.Equal(1, inner.MaxConcurrency);
        Assert.Equal(100, inner.TotalReads);
    }

    [Fact]
    public void NullArguments_Throw()
    {
        Assert.Throws<ArgumentNullException>(() => new SerializedWaveProvider(null!, new object()));
        Assert.Throws<ArgumentNullException>(() => new SerializedWaveProvider(new StubProvider(WaveFormat.CreateIeeeFloatWaveFormat(48000, 2)), null!));
    }

    private static void ReadLoop(IWaveProvider provider)
    {
        var buffer = new byte[1024];
        for (var i = 0; i < 50; i++)
        {
            provider.Read(buffer);
        }
    }

    private sealed class StubProvider(WaveFormat format) : IWaveProvider
    {
        public WaveFormat WaveFormat { get; } = format;

        public int Read(Span<byte> buffer)
        {
            buffer.Clear();
            return buffer.Length;
        }
    }

    /// <summary>Провайдер, фиксирующий максимальное число одновременных Read.</summary>
    private sealed class ConcurrentProbeProvider : IWaveProvider
    {
        private int _current;
        private int _max;
        private int _total;

        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);

        public int MaxConcurrency => Volatile.Read(ref _max);

        public int TotalReads => Volatile.Read(ref _total);

        public int Read(Span<byte> buffer)
        {
            var concurrent = Interlocked.Increment(ref _current);
            var observed = Volatile.Read(ref _max);
            while (concurrent > observed)
            {
                var previous = Interlocked.CompareExchange(ref _max, concurrent, observed);
                if (previous == observed)
                {
                    break;
                }

                observed = previous;
            }

            Thread.Sleep(2); // окно для гонки, если замка нет
            Interlocked.Increment(ref _total);
            Interlocked.Decrement(ref _current);
            buffer.Clear();
            return buffer.Length;
        }
    }
}
