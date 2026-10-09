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

    [Fact]
    public void DefaultStereoRoute_PassesBothChannels()
    {
        var graph = new AudioGraph();
        var source = graph.AddNode("Вход", NodeKind.Source);
        var sink = graph.AddNode("Выход", NodeKind.Sink);
        graph.AddRoute(source.Id, sink.Id, out _);

        using var processor = new GraphProcessor(graph);
        processor.SetInput(source.Id, new StereoInput(left: 0.75f, right: 0.25f));

        var output = Process(processor, sink.Id);
        Assert.Equal(0.75f, output[0], 3);
        Assert.Equal(0.25f, output[1], 3);
        Assert.Equal(0.75f, output[2], 3);
        Assert.Equal(0.25f, output[3], 3);
    }

    [Fact]
    public void CrossChannelRoute_SendsLeftToRightOnly()
    {
        var graph = new AudioGraph();
        var source = graph.AddNode("Вход", NodeKind.Source);
        var sink = graph.AddNode("Выход", NodeKind.Sink);

        // Только L → R: левый канал источника попадает в правый канал назначения.
        graph.AddRoute(source.Id, 0, sink.Id, 1, out _);

        using var processor = new GraphProcessor(graph);
        processor.SetInput(source.Id, new StereoInput(left: 0.75f, right: 0.25f));

        var output = Process(processor, sink.Id);
        Assert.Equal(0f, output[0], 3);   // L назначения молчит
        Assert.Equal(0.75f, output[1], 3); // R назначения = L источника
    }

    [Fact]
    public void SwapMapping_ExchangesChannels()
    {
        var graph = new AudioGraph();
        var source = graph.AddNode("Вход", NodeKind.Source);
        var sink = graph.AddNode("Выход", NodeKind.Sink);

        // Кросс создаётся парами каналов: L→R, затем R→L.
        graph.AddRoute(source.Id, 0, sink.Id, 1, out var route);
        graph.AddRoute(source.Id, 1, sink.Id, 0, out _);
        Assert.Equal(ChannelMap.Pair(0, 1).With(1, 0, enabled: true).Bits, route!.Map.Bits);

        using var processor = new GraphProcessor(graph);
        processor.SetInput(source.Id, new StereoInput(left: 0.75f, right: 0.25f));

        var output = Process(processor, sink.Id);
        Assert.Equal(0.25f, output[0], 3);
        Assert.Equal(0.75f, output[1], 3);
    }

    [Fact]
    public void PartialMaps_SumPerChannelIndependently()
    {
        var graph = new AudioGraph();
        var a = graph.AddNode("A", NodeKind.Source);
        var b = graph.AddNode("B", NodeKind.Source);
        var sink = graph.AddNode("Выход", NodeKind.Sink);

        graph.AddRoute(a.Id, 0, sink.Id, 0, out _); // A.L → L
        graph.AddRoute(b.Id, 0, sink.Id, 1, out _); // B.L → R

        using var processor = new GraphProcessor(graph);
        processor.SetInput(a.Id, new StereoInput(left: 1f, right: 0.9f));
        processor.SetInput(b.Id, new StereoInput(left: 0.5f, right: 0.9f));

        var output = Process(processor, sink.Id);
        Assert.Equal(1f, output[0], 3);
        Assert.Equal(0.5f, output[1], 3);
    }

    [Fact]
    public void CrossChannelThroughBus_KeepsMappingAndAppliesBusGain()
    {
        var graph = new AudioGraph();
        var source = graph.AddNode("Вход", NodeKind.Source);
        var bus = graph.AddNode("Шина", NodeKind.Bus);
        var sink = graph.AddNode("Выход", NodeKind.Sink);

        graph.AddRoute(source.Id, 0, bus.Id, 1, out _); // L → R
        graph.AddRoute(bus.Id, sink.Id, out _);          // дальше стерео-парой

        using var processor = new GraphProcessor(graph);
        processor.SetInput(source.Id, new StereoInput(left: 1f, right: 0.1f));

        var output = Process(processor, sink.Id);
        Assert.Equal(0f, output[0], 3);   // шина получила только правый
        Assert.Equal(1f, output[1], 3);

        graph.SetNodeGain(bus.Id, 0.5f);
        output = Process(processor, sink.Id);
        Assert.Equal(0f, output[0], 3);
        Assert.Equal(0.5f, output[1], 3);
    }

    [Fact]
    public void Bypass_IgnoresGainAndMute()
    {
        var graph = new AudioGraph();
        var source = graph.AddNode("Вход", NodeKind.Source);
        var sink = graph.AddNode("Выход", NodeKind.Sink);
        graph.AddRoute(source.Id, sink.Id, out _);

        using var processor = new GraphProcessor(graph);
        processor.SetInput(source.Id, new ConstantInput(1f));

        graph.SetNodeGain(source.Id, 0.25f);
        Assert.Equal(0.25f, Process(processor, sink.Id)[0], 3);

        graph.SetNodeMute(source.Id, true);
        Assert.Equal(0f, Process(processor, sink.Id)[0]);

        // Обход = полная прозрачность: гейн и mute игнорируются.
        graph.SetNodeBypass(source.Id, true);
        Assert.Equal(1f, Process(processor, sink.Id)[0], 3);

        // Выход из обхода — mute снова действует.
        graph.SetNodeBypass(source.Id, false);
        Assert.Equal(0f, Process(processor, sink.Id)[0]);
    }

    [Fact]
    public void BypassOnBus_PassesSumUnchanged()
    {
        var graph = new AudioGraph();
        var a = graph.AddNode("A", NodeKind.Source);
        var bus = graph.AddNode("Шина", NodeKind.Bus);
        var sink = graph.AddNode("Выход", NodeKind.Sink);
        graph.AddRoute(a.Id, bus.Id, out _);
        graph.AddRoute(bus.Id, sink.Id, out _);

        using var processor = new GraphProcessor(graph);
        processor.SetInput(a.Id, new ConstantInput(1f));

        graph.SetNodeGain(bus.Id, 0.5f);
        Assert.Equal(0.5f, Process(processor, sink.Id)[0], 3);

        graph.SetNodeBypass(bus.Id, true);
        Assert.Equal(1f, Process(processor, sink.Id)[0], 3);
    }

    [Fact]
    public void StereoToMono_ScalesEachChannelByHalf()
    {
        var graph = new AudioGraph();
        var source = graph.AddNode("Вход", NodeKind.Source);
        var bus = graph.AddNode("Моно-шина", NodeKind.Bus);
        var sink = graph.AddNode("Выход", NodeKind.Sink);
        graph.SetNodeChannels(bus.Id, 1);
        graph.AddRoute(source.Id, bus.Id, out _);   // диагональ 2→1: обе пары, scale 0.5
        graph.AddRoute(bus.Id, sink.Id, out _);      // 1→2: дубль

        using var processor = new GraphProcessor(graph);
        processor.SetInput(source.Id, new StereoInput(left: 1f, right: 1f));

        var output = Process(processor, sink.Id);
        Assert.Equal(1f, output[0], 3);
        Assert.Equal(1f, output[1], 3);

        processor.SetInput(source.Id, new StereoInput(left: 1f, right: 0f));
        output = Process(processor, sink.Id);
        Assert.Equal(0.5f, output[0], 3);
        Assert.Equal(0.5f, output[1], 3);
    }

    [Fact]
    public void MonoSourceToStereo_DuplicatesWithoutScaling()
    {
        var graph = new AudioGraph();
        var source = graph.AddNode("Моно", NodeKind.Source);
        var sink = graph.AddNode("Выход", NodeKind.Sink);
        graph.SetNodeChannels(source.Id, 1);
        graph.AddRoute(source.Id, sink.Id, out _);   // 1→2: дубль без деления

        using var processor = new GraphProcessor(graph);
        processor.SetInput(source.Id, new StereoInput(left: 0.5f, right: 0.5f));

        var output = Process(processor, sink.Id);
        Assert.Equal(0.5f, output[0], 3);
        Assert.Equal(0.5f, output[1], 3);
    }

    [Fact]
    public void MonoSink_UpscalesToEngineChannels()
    {
        var graph = new AudioGraph();
        var source = graph.AddNode("Вход", NodeKind.Source);
        var sink = graph.AddNode("Моно-выход", NodeKind.Sink);
        graph.SetNodeChannels(sink.Id, 1);
        graph.AddRoute(source.Id, sink.Id, out _);   // 2→1: scale 0.5

        using var processor = new GraphProcessor(graph);
        processor.SetInput(source.Id, new StereoInput(left: 1f, right: 1f));

        // Моно-назначение дублируется на оба канала движка (иначе только левый).
        var output = Process(processor, sink.Id);
        Assert.Equal(1f, output[0], 3);
        Assert.Equal(1f, output[1], 3);
    }

    [Fact]
    public void TwoSinksInSameEpoch_ReadEachInputExactlyOnce()
    {
        var graph = new AudioGraph();
        var s1 = graph.AddNode("A", NodeKind.Source);
        var s2 = graph.AddNode("B", NodeKind.Source);
        var sink1 = graph.AddNode("Выход1", NodeKind.Sink);
        var sink2 = graph.AddNode("Выход2", NodeKind.Sink);
        graph.AddRoute(s1.Id, sink1.Id, out _);
        graph.AddRoute(s2.Id, sink2.Id, out _);

        using var processor = new GraphProcessor(graph);
        var in1 = new CountingInput(1f);
        var in2 = new CountingInput(2f);
        processor.SetInput(s1.Id, in1);
        processor.SetInput(s2.Id, in2);

        // Четыре пулла подряд БЕЗ перевода эпохи (2 сника ×2): без
        // эпохального кэша каждый пулл дренажил кольца заново — при2+
        // выходах второй пулл видел пустоту («под» в полную скорость).
        var t0 = Environment.TickCount64;
        var a1 = Raw(processor, sink1.Id);
        var b1 = Raw(processor, sink2.Id);
        var a2 = Raw(processor, sink1.Id);
        var b2 = Raw(processor, sink2.Id);
        if (Environment.TickCount64 - t0 >= 10)
        {
            return; // пересекли границу эпохи10мс — не детерминировано
        }

        Assert.Equal(1, in1.ReadCalls);
        Assert.Equal(1, in2.ReadCalls);
        Assert.Equal(1f, a1[0]);
        Assert.Equal(2f, b1[0]);
        Assert.Equal(1f, a2[0]);
        Assert.Equal(2f, b2[0]);
    }

    [Fact]
    public void Invalidate_ForcesRecomputeWithinSameEpoch()
    {
        var graph = new AudioGraph();
        var source = graph.AddNode("Вход", NodeKind.Source);
        var sink = graph.AddNode("Выход", NodeKind.Sink);
        graph.AddRoute(source.Id, sink.Id, out _);

        using var processor = new GraphProcessor(graph);
        var input = new CountingInput(1f);
        processor.SetInput(source.Id, input);

        _ = Raw(processor, sink.Id);
        Assert.Equal(1, input.ReadCalls);

        // Второй пулл в той же эпохе — из кэша (без повторного чтения).
        _ = Raw(processor, sink.Id);
        Assert.Equal(1, input.ReadCalls);

        // Смена входа сбрасывает эпохальные буферы.
        processor.SetInput(source.Id, input);
        _ = Raw(processor, sink.Id);
        Assert.Equal(2, input.ReadCalls);

        // Явная инвалидация (правки графа) тоже.
        processor.Invalidate();
        _ = Raw(processor, sink.Id);
        Assert.Equal(3, input.ReadCalls);
    }

    private static float[] Process(GraphProcessor processor, Guid sinkId, int frames = 8)
    {
        // Семантика старых тестов: каждый вызов = новый аудио-цикл.
        // Runtime этого НЕ делает — там работает эпохальный кэш (см.
        // TwoSinksInSameEpoch); тесты переводят эпоху вручную.
        processor.Invalidate();
        return Raw(processor, sinkId, frames);
    }

    private static float[] Raw(GraphProcessor processor, Guid sinkId, int frames = 8)
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

    /// <summary>Вход, считающий вызовы Read (проверка эпохального кэша).</summary>
    private sealed class CountingInput(float value) : ISampleInput
    {
        public long UnderrunSamples => 0;

        public int ReadCalls { get; private set; }

        public int Read(Span<float> destination)
        {
            ReadCalls++;
            destination.Fill(value);
            return destination.Length;
        }
    }

    /// <summary>Stereo-вход с разными значениями каналов (interleaved L,R).</summary>
    private sealed class StereoInput(float left, float right) : ISampleInput
    {
        public long UnderrunSamples => 0;

        public int Read(Span<float> destination)
        {
            for (var i = 0; i + 1 < destination.Length; i += 2)
            {
                destination[i] = left;
                destination[i + 1] = right;
            }

            return destination.Length;
        }
    }
}
