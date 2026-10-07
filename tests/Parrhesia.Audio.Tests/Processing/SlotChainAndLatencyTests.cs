using Parrhesia.Audio.Processing;
using Parrhesia.Core.Graph;
using Parrhesia.Plugins;

namespace Parrhesia.Audio.Tests.Processing;

/// <summary>
/// Врезка цепочек слотов в GraphProcessor и компенсация латентности
/// параллельных веток (Impulse-тесты: сухая и «медленная» ветки должны
/// совпасть по фазе на суммировании).
/// </summary>
public class SlotChainAndLatencyTests
{
    private const int BlockFrames = 64;

    [Fact]
    public void Chain_ProcessesBusBuffer()
    {
        var (graph, source, bus, sink) = BuildParallelGraph();
        using var processor = new GraphProcessor(graph);
        processor.SetInput(source.Id, new ConstantInput(0.5f));
        processor.SetSlotChain(bus.Id, [new MultiplyPlugin(2f)]);

        // Сумма на sink: сухая0,5 + влажная (0,5×2) =1,5.
        Assert.Equal(1.5f, Process(processor, sink.Id)[0], 3);
    }

    [Fact]
    public void NodeBypass_SkipsChainEntirely()
    {
        var (graph, source, bus, sink) = BuildParallelGraph();
        using var processor = new GraphProcessor(graph);
        processor.SetInput(source.Id, new ConstantInput(0.5f));
        processor.SetSlotChain(bus.Id, [new MultiplyPlugin(2f)]);

        graph.SetNodeBypass(bus.Id, true);

        // Обход: без плагина — сухая0,5 + влажная0,5 =1,0.
        Assert.Equal(1.0f, Process(processor, sink.Id)[0], 3);
    }

    [Fact]
    public void NoLatency_ImpulseArrivesAtSameBlockOnBothPaths()
    {
        var (graph, source, bus, sink) = BuildParallelGraph();
        using var processor = new GraphProcessor(graph);
        processor.SetInput(source.Id, new ImpulseInput());

        var first = Process(processor, sink.Id);

        // Обе ветки без задержек: импульс сразу, дважды (сухая + шина).
        Assert.Equal(2f, first[0], 3);
        Assert.Equal(2f, first[1], 3);
    }

    [Fact]
    public void LatentWetPath_DryPathIsDelayedToMatch()
    {
        const int latencyFrames = 96;
        var (graph, source, bus, sink) = BuildParallelGraph();
        using var processor = new GraphProcessor(graph);
        processor.SetInput(source.Id, new ImpulseInput());
        processor.SetSlotChain(bus.Id, [new MultiplyPlugin(1f, latencyFrames)]);
        processor.Invalidate(); // зафиксировать латентность в снимке

        var block1 = Process(processor, sink.Id);
        var block2 = Process(processor, sink.Id);
        var block3 = Process(processor, sink.Id);

        Assert.All(block1, s => Assert.Equal(0f, s));

        // Глобальная задержка сухой ветки = кадр96 =192 сэмпла = второй блок (128..255), локальный индекс64.
        var delaySamples = latencyFrames * 2;
        var blockIndex = delaySamples / (BlockFrames * 2);
        var localIndex = delaySamples % (BlockFrames * 2);
        var aligned = (new[] { block1, block2, block3 })[blockIndex];

        // Сухая (задержана на96) и влажная (плагин задержал на96) складываются в2.
        Assert.Equal(2f, aligned[localIndex], 3);
        Assert.Equal(2f, aligned[localIndex + 1], 3);
        Assert.Equal(0f, aligned[0], 3);
        Assert.All(block3, s => Assert.Equal(0f, s));
    }

    [Fact]
    public void MultiBlockImpulse_StaysAlignedAcrossBlockBoundary()
    {
        // Латентность128 = ровно2 блока — проверка границы блоков.
        var (graph, source, bus, sink) = BuildParallelGraph();
        using var processor = new GraphProcessor(graph);
        processor.SetInput(source.Id, new ImpulseInput());
        processor.SetSlotChain(bus.Id, [new MultiplyPlugin(1f, 128)]);
        processor.Invalidate();

        _ = Process(processor, sink.Id);
        _ = Process(processor, sink.Id);
        var third = Process(processor, sink.Id);

        Assert.Equal(2f, third[0], 3);
        Assert.Equal(2f, third[1], 3);
        Assert.Equal(0f, third[2], 3);
    }

    private static (AudioGraph Graph, AudioNode Source, AudioNode Bus, AudioNode Sink) BuildParallelGraph()
    {
        var graph = new AudioGraph();
        var source = graph.AddNode("Вход", NodeKind.Source);
        var bus = graph.AddNode("Шина", NodeKind.Bus);
        var sink = graph.AddNode("Выход", NodeKind.Sink);
        graph.AddRoute(source.Id, sink.Id, out _);       // сухая ветка
        graph.AddRoute(source.Id, bus.Id, out _);        // влажная: через шину
        graph.AddRoute(bus.Id, sink.Id, out _);
        return (graph, source, bus, sink);
    }

    private static float[] Process(GraphProcessor processor, Guid sinkId, int frames = BlockFrames)
    {
        var output = new float[frames * 2];
        processor.ProcessBlock(sinkId, output, frames);
        return output;
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

    private sealed class ConstantInput(float value) : ISampleInput
    {
        public long UnderrunSamples => 0;

        public int Read(Span<float> destination)
        {
            destination.Fill(value);
            return destination.Length;
        }
    }

    /// <summary>
    /// Фейк-плагин: factor — множитель; latencyFrames>0 — вместо множителя
    /// работает как линия задержки (выход[i] = вход[i − L], интерлив, стерео).
    /// </summary>
    private sealed class MultiplyPlugin(float factor, int latencyFrames = 0) : IAudioPlugin
    {
        private float[] _ring = latencyFrames > 0 ? new float[latencyFrames * 2] : [];
        private int _position;

        public string Name => "fake";

        public int LatencySamples => latencyFrames;

        public bool Disposed { get; private set; }

        public void Prepare(int sampleRate, int maxBlockFrames, int channels)
        {
        }

        public void Process(Span<float> interleaved, int frames)
        {
            if (latencyFrames <= 0)
            {
                for (var i = 0; i < interleaved.Length; i++)
                {
                    interleaved[i] *= factor;
                }

                return;
            }

            for (var i = 0; i < interleaved.Length; i++)
            {
                var delayed = _ring[_position];
                _ring[_position] = interleaved[i];
                interleaved[i] = delayed;
                _position++;
                if (_position == _ring.Length)
                {
                    _position = 0;
                }
            }
        }

        public byte[]? GetState() => null;

        public void SetState(byte[]? state)
        {
        }

        public void Dispose() => Disposed = true;
    }
}
