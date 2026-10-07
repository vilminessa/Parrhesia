using Parrhesia.Audio.Engine;

namespace Parrhesia.Audio.Tests.Engine;

/// <summary>
/// Линейный ресемплер: длина/частота/тишина/непрерывность фазы — контракт адаптации
/// источника к формату движка (Ф6-B1).
/// </summary>
public class LinearResamplerTests
{
    [Fact]
    public void Upsample_44100_To_48000_OutputLengthMatchesRatio()
    {
        var resampler = new LinearResampler(44100, 48000, 2);
        var produced = ProcessInChunks(resampler, new float[44100 * 2], 441) / 2;

        // Идеал 48000 (1 с), хвост ≤1 кадра ждёт следующего блока.
        Assert.InRange(produced, 48000 - 3, 48000 + 3);
    }

    [Fact]
    public void Downsample_48000_To_44100_OutputLengthMatchesRatio()
    {
        var resampler = new LinearResampler(48000, 44100, 2);
        var produced = ProcessInChunks(resampler, new float[48000 * 2], 480) / 2;

        Assert.InRange(produced, 44100 - 3, 44100 + 3);
    }

    [Fact]
    public void Sine_FrequencySurvives_44100_To_48000()
    {
        const int sourceRate = 44100;
        const int targetRate = 48000;
        const float frequency = 440f;

        var input = new float[sourceRate];
        for (var i = 0; i < input.Length; i++)
        {
            input[i] = MathF.Sin(2 * MathF.PI * frequency * i / sourceRate);
        }

        var resampler = new LinearResampler(sourceRate, targetRate, 1);
        var output = new List<float>();
        ProcessInChunks(resampler, input, output, 441);

        // Пересчёты по середине записи: частота не должна сместиться.
        var start = output.Count / 4;
        var end = output.Count * 3 / 4;
        var crossings = 0;
        for (var i = start + 1; i < end; i++)
        {
            if (output[i - 1] < 0 && output[i] >= 0)
            {
                crossings++;
            }
        }

        var duration = (end - start) / (double)targetRate;
        var measured = crossings / duration;
        Assert.InRange(measured, frequency - 5, frequency + 5);
    }

    [Fact]
    public void Silence_In_Silence_Out()
    {
        var resampler = new LinearResampler(44100, 48000, 2);
        var output = new List<float>();
        ProcessInChunks(resampler, new float[8820], output, 441);

        Assert.NotEmpty(output);
        Assert.All(output, sample => Assert.Equal(0f, sample));
    }

    [Fact]
    public void Output_IsAlwaysFinite()
    {
        var input = new float[4800];
        var random = new Random(1234);
        for (var i = 0; i < input.Length; i++)
        {
            input[i] = (float)(random.NextDouble() * 8 - 4); // с запасом за пределами ±1
        }

        var resampler = new LinearResampler(44100, 48000, 2);
        var output = new List<float>();
        ProcessInChunks(resampler, input, output, 240);

        Assert.NotEmpty(output);
        Assert.All(output, sample => Assert.True(float.IsFinite(sample), $"не-финит: {sample}"));
    }

    [Fact]
    public void ChunkBoundaries_DoNotChangeOutput()
    {
        var input = new float[2000];
        for (var i = 0; i < input.Length; i++)
        {
            input[i] = MathF.Sin(2 * MathF.PI * 5f * i / input.Length);
        }

        var whole = new LinearResampler(44100, 48000, 2);
        var wholeOutput = new List<float>();
        ProcessChunk(whole, input, wholeOutput);

        var chunked = new LinearResampler(44100, 48000, 2);
        var chunkedOutput = new List<float>();
        ProcessInChunks(chunked, input, chunkedOutput, 37);

        // Одна фаза: нарезка на пакеты не даёт ни сдвига, ни рассинхрона длины.
        Assert.True(
            Math.Abs(wholeOutput.Count - chunkedOutput.Count) <= 2,
            $"длины: {wholeOutput.Count} vs {chunkedOutput.Count}");
        var comparable = Math.Min(wholeOutput.Count, chunkedOutput.Count);
        for (var i = 0; i < comparable; i++)
        {
            Assert.True(
                Math.Abs(wholeOutput[i] - chunkedOutput[i]) < 1e-5,
                $"расхождение на сэмпле {i}: {wholeOutput[i]} vs {chunkedOutput[i]}");
        }
    }

    [Fact]
    public void DestinationTooSmall_Throws()
    {
        var resampler = new LinearResampler(44100, 48000, 2);
        Assert.Throws<ArgumentException>(() => resampler.Process(new float[882], new float[8]));
    }

    private static int ProcessChunk(LinearResampler resampler, ReadOnlySpan<float> input, List<float> output)
    {
        var destination = new float[resampler.MaxOutputSamples(input.Length)];
        var written = resampler.Process(input, destination);
        output.AddRange(destination.AsSpan(0, written).ToArray());
        return written;
    }

    private static int ProcessInChunks(LinearResampler resampler, float[] input, int chunkFrames)
    {
        var total = 0;
        var channels = resampler.Channels;
        for (var offset = 0; offset < input.Length; offset += chunkFrames * channels)
        {
            var frames = Math.Min(chunkFrames, (input.Length - offset) / channels);
            total += ProcessChunk(resampler, input.AsSpan(offset, frames * channels), new List<float>());
        }

        return total;
    }

    private static void ProcessInChunks(LinearResampler resampler, float[] input, List<float> output, int chunkFrames)
    {
        var channels = resampler.Channels;
        for (var offset = 0; offset < input.Length; offset += chunkFrames * channels)
        {
            var frames = Math.Min(chunkFrames, (input.Length - offset) / channels);
            ProcessChunk(resampler, input.AsSpan(offset, frames * channels), output);
        }
    }
}
