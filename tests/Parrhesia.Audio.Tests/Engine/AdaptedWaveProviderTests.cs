using System.Runtime.InteropServices;
using NAudio.Wave;
using Parrhesia.Audio.Engine;

namespace Parrhesia.Audio.Tests.Engine;

/// <summary>Провайдер-адаптер: граф в частоте движка → цельной формат (фид 48k) (Ф6-B3).</summary>
public class AdaptedWaveProviderTests
{
    [Fact]
    public void WaveFormat_IsTargetFormat()
    {
        var inner = new RampProvider(44100, 2);
        var adapter = SourceFormatAdapter.Create(44100, 2, 48000, 2)!;
        var wrapped = new AdaptedWaveProvider(inner, adapter);

        Assert.Equal(48000, wrapped.WaveFormat.SampleRate);
        Assert.Equal(2, wrapped.WaveFormat.Channels);
        Assert.Equal(WaveFormatEncoding.IeeeFloat, wrapped.WaveFormat.Encoding);
    }

    [Fact]
    public void ConstantSizeReads_AreAlwaysFull_AndConsumeProportionalInput()
    {
        // Помпа читает блоки по 10 мс (480 кадров @48k) — каждый Read обязан быть полным,
        // а внутренний граф потреблён в пропорции 44.1k/48k.
        var inner = new RampProvider(44100, 2);
        var adapter = SourceFormatAdapter.Create(44100, 2, 48000, 2)!;
        var wrapped = new AdaptedWaveProvider(inner, adapter);

        var blockBytes = 480 * 2 * sizeof(float);
        var buffer = new byte[blockBytes];
        for (var i = 0; i < 100; i++)
        {
            var read = wrapped.Read(buffer);
            Assert.Equal(blockBytes, read);
        }

        var expectedInnerFrames = 100 * 480 * (44100.0 / 48000.0); // 1 с = 44100 кадров
        // Верхняя граница — «заглядывание» ресемплера: каждый рефилл читает
        // у графа чуть больше нужного (округление + запас), излишек остаётся в пуле.
        Assert.InRange(inner.FramesRead, expectedInnerFrames - 10, expectedInnerFrames + 600);
    }

    [Fact]
    public void Output_IsFiniteAndContinuous()
    {
        var inner = new RampProvider(44100, 2);
        var adapter = SourceFormatAdapter.Create(44100, 2, 48000, 2)!;
        var wrapped = new AdaptedWaveProvider(inner, adapter);

        var buffer = new byte[480 * 2 * sizeof(float)];
        var previous = float.NaN;
        var jumps = 0;
        for (var i = 0; i < 50; i++)
        {
            wrapped.Read(buffer);
            var samples = MemoryMarshal.Cast<byte, float>(buffer);
            foreach (var sample in samples)
            {
                Assert.True(float.IsFinite(sample));
                if (!float.IsNaN(previous) && MathF.Abs(sample - previous) > 4f)
                {
                    jumps++; // линейная лесенка рампа не должна разрываться больше шага
                }

                previous = sample;
            }
        }

        Assert.Equal(0, jumps);
    }

    [Fact]
    public async Task InnerEnding_DoesNotHangAndReturnsShortRead()
    {
        var inner = new FiniteProvider(2000);
        var adapter = SourceFormatAdapter.Create(44100, 2, 48000, 2)!;
        var wrapped = new AdaptedWaveProvider(inner, adapter);

        var buffer = new byte[480 * 2 * sizeof(float)];
        var reads = 0;
        var empty = await Task.Run(() =>
        {
            while (reads < 500)
            {
                if (wrapped.Read(buffer) == 0)
                {
                    return true;
                }

                reads++;
            }

            return false;
        });

        Assert.True(empty, "провайдер так и не кончился — подозрение на зацикливание");
        Assert.True(reads < 500);
    }

    [Fact]
    public void NullArguments_Throw()
    {
        var adapter = SourceFormatAdapter.Create(44100, 2, 48000, 2)!;
        Assert.Throws<ArgumentNullException>(() => new AdaptedWaveProvider(null!, adapter));
        Assert.Throws<ArgumentNullException>(() => new AdaptedWaveProvider(new RampProvider(44100, 2), null!));
    }

    /// <summary>Бесконечный источник: нарастающая «лесенка» в формате 44.1k.</summary>
    private sealed class RampProvider(int sampleRate, int channels) : IWaveProvider
    {
        private float _next;

        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);

        public long FramesRead { get; private set; }

        public int Read(Span<byte> buffer)
        {
            var samples = MemoryMarshal.Cast<byte, float>(buffer);
            for (var i = 0; i < samples.Length; i++)
            {
                samples[i] = _next;
                _next += 0.001f;
                if (_next > 1000f)
                {
                    _next = 0f;
                }
            }

            FramesRead += samples.Length / WaveFormat.Channels;
            return buffer.Length;
        }
    }

    /// <summary>Источник с концом: N сэмплов, затем нули (провайдер «кончился»).</summary>
    private sealed class FiniteProvider(int frames) : IWaveProvider
    {
        private int _remaining = frames * 2;

        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(44100, 2);

        public int Read(Span<byte> buffer)
        {
            if (_remaining <= 0)
            {
                return 0;
            }

            var take = Math.Min(_remaining, buffer.Length / sizeof(float));
            buffer[..(take * sizeof(float))].Clear();
            _remaining -= take;
            return take * sizeof(float);
        }
    }
}
