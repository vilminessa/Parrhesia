namespace Parrhesia.Audio.Engine;

/// <summary>
/// Адаптер формата источника к формату движка: приведение числа каналов и линейный
/// ресемпл. Стоит между <c>DataAvailable</c> и кольцом (<see cref="Buffers.SampleRing"/>),
/// кольцо остаётся в формате движка. Аналогия с прежним поведением: раньше несовпадение
/// форматов означало «источник пропущен» — теперь источник пропускается только если
/// формат вовсе не float32 (см. WasapiAudioEngine).
/// Непотокобезопасен: один экземпляр — один поток захвата.
/// </summary>
public sealed class SourceFormatAdapter
{
    private readonly int _sourceChannels;
    private readonly int _targetChannels;
    private readonly bool _needsChannelMap;
    private readonly LinearResampler _resampler;

    /// <summary>Буфер «каналы приведены, частота ещё исходная» (растёт в конструкторе/первом вызове, вне RT потока данных он же владелец).</summary>
    private float[] _mapped = [];

    private SourceFormatAdapter(int sourceSampleRate, int sourceChannels, int targetSampleRate, int targetChannels)
    {
        SourceSampleRate = sourceSampleRate;
        TargetSampleRate = targetSampleRate;
        _sourceChannels = sourceChannels;
        _targetChannels = targetChannels;
        _needsChannelMap = sourceChannels != targetChannels;
        _resampler = new LinearResampler(sourceSampleRate, targetSampleRate, targetChannels);
    }

    public int SourceSampleRate { get; }

    public int TargetSampleRate { get; }

    public int SourceChannels => _sourceChannels;

    public int TargetChannels => _targetChannels;

    /// <summary>
    /// Создаёт адаптер либо null, если форматы совпадают — тогда писать в кольцо можно
    /// напрямую (null = пасsthrough, а не ошибка).
    /// </summary>
    public static SourceFormatAdapter? Create(
        int sourceSampleRate,
        int sourceChannels,
        int targetSampleRate,
        int targetChannels)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sourceSampleRate);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sourceChannels);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(targetSampleRate);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(targetChannels);

        return sourceSampleRate == targetSampleRate && sourceChannels == targetChannels
            ? null
            : new SourceFormatAdapter(sourceSampleRate, sourceChannels, targetSampleRate, targetChannels);
    }

    /// <summary>Максимум сэмплов выхода для данного числа сэмплов входа (размер буфера назначения).</summary>
    public int MaxDestinationSamples(int sourceSamples)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sourceSamples);
        var frames = sourceSamples / _sourceChannels;
        return _resampler.MaxOutputSamples(frames * _targetChannels);
    }

    /// <summary>
    /// Адаптирует источник в буфер назначения (размер — <see cref="MaxDestinationSamples"/>).
    /// Возвращает число записанных сэмплов, кратное каналам движка.
    /// </summary>
    public int Process(ReadOnlySpan<float> source, Span<float> destination)
    {
        var frames = source.Length / _sourceChannels;
        if (frames == 0)
        {
            return 0;
        }

        ReadOnlySpan<float> stage;
        if (_needsChannelMap)
        {
            var mappedSamples = frames * _targetChannels;
            if (_mapped.Length < mappedSamples)
            {
                _mapped = new float[mappedSamples];
            }

            var mapped = _mapped.AsSpan(0, mappedSamples);
            MapChannels(source, mapped, frames);
            stage = mapped;
        }
        else
        {
            stage = source;
        }

        return _resampler.Process(stage, destination);
    }

    /// <summary>
    /// Приведение каналов одного блока (interleaved, частота не меняется):
    /// моно → все каналы, каналов больше, чем нужно → первые N (для движка это стерео),
    /// каналов меньше (не моно) → копируются первые, остальные нули; стерео → моно → среднее.
    /// </summary>
    private void MapChannels(ReadOnlySpan<float> source, Span<float> destination, int frames)
    {
        if (_sourceChannels == 1)
        {
            for (var frame = 0; frame < frames; frame++)
            {
                var value = source[frame];
                var offset = frame * _targetChannels;
                for (var channel = 0; channel < _targetChannels; channel++)
                {
                    destination[offset + channel] = value;
                }
            }

            return;
        }

        if (_targetChannels == 1)
        {
            for (var frame = 0; frame < frames; frame++)
            {
                var sum = 0f;
                var inputOffset = frame * _sourceChannels;
                for (var channel = 0; channel < _sourceChannels; channel++)
                {
                    sum += source[inputOffset + channel];
                }

                destination[frame] = sum / _sourceChannels;
            }

            return;
        }

        var copy = Math.Min(_sourceChannels, _targetChannels);
        for (var frame = 0; frame < frames; frame++)
        {
            var inputOffset = frame * _sourceChannels;
            var outputOffset = frame * _targetChannels;
            for (var channel = 0; channel < copy; channel++)
            {
                destination[outputOffset + channel] = source[inputOffset + channel];
            }

            for (var channel = copy; channel < _targetChannels; channel++)
            {
                destination[outputOffset + channel] = 0f;
            }
        }
    }
}
