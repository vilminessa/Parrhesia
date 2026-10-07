using NAudio.Wave;

namespace Parrhesia.Audio.Engine;

/// <summary>
/// Обёртка <see cref="IWaveProvider"/>, сериализующая параллельные <see cref="Read"/>
/// общим замком. Нужна, когда один GraphProcessor обслуживает нескольких RT-потребителей
/// (помпа виртуального сника + монитор-плеер): ядро микширования не потокобезопасно
/// и по контракту не меняется — мьютекс живёт здесь, на границе потребителей.
/// </summary>
public sealed class SerializedWaveProvider : IWaveProvider
{
    private readonly IWaveProvider _inner;
    private readonly object _gate;

    public SerializedWaveProvider(IWaveProvider inner, object gate)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
    }

    public WaveFormat WaveFormat => _inner.WaveFormat;

    public int Read(Span<byte> buffer)
    {
        lock (_gate)
        {
            return _inner.Read(buffer);
        }
    }
}
