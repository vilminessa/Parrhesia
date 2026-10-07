using System.Runtime.InteropServices;
using NAudio.Wave;

namespace Parrhesia.Audio.Engine;

/// <summary>
/// IWaveProvider, пропускающий блоки внутреннего провайдера через
/// <see cref="SourceFormatAdapter"/>: например, граф в частоте движка → фид
/// драйвера (48k). Хвост ресемплера (позиция/история) переживает границы
/// <see cref="Read"/> — в пуле лежат уже выданные, но ещё не потреблённые сэмплы.
/// Не потокобезопасен — наружу ставится <see cref="SerializedWaveProvider"/>.
/// </summary>
public sealed class AdaptedWaveProvider : IWaveProvider
{
    /// <summary>Страховка от зацикливания, если внутренний провайдер отдаёт ноль.</summary>
    private const int MaxRefillIterations = 64;

    private readonly IWaveProvider _inner;
    private readonly SourceFormatAdapter _adapter;
    private readonly int _sourceChannels;

    private float[] _source = [];
    private float[] _pool = [];
    private int _poolSamples;

    public AdaptedWaveProvider(IWaveProvider inner, SourceFormatAdapter adapter)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        _sourceChannels = adapter.SourceChannels;

        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(adapter.TargetSampleRate, adapter.TargetChannels);
    }

    public WaveFormat WaveFormat { get; }

    public int Read(Span<byte> buffer)
    {
        var destinationSamples = buffer.Length / sizeof(float);
        if (destinationSamples == 0)
        {
            return 0;
        }

        var copied = 0;
        var iterations = 0;
        while (copied < destinationSamples)
        {
            if (_poolSamples == 0)
            {
                if (++iterations > MaxRefillIterations || !Refill(destinationSamples - copied))
                {
                    break; // внутренний источник кончился — отдаём что есть
                }
            }

            var take = Math.Min(destinationSamples - copied, _poolSamples);
            MemoryMarshal.AsBytes(_pool.AsSpan(0, take)).CopyTo(buffer[(copied * sizeof(float))..]);
            _pool.AsSpan(take, _poolSamples - take).CopyTo(_pool);
            _poolSamples -= take;
            copied += take;
        }

        return copied * sizeof(float);
    }

    /// <summary>Читает у внутреннего провайдера и наполняет пул адаптированными сэмплами.</summary>
    private bool Refill(int missingSamples)
    {
        // Сколько сэмплов источника нужно, чтобы покрыть недостающие кадры назначения.
        var missingFrames = (missingSamples / WaveFormat.Channels) + 1;
        var sourceFrames = (int)Math.Ceiling(missingFrames * ((double)_adapter.SourceSampleRate / _adapter.TargetSampleRate)) + 2;
        var sourceSamples = sourceFrames * _sourceChannels;

        if (_source.Length < sourceSamples)
        {
            _source = new float[sourceSamples];
        }

        var read = _inner.Read(MemoryMarshal.AsBytes(_source.AsSpan(0, sourceSamples)));
        if (read < sizeof(float))
        {
            return false;
        }

        var inputSamples = read / sizeof(float);
        var capacity = _adapter.MaxDestinationSamples(inputSamples);
        if (_pool.Length < _poolSamples + capacity)
        {
            var grown = new float[_poolSamples + capacity];
            _pool.AsSpan(0, _poolSamples).CopyTo(grown);
            _pool = grown;
        }

        var written = _adapter.Process(
            _source.AsSpan(0, inputSamples),
            _pool.AsSpan(_poolSamples, capacity));
        _poolSamples += written;
        return written > 0 || inputSamples > 0;
    }
}
