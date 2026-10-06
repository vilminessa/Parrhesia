using Parrhesia.Audio.Processing;
using Parrhesia.Core.Graph;

namespace Parrhesia.Audio.Tests.Processing;

public class GraphProcessorTests
{
    [Fact]
    public void SourceToSink_PassesSignal()
    {
        var graph = new AudioGraph();
        var source = graph.AddNode("Вход", NodeKind.Source);
        var sink = graph.AddNode("Выход", NodeKind.Sink);
        graph.AddRoute(source.Id, sink.Id, out _);

        using var processor = new GraphProcessor(graph);
        processor.SetInput(source.Id, new ConstantInput(1f));

        Assert.Equal(1f, Process(processor, sink.Id)[0]);
        Assert.Equal(1f, processor.GetPeak(sink.Id));
    }

    [Fact]
    public void RouteAndNodeGains_Multiply()
    {
        var graph = new AudioGraph();
        var source = graph.AddNode("Вход", NodeKind.Source);
        var sink = graph.AddNode("Выход", NodeKind.Sink);
        graph.AddRoute(source.Id, sink.Id, out _);

        using var processor = new GraphProcessor(graph);
        processor.SetInput(source.Id, new ConstantInput(1f));

        graph.SetRouteGain(source.Id, sink.Id, 0.5f);
        Assert.Equal(0.5f, Process(processor, sink.Id)[0], 3);

        graph.SetNodeGain(source.Id, 0.5f);
        Assert.Equal(0.25f, Process(processor, sink.Id)[0], 3);
    }

    [Fact]
    public void DisabledRoute_SilencesSink_ButSourceMeterStaysAlive()
    {
        var graph = new AudioGraph();
        var source = graph.AddNode("Вход", NodeKind.Source);
        var sink = graph.AddNode("Выход", NodeKind.Sink);
        graph.AddRoute(source.Id, sink.Id, out _);

        using var processor = new GraphProcessor(graph);
        processor.SetInput(source.Id, new ConstantInput(1f));
        graph.SetRouteEnabled(source.Id, sink.Id, false);

        var output = Process(processor, sink.Id);
        Assert.All(output, sample => Assert.Equal(0f, sample));
        Assert.True(processor.GetPeak(source.Id) > 0f);
        Assert.Equal(0f, processor.GetPeak(sink.Id));
    }

    [Fact]
    public void MutedSource_BlocksSignal_ButKeepsItsOwnMeter()
    {
        var graph = new AudioGraph();
        var source = graph.AddNode("Вход", NodeKind.Source);
        var sink = graph.AddNode("Выход", NodeKind.Sink);
        graph.AddRoute(source.Id, sink.Id, out _);

        using var processor = new GraphProcessor(graph);
        processor.SetInput(source.Id, new ConstantInput(1f));
        graph.SetNodeMute(source.Id, true);

        var output = Process(processor, sink.Id);
        Assert.All(output, sample => Assert.Equal(0f, sample));
        Assert.Equal(1f, processor.GetPeak(source.Id));
    }

    [Fact]
    public void Solo_SilencesEveryoneElse()
    {
        var graph = new AudioGraph();
        var a = graph.AddNode("A", NodeKind.Source);
        var b = graph.AddNode("B", NodeKind.Source);
        var sink = graph.AddNode("Выход", NodeKind.Sink);
        graph.AddRoute(a.Id, sink.Id, out _);
        graph.AddRoute(b.Id, sink.Id, out _);

        using var processor = new GraphProcessor(graph);
        processor.SetInput(a.Id, new ConstantInput(0.5f));
        processor.SetInput(b.Id, new ConstantInput(1f));

        graph.SetNodeSolo(a.Id, true);

        var output = Process(processor, sink.Id);
        Assert.Equal(0.5f, output[0], 3);
        Assert.True(processor.GetPeak(b.Id) > 0f, "Метра B должна видеть сигнал до мьюта");
    }

    [Fact]
    public void BusSumsSources_AndAppliesOwnGain()
    {
        var graph = new AudioGraph();
        var a = graph.AddNode("A", NodeKind.Source);
        var b = graph.AddNode("B", NodeKind.Source);
        var bus = graph.AddNode("Шина", NodeKind.Bus);
        var sink = graph.AddNode("Выход", NodeKind.Sink);
        graph.AddRoute(a.Id, bus.Id, out _);
        graph.AddRoute(b.Id, bus.Id, out _);
        graph.AddRoute(bus.Id, sink.Id, out _);

        using var processor = new GraphProcessor(graph);
        processor.SetInput(a.Id, new ConstantInput(1f));
        processor.SetInput(b.Id, new ConstantInput(0.5f));

        Assert.Equal(1.5f, Process(processor, sink.Id)[0], 3);

        graph.SetNodeGain(bus.Id, 0.5f);
        Assert.Equal(0.75f, Process(processor, sink.Id)[0], 3);
    }

    [Fact]
    public void UnboundSource_ProducesSilence_NotGarbage()
    {
        var graph = new AudioGraph();
        var source = graph.AddNode("Вход", NodeKind.Source);
        var sink = graph.AddNode("Выход", NodeKind.Sink);
        graph.AddRoute(source.Id, sink.Id, out _);

        using var processor = new GraphProcessor(graph);

        var output = Process(processor, sink.Id);
        Assert.All(output, sample => Assert.Equal(0f, sample));
    }

    [Fact]
    public void UnknownSink_OutputsSilence()
    {
        var graph = new AudioGraph();
        using var processor = new GraphProcessor(graph);

        var output = new float[16];
        output.AsSpan().Fill(9f);
        processor.ProcessBlock(Guid.NewGuid(), output, 8);

        Assert.All(output, sample => Assert.Equal(0f, sample));
    }

    [Fact]
    public void StructuralChange_IsVisibleOnNextBlock()
    {
        var graph = new AudioGraph();
        var source = graph.AddNode("Вход", NodeKind.Source);
        var bus = graph.AddNode("Шина", NodeKind.Bus);
        var sink = graph.AddNode("Выход", NodeKind.Sink);

        using var processor = new GraphProcessor(graph);
        processor.SetInput(source.Id, new ConstantInput(1f));

        Assert.Equal(0f, Process(processor, sink.Id)[0]);

        graph.AddRoute(source.Id, bus.Id, out _);
        graph.AddRoute(bus.Id, sink.Id, out _);

        Assert.Equal(1f, Process(processor, sink.Id)[0], 3);
    }

    private static float[] Process(GraphProcessor processor, Guid sinkId, int frames = 8)
    {
        var output = new float[frames * 2];
        processor.ProcessBlock(sinkId, output, frames);
        return output;
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
}
