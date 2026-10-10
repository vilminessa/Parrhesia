using System.Diagnostics;
using Parrhesia.Audio.Processing;
using Parrhesia.Core.Graph;
using Parrhesia.Plugins;

namespace Parrhesia.Audio.Tests.Processing;

/// <summary>
/// P-волна: загрузка плагинов ушла в фон (UI не ждёт dlopen), цепочки
/// публикуются без выгрузки disabled-экземпляров, getState/prepare идут на
/// паузе узла (вне process — VST3-контракт).
/// </summary>
public class SlotChainManagerTests
{
    [Fact]
    public async Task Sync_LoadsInstanceAndPublishesChain()
    {
        var graph = new AudioGraph();
        var source = graph.AddNode("Вход", NodeKind.Source);
        var node = graph.AddNode("Обработка", NodeKind.Plugin);
        var sink = graph.AddNode("Выход", NodeKind.Sink);
        graph.AddRoute(source.Id, sink.Id, out _);
        graph.AddRoute(source.Id, node.Id, out _);
        graph.AddRoute(node.Id, sink.Id, out _);

        using var processor = new GraphProcessor(graph);
        processor.SetInput(source.Id, new ConstantInput(0.5f));
        var factory = new FakeFactory();
        using var manager = new SlotChainManager(graph, processor, bridgeFactory: (_, slot) => factory.Create(slot));

        graph.AddSlot(node.Id, new PluginSlot { Path = "a.clap", PluginId = "a", Name = "A" });
        await manager.SyncTask;

        Assert.Equal(1, factory.Created);
        // Сумма на sink: сухая0,5 + влажная (0,5×2) =1,5.
        Assert.Equal(1.5f, Process(processor, sink.Id)[0], 3);
        Assert.Empty(manager.LastErrors);
    }

    [Fact]
    public async Task ToggleEnabled_ReusesInstanceWithoutReload()
    {
        var (graph, source, node, sink) = BuildGraph();
        using var processor = new GraphProcessor(graph);
        processor.SetInput(source.Id, new ConstantInput(0.5f));
        var factory = new FakeFactory();
        using var manager = new SlotChainManager(graph, processor, bridgeFactory: (_, slot) => factory.Create(slot));
        graph.AddSlot(node.Id, new PluginSlot { Path = "a.clap", PluginId = "a" });
        await manager.SyncTask;

        graph.SetSlotEnabled(node.Id, 0, false);
        await manager.SyncTask;

        Assert.Equal(1, factory.Created); // переключение Enabled не перезагружает DLL
        // Слот выключен: сухая0,5 + влажная0,5 =1,0.
        Assert.Equal(1.0f, Process(processor, sink.Id)[0], 3);

        graph.SetSlotEnabled(node.Id, 0, true);
        await manager.SyncTask;
        Assert.Equal(1, factory.Created);
        // Слот включён: сухая0,5 + влажная (0,5×2) =1,5.
        Assert.Equal(1.5f, Process(processor, sink.Id)[0], 3);
    }

    [Fact]
    public async Task DisabledSlot_InstanceSurvivesReEnable()
    {
        // Регрессия P-волны: публикация цепочки БЕЗ disabled-слота раньше
        // вела к отложенной выгрузке живого экземпляра — re-enable работал
        // на освобождённой памяти (AV в рантайме).
        var (graph, source, node, sink) = BuildGraph();
        using var processor = new GraphProcessor(graph);
        processor.SetInput(source.Id, new ConstantInput(0.5f));
        var factory = new FakeFactory();
        using var manager = new SlotChainManager(graph, processor, bridgeFactory: (_, slot) => factory.Create(slot));
        graph.AddSlot(node.Id, new PluginSlot { Path = "a.clap", PluginId = "a" });
        await manager.SyncTask;

        graph.SetSlotEnabled(node.Id, 0, false);
        await manager.SyncTask;
        await Task.Delay(700); // прежний баг: dispose через «греc-таймер»

        var instance = factory.Instances[0];
        Assert.False(instance.Disposed);

        graph.SetSlotEnabled(node.Id, 0, true);
        await manager.SyncTask;

        Assert.Same(instance, factory.Instances[0]);
        Assert.Equal(1.5f, Process(processor, sink.Id)[0], 3);
    }

    [Fact]
    public async Task ChangingPath_ReloadsAndDisposesOldDeferred()
    {
        var (graph, _, node, _) = BuildGraph();
        using var processor = new GraphProcessor(graph);
        var factory = new FakeFactory();
        using var manager = new SlotChainManager(graph, processor, bridgeFactory: (_, slot) => factory.Create(slot));
        graph.AddSlot(node.Id, new PluginSlot { Path = "a.clap", PluginId = "a" });
        await manager.SyncTask;

        var first = factory.Instances[0];

        graph.RemoveSlot(node.Id, 0);
        graph.AddSlot(node.Id, new PluginSlot { Path = "b.clap", PluginId = "b" });
        await manager.SyncTask;

        Assert.Equal(2, factory.Created);
        Assert.False(first.Disposed); // отложенная выгрузка — RT мог ещё держать

        // Выгрузка — по подтверждению RT-покоя либо таймауту (движок не тянет).
        await Task.Delay(1200);
        Assert.True(first.Disposed);
    }

    [Fact]
    public async Task LoadFailure_CollectsErrorAndKeepsPassthrough()
    {
        var (graph, source, node, sink) = BuildGraph();
        using var processor = new GraphProcessor(graph);
        processor.SetInput(source.Id, new ConstantInput(0.5f));
        using var manager = new SlotChainManager(
            graph,
            processor,
            bridgeFactory: (_, _) => throw new PluginLoadException("нет модуля"));

        graph.AddSlot(node.Id, new PluginSlot { Path = "битый.clap", PluginId = "x", Name = "Битый" });
        await manager.SyncTask;

        Assert.Single(manager.LastErrors);
        Assert.Contains("нет модуля", manager.LastErrors[0]);
        // Слот молчит: сухая0,5 + влажная0,5 =1,0.
        Assert.Equal(1.0f, Process(processor, sink.Id)[0], 3);
    }

    [Fact]
    public async Task Prepare_PropagatesFormatToInstances()
    {
        var (graph, _, node, _) = BuildGraph();
        using var processor = new GraphProcessor(graph);
        var factory = new FakeFactory();
        using var manager = new SlotChainManager(graph, processor, bridgeFactory: (_, slot) => factory.Create(slot));
        graph.AddSlot(node.Id, new PluginSlot { Path = "a.clap", PluginId = "a" });
        await manager.SyncTask;

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
        var (graph, _, node, _) = BuildGraph();
        using var processor = new GraphProcessor(graph);
        var factory = new FakeFactory();
        using var manager = new SlotChainManager(graph, processor, bridgeFactory: (_, slot) => factory.Create(slot));
        graph.AddSlot(node.Id, new PluginSlot { Path = "a.clap", PluginId = "a" });
        await manager.SyncTask;

        graph.SetNodeGain(node.Id, 0.5f); // безобидная правка → Changed → ре-публикация
        await manager.SyncTask;

        await Task.Delay(700);
        Assert.False(factory.Instances[0].Disposed);
    }

    [Fact]
    public async Task CollectStates_WritesPluginStateIntoModel()
    {
        var (graph, _, node, _) = BuildGraph();
        using var processor = new GraphProcessor(graph);
        var factory = new FakeFactory { State = [7, 7, 7] };
        using var manager = new SlotChainManager(graph, processor, bridgeFactory: (_, slot) => factory.Create(slot));
        graph.AddSlot(node.Id, new PluginSlot { Path = "a.clap", PluginId = "a" });
        await manager.SyncTask;

        var changes = 0;
        void Count(object? _, GraphChange e) => changes++;
        graph.Changed += Count;

        manager.CollectStates();
        Assert.Equal(new byte[] { 7, 7, 7 }, node.Slots[0].State);

        // Повторный снимок с теми же байтами — без новых событий Changed.
        var before = changes;
        manager.CollectStates();
        Assert.Equal(before, changes);
    }

    [Fact]
    public async Task ReplaceWith_KeepsInstancesByIdentity()
    {
        // Переключение профилей (ReplaceWith сохраняет id узлов) не должно
        // перезагружать плагины при совпадении path/id.
        var (graph, _, node, _) = BuildGraph();
        using var processor = new GraphProcessor(graph);
        var factory = new FakeFactory();
        using var manager = new SlotChainManager(graph, processor, bridgeFactory: (_, slot) => factory.Create(slot));
        graph.AddSlot(node.Id, new PluginSlot { Path = "a.clap", PluginId = "a" });
        await manager.SyncTask;

        var copy = new AudioGraph();
        copy.ReplaceWith(graph);
        graph.ReplaceWith(copy);
        await manager.SyncTask;

        Assert.Equal(1, factory.Created);
    }

    [Fact]
    public async Task Sync_LoadsInBackground_NotBlockingCaller()
    {
        // UI-поток: клик «+ слот» не должен ждать dlopen (секунды у реальных
        // VST3) — загрузка уходит в фон (P-волна «зависание»).
        var (graph, _, node, _) = BuildGraph();
        using var processor = new GraphProcessor(graph);
        var factory = new FakeFactory { CreateDelayMs = 300 };
        using var manager = new SlotChainManager(graph, processor, bridgeFactory: (_, slot) => factory.Create(slot));

        var stopwatch = Stopwatch.StartNew();
        graph.AddSlot(node.Id, new PluginSlot { Path = "медленный.vst3", PluginId = "slow" });
        var elapsed = stopwatch.ElapsedMilliseconds;

        Assert.True(elapsed < 250, $"AddSlot занял {elapsed} мс — загрузка на вызывающем потоке");
        await manager.SyncTask;
        Assert.Equal(1, factory.Created);
    }

    [Fact]
    public async Task CollectStates_NeverOverlapsProcess()
    {
        // VST3-контракт: getState параллельно с process — data race, роняющая
        // рантайм. Пауза цепочки перед снимком исключает пересечение.
        var (graph, source, node, sink) = BuildGraph();
        using var processor = new GraphProcessor(graph);
        processor.SetInput(source.Id, new ConstantInput(0.5f));
        var factory = new FakeFactory { Racing = true };
        using var manager = new SlotChainManager(graph, processor, bridgeFactory: (_, slot) => factory.Create(slot));
        graph.AddSlot(node.Id, new PluginSlot { Path = "a.clap", PluginId = "a" });
        await manager.SyncTask;

        var pumping = true;
        var pump = Task.Run(() =>
        {
            while (Volatile.Read(ref pumping))
            {
                Process(processor, sink.Id, frames: 16);
            }
        });

        for (var i = 0; i < 10; i++)
        {
            manager.CollectStates();
            await Task.Delay(10);
        }

        Volatile.Write(ref pumping, false);
        await pump;

        Assert.False(factory.Instances[0].OverlapDetected);
    }

    [Fact]
    public async Task PluginNode_GetChain_PublishedAndProcessed()
    {
        // S-волна: узел-плагин получает слот-вставку и обрабатывает сигнал
        // (суммарный вход → плагин ×2 → выход), как шина.
        var graph = new AudioGraph();
        var source = graph.AddNode("Вход", NodeKind.Source);
        var plugin = graph.AddNode("Компрессор", NodeKind.Plugin);
        var sink = graph.AddNode("Выход", NodeKind.Sink);
        graph.AddRoute(source.Id, plugin.Id, out _);
        graph.AddRoute(plugin.Id, sink.Id, out _);

        using var processor = new GraphProcessor(graph);
        processor.SetInput(source.Id, new ConstantInput(0.5f));
        var factory = new FakeFactory();

        // Узлы-плагины идут через мост-фабрику (S2): подменяем её фейком,
        // чтобы не спавнить настоящий процесс-исполнитель.
        using var manager = new SlotChainManager(
            graph, processor, bridgeFactory: (_, slot) => factory.Create(slot));

        graph.AddSlot(plugin.Id, new PluginSlot { Path = "a.clap", PluginId = "a" });
        await manager.SyncTask;

        Assert.Equal(1, factory.Created);
        Assert.Empty(manager.LastErrors);
        // Сигнал:0,5 → плагин ×2 =1,0 на sink.
        Assert.Equal(1.0f, Process(processor, sink.Id)[0], 3);
    }

    [Fact]
    public async Task SpawnTimeout_SchedulesPatientRetry()
    {
        // T1: зависание модуля (spawn-timeout, класс Clear) — терпеливые
        // ретраи (раз в5+ мин), а не вечные15с-циклы.
        var (graph, _, node, _) = BuildGraph();
        using var processor = new GraphProcessor(graph);
        using var manager = new SlotChainManager(
            graph, processor, bridgeFactory: (_, _) => throw new PluginSpawnTimeoutException("завис"));
        graph.AddSlot(node.Id, new PluginSlot { Path = "вис.vst3", PluginId = "v", Name = "Вис" });
        await manager.SyncTask;

        var status = manager.GetPluginStatus(node.Id);
        Assert.True(status is { Loaded: false });
        Assert.Equal(1, status!.SpawnAttempts);
        Assert.InRange(status.SecondsToRetry, 250, 300); //5 мин
        Assert.Single(manager.LastErrors);
    }

    [Fact]
    public async Task FastFailure_SchedulesAggressiveRetry()
    {
        // T1: быстрое падение (битый модуль) — ретраи агрессивные (секунды).
        var (graph, _, node, _) = BuildGraph();
        using var processor = new GraphProcessor(graph);
        using var manager = new SlotChainManager(
            graph, processor, bridgeFactory: (_, _) => throw new PluginLoadException("битый"));
        graph.AddSlot(node.Id, new PluginSlot { Path = "битый.vst3", PluginId = "b", Name = "Битый" });
        await manager.SyncTask;

        var status = manager.GetPluginStatus(node.Id);
        Assert.True(status is { Loaded: false });
        Assert.InRange(status!.SecondsToRetry, 0, 2);
    }

    [Fact]
    public async Task Restart_ResetsBackoff_ImmediateRetry()
    {
        // T1: кнопка «Перезапустить» сбрасывает бэкофф — попытка немедленная.
        var (graph, _, node, _) = BuildGraph();
        using var processor = new GraphProcessor(graph);
        using var manager = new SlotChainManager(
            graph, processor, bridgeFactory: (_, _) => throw new PluginSpawnTimeoutException("завис"));
        graph.AddSlot(node.Id, new PluginSlot { Path = "вис.vst3", PluginId = "v", Name = "Вис" });
        await manager.SyncTask;
        Assert.Equal(1, manager.GetSpawnAttempts(node.Id));

        manager.RestartPluginNode(node.Id);

        var deadline = Environment.TickCount64 + 3000;
        while (Environment.TickCount64 < deadline && manager.GetSpawnAttempts(node.Id) < 2)
        {
            await Task.Delay(50);
        }

        Assert.Equal(2, manager.GetSpawnAttempts(node.Id)); // попытка сразу, без5-мин ожидания
    }

    private static (AudioGraph Graph, AudioNode Source, AudioNode Node, AudioNode Sink) BuildGraph()
    {
        var graph = new AudioGraph();
        var source = graph.AddNode("Вход", NodeKind.Source);
        var node = graph.AddNode("Обработка", NodeKind.Plugin);
        var sink = graph.AddNode("Выход", NodeKind.Sink);
        graph.AddRoute(source.Id, sink.Id, out _);
        graph.AddRoute(source.Id, node.Id, out _);
        graph.AddRoute(node.Id, sink.Id, out _);
        return (graph, source, node, sink);
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

        /// <summary>Искусственная задержка «dlopen» (мс) для теста фона.</summary>
        public int CreateDelayMs { get; set; }

        /// <summary>Создавать плагин, ловящий пересечение process/getState.</summary>
        public bool Racing { get; set; }

        public IAudioPlugin Create(PluginSlot slot)
        {
            if (CreateDelayMs > 0)
            {
                Thread.Sleep(CreateDelayMs);
            }

            Created++;
            var plugin = new FakePlugin(slot.Path) { State = State };
            if (Racing)
            {
                plugin.RaceWatch = true;
            }

            Instances.Add(plugin);
            return plugin;
        }
    }

    private sealed class FakePlugin(string path) : IAudioPlugin
    {
        private int _nodey;

        public string Name => path;
        public int LatencySamples => 0;
        public bool Prepared { get; private set; }
        public bool Disposed { get; private set; }
        public int SampleRate { get; private set; }
        public int MaxBlock { get; private set; }
        public byte[]? State { get; set; }

        /// <summary>Включает «гонку-ловушку»: GetState фиксирует пересечение с Process.</summary>
        public bool RaceWatch { get; set; }

        public bool OverlapDetected { get; private set; }

        public void Prepare(int sampleRate, int maxBlockFrames, int channels)
        {
            Prepared = true;
            SampleRate = sampleRate;
            MaxBlock = maxBlockFrames;
        }

        public void Process(Span<float> interleaved, int frames)
        {
            if (RaceWatch)
            {
                Interlocked.Increment(ref _nodey);
                Thread.Sleep(1); // имитация работы плагина внутри process
                Interlocked.Decrement(ref _nodey);
            }

            for (var i = 0; i < interleaved.Length; i++)
            {
                interleaved[i] *= 2f;
            }
        }

        public byte[]? GetState()
        {
            if (RaceWatch && Volatile.Read(ref _nodey) != 0)
            {
                OverlapDetected = true;
            }

            return State;
        }

        public void SetState(byte[]? state) => State = state;

        public void Dispose() => Disposed = true;
    }
}
