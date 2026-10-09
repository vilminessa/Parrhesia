using Parrhesia.Audio.Processing;
using Parrhesia.Core.Graph;
using Parrhesia.Plugins;

namespace Parrhesia.Audio.Tests.Processing;

public class SlotChainManagerTests
{
    [Fact]
    public void Sync_LoadsInstanceAndPublishesChain()
    {
        var graph = new AudioGraph();
        var source = graph.AddNode("Вход", NodeKind.Source);
        var bus = graph.AddNode("Шина", NodeKind.Bus);
        var sink = graph.AddNode("Выход", NodeKind.Sink);
        graph.AddRoute(source.Id, sink.Id, out _);
        graph.AddRoute(source.Id, bus.Id, out _);
        graph.AddRoute(bus.Id, sink.Id, out _);

        using var processor = new GraphProcessor(graph);
        processor.SetInput(source.Id, new ConstantInput(0.5f));
        var factory = new FakeFactory();
        using var manager = new SlotChainManager(graph, processor, factory.Create);

        graph.AddSlot(bus.Id, new PluginSlot { Path = "a.clap", PluginId = "a", Name = "A" });

        Assert.Equal(1, factory.Created);
        // Сумма на sink: сухая0,5 + влажная (0,5×2) =1,5.
        Assert.Equal(1.5f, Process(processor, sink.Id)[0], 3);
        Assert.Empty(manager.LastErrors);
    }

    [Fact]
    public void ToggleEnabled_ReusesInstanceWithoutReload()
    {
        var (graph, source, bus, sink) = BuildGraph();
        using var processor = new GraphProcessor(graph);
        processor.SetInput(source.Id, new ConstantInput(0.5f));
        var factory = new FakeFactory();
        using var manager = new SlotChainManager(graph, processor, factory.Create);
        graph.AddSlot(bus.Id, new PluginSlot { Path = "a.clap", PluginId = "a" });

        graph.SetSlotEnabled(bus.Id, 0, false);

        Assert.Equal(1, factory.Created); // переключение Enabled не перезагружает DLL
        // Слот выключен: сухая0,5 + влажная0,5 =1,0.
        Assert.Equal(1.0f, Process(processor, sink.Id)[0], 3);

        graph.SetSlotEnabled(bus.Id, 0, true);
        Assert.Equal(1, factory.Created);
        // Слот включён: сухая0,5 + влажная (0,5×2) =1,5.
        Assert.Equal(1.5f, Process(processor, sink.Id)[0], 3);
    }

    [Fact]
    public async Task ChangingPath_ReloadsAndDisposesOldDeferred()
    {
        var (graph, _, bus, _) = BuildGraph();
        using var processor = new GraphProcessor(graph);
        var factory = new FakeFactory();
        using var manager = new SlotChainManager(graph, processor, factory.Create);
        graph.AddSlot(bus.Id, new PluginSlot { Path = "a.clap", PluginId = "a" });

        var first = factory.Instances[0];

        graph.RemoveSlot(bus.Id, 0);
        graph.AddSlot(bus.Id, new PluginSlot { Path = "b.clap", PluginId = "b" });

        Assert.Equal(2, factory.Created);
        Assert.False(first.Disposed); // отложенная выгрузка — RT мог ещё держать

        await Task.Delay(700);
        Assert.True(first.Disposed);
    }

    [Fact]
    public void LoadFailure_CollectsErrorAndKeepsPassthrough()
    {
        var (graph, source, bus, sink) = BuildGraph();
        using var processor = new GraphProcessor(graph);
        processor.SetInput(source.Id, new ConstantInput(0.5f));
        using var manager = new SlotChainManager(
            graph,
            processor,
            _ => throw new PluginLoadException("нет модуля"));

        graph.AddSlot(bus.Id, new PluginSlot { Path = "битый.clap", PluginId = "x", Name = "Битый" });

        Assert.Single(manager.LastErrors);
        Assert.Contains("нет модуля", manager.LastErrors[0]);
        // Слот молчит: сухая0,5 + влажная0,5 =1,0.
        Assert.Equal(1.0f, Process(processor, sink.Id)[0], 3);
    }

    [Fact]
    public void Prepare_PropagatesFormatToInstances()
    {
        var (graph, _, bus, _) = BuildGraph();
        using var processor = new GraphProcessor(graph);
        var factory = new FakeFactory();
        using var manager = new SlotChainManager(graph, processor, factory.Create);
        graph.AddSlot(bus.Id, new PluginSlot { Path = "a.clap", PluginId = "a" });

        manager.Prepare(44100, 4410, 2);

        var instance = Assert.Single(factory.Instances);
        Assert.True(instance.Prepared);
        Assert.Equal(44100, instance.SampleRate);
        Assert.Equal(4410, instance.MaxBlock);
    }

    [Fact]
    public async Task Republish_OnBenignGraphChange_DoesNotDisposeLiveInstances()
    {
        // Регрессия: SetSlotChain при каждом Changed публикует новый массив
        // с ТЕМИ ЖЕ инстансами — выгрузка старого массива целиком убивала
        // бы плагины через500 мс после любой правки гейна.
        var (graph, _, bus, _) = BuildGraph();
        using var processor = new GraphProcessor(graph);
        var factory = new FakeFactory();
        using var manager = new SlotChainManager(graph, processor, factory.Create);
        graph.AddSlot(bus.Id, new PluginSlot { Path = "a.clap", PluginId = "a" });

        graph.SetNodeGain(bus.Id, 0.5f); // безобидная правка → Changed → ре-публикация

        await Task.Delay(700);
        Assert.False(factory.Instances[0].Disposed);
    }

    [Fact]
    public async Task CollectStates_WritesPluginStateIntoModel()
    {
        var (graph, _, bus, _) = BuildGraph();
        using var processor = new GraphProcessor(graph);
        var factory = new FakeFactory { State = [7, 7, 7] };
        using var manager = new SlotChainManager(graph, processor, factory.Create);
        graph.AddSlot(bus.Id, new PluginSlot { Path = "a.clap", PluginId = "a" });

        var changes = 0;
        void Count(object? _, GraphChange e) => changes++;
        graph.Changed += Count;

        manager.CollectStates();
        Assert.Equal(new byte[] { 7, 7, 7 }, bus.Slots[0].State);

        // Повторный снимок с теми же байтами — без новых событий Changed.
        var before = changes;
        manager.CollectStates();
        Assert.Equal(before, changes);
    }

    [Fact]
    public void ReplaceWith_KeepsInstancesByIdentity()
    {
        // Переключение профилей (ReplaceWith сохраняет id узлов) не должно
        // перезагружать плагины при совпадении path/id.
        var (graph, _, bus, _) = BuildGraph();
        using var processor = new GraphProcessor(graph);
        var factory = new FakeFactory();
        using var manager = new SlotChainManager(graph, processor, factory.Create);
        graph.AddSlot(bus.Id, new PluginSlot { Path = "a.clap", PluginId = "a" });

        var copy = new AudioGraph();
        copy.ReplaceWith(graph);
        graph.ReplaceWith(copy);

        Assert.Equal(1, factory.Created);
    }

    private static (AudioGraph Graph, AudioNode Source, AudioNode Bus, AudioNode Sink) BuildGraph()
    {
        var graph = new AudioGraph();
        var source = graph.AddNode("Вход", NodeKind.Source);
        var bus = graph.AddNode("Шина", NodeKind.Bus);
        var sink = graph.AddNode("Выход", NodeKind.Sink);
        graph.AddRoute(source.Id, sink.Id, out _);
        graph.AddRoute(source.Id, bus.Id, out _);
        graph.AddRoute(bus.Id, sink.Id, out _);
        return (graph, source, bus, sink);
    }

    private static float[] Process(GraphProcessor processor, Guid sinkId, int frames = 8)
    {
        // Один вызов = один аудио-цикл: вручную переводим эпоху (в рантайме
        // эпохальный кэш сам решает, пересчитывать ли — см. GraphProcessor).
        processor.Invalidate();
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

    private sealed class FakeFactory
    {
        public int Created { get; private set; }
        public List<FakePlugin> Instances { get; } = [];

        public byte[]? State { get; set; }

        public IAudioPlugin Create(PluginSlot slot)
        {
            Created++;
            var plugin = new FakePlugin(slot.Path) { State = State };
            Instances.Add(plugin);
            return plugin;
        }
    }

    private sealed class FakePlugin(string path) : IAudioPlugin
    {
        public string Name => path;
        public int LatencySamples => 0;
        public bool Prepared { get; private set; }
        public bool Disposed { get; private set; }
        public int SampleRate { get; private set; }
        public int MaxBlock { get; private set; }
        public byte[]? State { get; set; }

        public void Prepare(int sampleRate, int maxBlockFrames, int channels)
        {
            Prepared = true;
            SampleRate = sampleRate;
            MaxBlock = maxBlockFrames;
        }

        public void Process(Span<float> interleaved, int frames)
        {
            for (var i = 0; i < interleaved.Length; i++)
            {
                interleaved[i] *= 2f;
            }
        }

        public byte[]? GetState() => State;

        public void SetState(byte[]? state) => State = state;

        public void Dispose() => Disposed = true;
    }
}
