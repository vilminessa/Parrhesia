using System.Runtime.InteropServices;
using NAudio.Wave;
using Parrhesia.Audio.Processing;

namespace Parrhesia.Audio.Engine;

/// <summary>
/// IWaveProvider, который на каждый Read микширует блок из графа.
/// Формат — формат движка (совпадает с mix format устройства вывода).
/// </summary>
internal sealed class GraphWaveProvider : IWaveProvider
{
    private readonly GraphProcessor _processor;
    private readonly Guid _sinkId;

    public GraphWaveProvider(GraphProcessor processor, Guid sinkId, WaveFormat format)
    {
        _processor = processor;
        _sinkId = sinkId;
        WaveFormat = format;
    }

    public WaveFormat WaveFormat { get; }

    public int Read(Span<byte> buffer)
    {
        var blockAlign = WaveFormat.BlockAlign;
        var frames = buffer.Length / blockAlign;
        if (frames <= 0)
        {
            return 0;
        }

        var floats = MemoryMarshal.Cast<byte, float>(buffer[..(frames * blockAlign)]);
        _processor.ProcessBlock(_sinkId, floats, frames);
        return frames * blockAlign;
    }
}
