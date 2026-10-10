using Parrhesia.Audio.Processing;
using Parrhesia.Core.Graph;
using Parrhesia.Plugins;

namespace Parrhesia.Audio.Tests.Processing;

/// <summary>
/// S5: компенсация латентности узла-плагина — сухая ветка мимо узла
/// выравнивается буфером ChainLatency (латентность исполнителя/плагина),
/// импульс выходит в обоих путях синхронно.
/// </summary>
public class LatencyCompensationTests
{
    private const int BlockFrames = 64;

    [Fact]
    public void PluginNode_Latency128_AlignsWithDryPath()
    {
        var graph = new AudioGraph();
        var source = graph.AddNode("Вход", NodeKind.Source);
        var plugin = graph.AddNode("Обработка", NodeKind.Plugin);
        var sink = graph.AddNode("Выход", NodeKind.Sink);
        graph.AddRoute(source.Id, sink.Id, out _);      // сухая
        graph.AddRoute(source.Id, plugin.Id, out _);     // влажная
        graph.AddRoute(plugin.Id, sink.Id, out _);

        using var processor = new GraphProcessor(graph);

        // Фейковая фабрика моста: латентность128 кадров (×1 — чистая задержка).
        using var manager = new SlotChainManager(
            graph, processor, bridgeFactory: (_, _) => new LatencyPlugin(128));
        graph.AddSlot(plugin.Id, new PluginSlot { Path = "latency", PluginId = "lat" });
        manager.Prepare(48000, BlockFrames, 2);
        Assert.Empty(manager.LastErrors);

        // Латентность цепочки узла видна snapshot'у — на ней строится
        // компенсирующая задержка сухих рёбер.
        Assert.Equal(128, processor.ChainLatency(plugin.Id));

        processor.SetInput(source.Id, new ImpulseInput());

        var first = Process(processor, sink.Id);
        var second = Process(processor, sink.Id);
        var third = Process(processor, sink.Id);

        // Латентность128 = ровно2 блока по64: импульс (кадр0) выходит в
        // третьем блоке, и сухая ветка (компенсация128) совпадает с плагином.
        Assert.All(first, s => Assert.Equal(0f, s));
        Assert.All(second, s => Assert.Equal(0f, s));
        Assert.Equal(2f, third[0], 3);
        Assert.Equal(2f, third[1], 3);
        Assert.Equal(0f, third[2], 3);
    }

    private static float[] Process(GraphProcessor processor, Guid sinkId, int frames = BlockFrames)
    {
        processor.Invalidate();
        var output = new float[frames * 2];
        processor.ProcessBlock(sinkId, output, frames);
        return output;
    }

    /// <summary>Чистая задержка delayFrames кадров (стерео) — как нативный latency-плагин.</summary>
    private sealed class LatencyPlugin(int delayFrames) : IAudioPlugin
    {
        private readonly float[] _buffer = new float[delayFrames * 2];
        private int _position;

        public string Name => "latency";

        public int LatencySamples => delayFrames;

        public void Prepare(int sampleRate, int maxBlockFrames, int channels)
        {
        }

        public void Process(Span<float> interleaved, int frames)
        {
            var samples = Math.Min(interleaved.Length, frames * 2);
            for (var i = 0; i < samples; i += 2)
            {
                var delayedL = _buffer[_position];
                var delayedR = _buffer[_position + 1];
                _buffer[_position] = interleaved[i];
                _buffer[_position + 1] = interleaved[i + 1];
                interleaved[i] = delayedL;
                interleaved[i + 1] = delayedR;
                _position += 2;
                if (_position >= _buffer.Length)
                {
                    _position = 0;
                }
            }
        }

        public byte[]? GetState() => null;

        public void SetState(byte[]? state)
        {
        }

        public void Dispose()
        {
        }
    }

    /// <summary>Один импульс в первом блоке (кадр0, оба канала), дальше тишина.</summary>
    private sealed class ImpulseInput : ISampleInput
    {
        private bool _fired;

        public long UnderrunSamples => 0;

        public int Read(Span<float> destination)
        {
            destination.Clear();
            if (!_fired)
            {
                _fired = true;
                if (destination.Length >= 2)
                {
                    destination[0] = 1f;
                    destination[1] = 1f;
                }
            }

            return destination.Length;
        }
    }
}
