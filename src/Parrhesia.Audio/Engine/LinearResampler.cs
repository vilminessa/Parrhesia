namespace Parrhesia.Audio.Engine;

/// <summary>
/// Линейный ресемплер float32 (interleaved) для адаптации источника к формату движка.
/// Непрерывная фаза через границы блоков: позиция выходного сэмпла хранится между
/// вызовами <see cref="Process"/>, поэтому пакеты разной длины не сбивают частоту.
/// Без аллокаций в <see cref="Process"/> — годится для потока захвата.
/// Компромисс качества: линейная интерполяция без антиалиасинга при даунсемплинге —
/// для речи/стрима приемлемо, для Hi-Fi/mastering нет (там нужен фильтр).
/// Один экземпляр обслуживает один поток данных; состоянием (фаза) не потокобезопасен.
/// </summary>
public sealed class LinearResampler
{
    private readonly int _channels;
    private readonly double _step;

    /// <summary>Последний кадр предыдущего блока — для интерполяции на стыке (позиция −1).</summary>
    private readonly float[] _history;

    /// <summary>Позиция следующего выходного сэмпла в координатах текущего блока; ≥ −1.</summary>
    private double _position;

    public LinearResampler(int sourceRate, int targetRate, int channels)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sourceRate);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(targetRate);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(channels);

        _channels = channels;
        _step = (double)sourceRate / targetRate;
        _history = new float[channels];
        _position = 0;
    }

    public int Channels => _channels;

    /// <summary>
    /// Максимум сэмплов выхода для <paramref name="sourceSamples"/> сэмплов входа —
    /// размер буфера назначения (вызывающий аллоцирует вне RT, в <see cref="OpenSources"/>).
    /// </summary>
    public int MaxOutputSamples(int sourceSamples)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sourceSamples);
        var frames = sourceSamples / _channels;
        var maxFrames = (int)Math.Ceiling(frames / _step) + 1;
        return maxFrames * _channels;
    }

    /// <summary>
    /// Ресемплирует <paramref name="source"/> в <paramref name="destination"/> и возвращает
    /// число записанных сэмплов (кратно каналам). Нужен вызов впустую с нулём сэмплов —
    /// не требуется: хвост довыдаётся со следующим блоком.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="destination"/> меньше <see cref="MaxOutputSamples"/>.</exception>
    public int Process(ReadOnlySpan<float> source, Span<float> destination)
    {
        var frames = source.Length / _channels;
        if (frames == 0)
        {
            return 0;
        }

        if (destination.Length < MaxOutputSamples(source.Length))
        {
            throw new ArgumentException(
                $"Буфер назначения слишком мал: {destination.Length} < {MaxOutputSamples(source.Length)}.",
                nameof(destination));
        }

        var position = _position;
        var produced = 0;

        // Выдаём, пока для позиции есть будущий кадр (интерполяция между i и i+1).
        // Позиции ≥ frames−1 ждут следующий блок и догоняются на следующем вызове.
        while (position < frames - 1)
        {
            var index = (int)Math.Floor(position);
            var t = (float)(position - index);

            ReadOnlySpan<float> first;
            ReadOnlySpan<float> second;
            if (index < 0)
            {
                // Стык блоков: первый сэмпл — из истории (последний кадр прошлого блока).
                first = _history;
                second = source[.._channels];
            }
            else
            {
                first = source.Slice(index * _channels, _channels);
                second = source.Slice((index + 1) * _channels, _channels);
            }

            var offset = produced * _channels;
            for (var channel = 0; channel < _channels; channel++)
            {
                var a = first[channel];
                destination[offset + channel] = a + ((second[channel] - a) * t);
            }

            position += _step;
            produced++;
        }

        _position = position - frames;
        source.Slice((frames - 1) * _channels, _channels).CopyTo(_history);
        return produced * _channels;
    }
}
