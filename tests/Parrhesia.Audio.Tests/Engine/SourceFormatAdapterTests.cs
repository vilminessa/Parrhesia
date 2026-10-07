using Parrhesia.Audio.Engine;

namespace Parrhesia.Audio.Tests.Engine;

/// <summary>
/// Адаптер формата источника: замена прежнего «источник пропущен» (Ф6-B1).
/// </summary>
public class SourceFormatAdapterTests
{
    [Fact]
    public void SameFormat_IsPassthrough()
    {
        Assert.Null(SourceFormatAdapter.Create(48000, 2, 48000, 2));
    }

    [Fact]
    public void MismatchedRate_InsteadOfSkip_ReturnsAdapter()
    {
        // Регрессия скипа: 44.1k-источник при движке 48k теперь адаптируется.
        var adapter = SourceFormatAdapter.Create(44100, 2, 48000, 2);

        Assert.NotNull(adapter);
        Assert.Equal(44100, adapter!.SourceSampleRate);
        Assert.Equal(48000, adapter.TargetSampleRate);
    }

    [Fact]
    public void Upsample_LengthMatchesRatio()
    {
        var adapter = SourceFormatAdapter.Create(44100, 2, 48000, 2)!;
        var produced = ProcessInChunks(adapter, new float[44100 * 2], 441) / 2;

        Assert.InRange(produced, 48000 - 3, 48000 + 3);
    }

    [Fact]
    public void MonoToStereo_DuplicatesChannelAndKeepsTimeline()
    {
        var adapter = SourceFormatAdapter.Create(48000, 1, 48000, 2)!;

        var first = new float[100];
        for (var i = 0; i < first.Length; i++)
        {
            first[i] = i;
        }

        var output = new List<float>();
        ProcessChunk(adapter, first, output);
        Assert.Equal(99 * 2, output.Count); // 1 кадр — хвост под следующий блок
        for (var frame = 0; frame < 99; frame++)
        {
            Assert.Equal(frame, output[frame * 2]);
            Assert.Equal(frame, output[frame * 2 + 1]); // L == R
        }

        var second = new float[100];
        for (var i = 0; i < second.Length; i++)
        {
            second[i] = 100 + i;
        }

        ProcessChunk(adapter, second, output);
        Assert.Equal((99 + 100) * 2, output.Count);

        // Таймлайн непрерывен: блок продолжается со значения99 (история), затем 100..198.
        Assert.Equal(99f, output[99 * 2]);
        for (var i = 0; i < 100; i++)
        {
            Assert.Equal(99 + i, output[(99 + i) * 2]);
            Assert.Equal(99 + i, output[(99 + i) * 2 + 1]);
        }
    }

    [Fact]
    public void SixChannelsToStereo_TakesFirstTwo()
    {
        var adapter = SourceFormatAdapter.Create(48000, 6, 48000, 2)!;

        var input = new float[50 * 6];
        for (var frame = 0; frame < 50; frame++)
        {
            for (var channel = 0; channel < 6; channel++)
            {
                input[(frame * 6) + channel] = (frame * 10) + channel;
            }
        }

        var output = new List<float>();
        ProcessChunk(adapter, input, output);

        Assert.Equal(49 * 2, output.Count); // хвост 1 кадр
        for (var frame = 0; frame < 49; frame++)
        {
            Assert.Equal((frame * 10) + 0, output[frame * 2]);
            Assert.Equal((frame * 10) + 1, output[frame * 2 + 1]);
        }
    }

    [Fact]
    public void Silence_AdvancesPhase_SameAsSignal()
    {
        var signal = new SourceFormatAdapterHolder(44100, 1, 48000, 1);
        var sine = new float[44100];
        for (var i = 0; i < sine.Length; i++)
        {
            sine[i] = MathF.Sin(2 * MathF.PI * 440f * i / 44100);
        }

        var withSignal = ProcessInChunks(signal.Adapter, sine, 441);
        withSignal += ProcessInChunks(signal.Adapter, new float[44100], 441);

        var silent = new SourceFormatAdapterHolder(44100, 1, 48000, 1);
        var allSilence = ProcessInChunks(silent.Adapter, new float[88200], 441);

        // Тишинный блок продвигает фазу ровно как звуковой: длина не «уплывает».
        Assert.True(
            Math.Abs(withSignal - allSilence) <= 2,
            $"сигнал+тишина {withSignal} vs одна тишина {allSilence}");
    }

    [Fact]
    public void Mono44100_To_Stereo48000_BothStagesAtOnce()
    {
        var adapter = SourceFormatAdapter.Create(44100, 1, 48000, 2)!;
        var produced = ProcessInChunks(adapter, new float[44100], 441) / 2;

        Assert.InRange(produced, 48000 - 3, 48000 + 3);
    }

    private static int ProcessChunk(SourceFormatAdapter adapter, ReadOnlySpan<float> input, List<float> output)
    {
        var destination = new float[adapter.MaxDestinationSamples(input.Length)];
        var written = adapter.Process(input, destination);
        output.AddRange(destination.AsSpan(0, written).ToArray());
        return written;
    }

    private static int ProcessInChunks(SourceFormatAdapter adapter, float[] input, int chunkFrames)
    {
        var total = 0;
        var sourceChannels = adapter.SourceChannels;
        var output = new List<float>();
        for (var offset = 0; offset < input.Length; offset += chunkFrames * sourceChannels)
        {
            var frames = Math.Min(chunkFrames, (input.Length - offset) / sourceChannels);
            total += ProcessChunk(adapter, input.AsSpan(offset, frames * sourceChannels), output);
        }

        return total;
    }

    /// <summary>Обёртка — просто чтобы держать адаптер вместе с его параметрами в тесте.</summary>
    private sealed class SourceFormatAdapterHolder
    {
        public SourceFormatAdapterHolder(int sourceRate, int sourceChannels, int targetRate, int targetChannels)
        {
            Adapter = SourceFormatAdapter.Create(sourceRate, sourceChannels, targetRate, targetChannels)!;
        }

        public SourceFormatAdapter Adapter { get; }
    }
}
