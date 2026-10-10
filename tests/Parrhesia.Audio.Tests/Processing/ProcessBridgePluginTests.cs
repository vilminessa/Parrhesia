using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Parrhesia.Audio.Processing;
using Parrhesia.Core.Graph;
using Parrhesia.Plugins;
using Parrhesia.Plugins.Vst3;
using Xunit.Abstractions;

namespace Parrhesia.Audio.Tests.Processing;

/// <summary>
/// S2/S3 (S-волна): сквозной тест — узел-плагин (NodeKind.Plugin) исполняется
/// в дочернем процессе (App.exe --plugin-host) через реальный VST3-модуль.
/// Единый механизм без исключений: спавн, state через управляющий канал,
/// wet-обработка по мосту, компенсация латентности, авто-restart убитого
/// исполнителя тиком менеджера, ретраи спавна с бэкоффом, смерть при Dispose.
/// Пропускается, если App.exe или тестовый плагин не собраны.
/// </summary>
public class ProcessBridgePluginTests
{
    private const int BlockFrames = 480; //10мс @48кГц

    private readonly ITestOutputHelper _output;

    public ProcessBridgePluginTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void PluginNode_RealVst3_RunsInChildProcess_StateRoundTrips_ChildDiesOnDispose()
    {
        var exe = ProcessBridgePlugin.FindAppExe();
        var dll = Path.Combine(AppContext.BaseDirectory, "test-plugin-vst3.dll");
        if (exe is null || !File.Exists(dll))
        {
            return; // вне репозитория — проба моста пропущена
        }

        // Идентичность из реального перечисления — не хардкодим CID.
        var descriptor = Vst3Loader.Enumerate(dll).Single(p => p.Name == "Parrhesia Test Gain");

        var graph = new AudioGraph();
        var source = graph.AddNode("Вход", NodeKind.Source);
        var plugin = graph.AddNode("Компрессор", NodeKind.Plugin);
        var sink = graph.AddNode("Выход", NodeKind.Sink);
        Assert.Equal(RouteError.None, graph.AddRoute(source.Id, plugin.Id, out _));
        Assert.Equal(RouteError.None, graph.AddRoute(plugin.Id, sink.Id, out _));
        graph.AddSlot(plugin.Id, new PluginSlot
        {
            Format = PluginFormat.Vst3,
            Path = descriptor.Path,
            PluginId = descriptor.PluginId,
            Name = descriptor.Name,
        });

        using var processor = new GraphProcessor(graph);
        using var manager = new SlotChainManager(graph, processor);
        var pid = 0;
        try
        {
            // Prepare → Sync: спавн исполнителя идёт вне локов менеджера.
            manager.Prepare(48000, BlockFrames, 2);
            foreach (var error in manager.LastErrors)
            {
                _output.WriteLine("ERR: " + error);
            }

            Assert.Empty(manager.LastErrors);

            var bridge = Assert.IsType<ProcessBridgePlugin>(manager.GetSlotInstance(plugin.Id, 0));
            pid = bridge.ChildPid;
            Assert.True(pid > 0, "процесс-исполнитель не запущен");

            // Латентность моста — один аудио-блок конвейера (для компенсации S3).
            Assert.Equal(BlockFrames, bridge.LatencySamples);

            // Компенсация маршрутов видит латентность цепочки узла — сухая
            // ветка мимо плагина выравнивается буфером на BlockFrames.
            Assert.Equal(BlockFrames, processor.ChainLatency(plugin.Id));

            // State хост → ребёнок → хост (named pipe) на живом VST3.
            var state = bridge.GetState();
            Assert.NotNull(state);
            bridge.SetState(state);
            Assert.Equal(state, bridge.GetState());

            // S4b: редактор открывается командой В ПРОЦЕССЕ-исполнителе —
            // ждём реальное окно «Редактор: …» у процесса исполнителя.
            var executorPid = bridge.ChildPid;
            var editor = bridge.OpenEditor();
            Assert.True(editor.Ok, editor.Error);

            var editorDeadline = Environment.TickCount64 + 8_000;
            while (!WindowWithTitleExists(executorPid, "Редактор:") &&
                   Environment.TickCount64 < editorDeadline)
            {
                Thread.Sleep(100);
            }

            Assert.True(
                WindowWithTitleExists(executorPid, "Редактор:"),
                "окно редактора не появилось в процессе-исполнителе за8 с");

            // Прогрев: первые блоки идут dry (0,5), затем ребёнок отдаёт wet ×2 = 1,0.
            processor.SetInput(source.Id, new ConstantInput(0.5f));
            var deadline = Environment.TickCount64 + 12_000;
            float[] output;
            do
            {
                output = Process(processor, sink.Id);
                if (output[0] >= 0.999f)
                {
                    break;
                }

                Thread.Sleep(10);
            }
            while (Environment.TickCount64 < deadline);

            Assert.True(
                output[0] >= 0.999f,
                $"wet не пришёл за12 с: output[0]={output[0]}; blocks={bridge.ProcessedBlocks} " +
                $"drops={bridge.Drops} alive={bridge.ChildAlive} pid={pid} dead={!ProcessAlive(pid)}");
            Assert.Equal(1.0f, output[1], 3);

            // Узел умер по-хорошему: после Dispose исполнителя нет.
            manager.Dispose();
            var killDeadline = Environment.TickCount64 + 5_000;
            while (ProcessAlive(pid) && Environment.TickCount64 < killDeadline)
            {
                Thread.Sleep(50);
            }

            Assert.False(ProcessAlive(pid), "процесс-исполнитель пережил Dispose менеджера");
        }
        finally
        {
            manager.Dispose(); // идемпотентно; ниже — страховка от сирот при упавшем assert
            if (pid > 0)
            {
                TryKill(pid);
            }
        }
    }

    [Fact]
    public void DeadChild_ReplacedByManagerTick_AudioRecovers()
    {
        var dll = Path.Combine(AppContext.BaseDirectory, "test-plugin-vst3.dll");
        if (ProcessBridgePlugin.FindAppExe() is null || !File.Exists(dll))
        {
            return; // вне репозитория — проба моста пропущена
        }

        var descriptor = Vst3Loader.Enumerate(dll).Single(p => p.Name == "Parrhesia Test Gain");
        var harness = CreateHarness(new PluginSlot
        {
            Format = PluginFormat.Vst3,
            Path = descriptor.Path,
            PluginId = descriptor.PluginId,
            Name = descriptor.Name,
        });
        var killedPid = 0;
        try
        {
            var first = Assert.IsType<ProcessBridgePlugin>(harness.Manager.GetSlotInstance(harness.PluginId, 0));
            killedPid = first.ChildPid;
            Assert.True(killedPid > 0, "исполнитель не запущен");

            // Душим исполнителя: heartbeat замерзнет → тик менеджера (2с)
            // увидит NeedsRestart и пересоздаст узел (бэкофф тут не нужен —
            // смерть ≠ ошибка спавна, рестарт сразу).
            TryKill(killedPid);

            ProcessBridgePlugin? replacement = null;
            var deadline = Environment.TickCount64 + 15_000;
            while (Environment.TickCount64 < deadline)
            {
                var current = harness.Manager.GetSlotInstance(harness.PluginId, 0) as ProcessBridgePlugin;
                if (current is not null && !ReferenceEquals(current, first) && current.ChildPid > 0)
                {
                    replacement = current;
                    break;
                }

                Thread.Sleep(100);
            }

            Assert.NotNull(replacement);
            var newBridge = replacement!;
            Assert.NotEqual(killedPid, newBridge.ChildPid);
            Assert.True(newBridge.ChildAlive);

            // Попытки: первая (спавн) + рестарт.
            Assert.Equal(2, harness.Manager.GetSpawnAttempts(harness.PluginId));

            // Аудио восстановилось: wet ×2 снова идёт по мосту.
            harness.Processor.SetInput(harness.SourceId, new ConstantInput(0.5f));
            deadline = Environment.TickCount64 + 6_000;
            float[] output;
            do
            {
                output = Process(harness.Processor, harness.SinkId);
                if (output[0] >= 0.999f)
                {
                    break;
                }

                Thread.Sleep(10);
            }
            while (Environment.TickCount64 < deadline);

            Assert.True(
                output[0] >= 0.999f,
                $"wet не восстановился после рестарта: {output[0]}; " +
                $"blocks={newBridge.ProcessedBlocks} drops={newBridge.Drops}");
        }
        finally
        {
            harness.Dispose();
            if (killedPid > 0)
            {
                TryKill(killedPid);
            }
        }
    }

    [Fact]
    public void SpawnFailure_RetriesWithBackoff()
    {
        // Слот с несуществующим модулем: спавн падает (ребёнок выходит с
        // кодом3), экземпляр=null — тик менеджера обязан ретраить по бэкоффу
        // (2с →4с →…) без единого изменения модели.
        var slot = new PluginSlot
        {
            Format = PluginFormat.Vst3,
            Path = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.vst3"),
            PluginId = "deadbeefdeadbeefdeadbeefdeadbeef",
            Name = "Нет модуля",
        };

        var harness = CreateHarness(slot);
        try
        {
            Assert.Null(harness.Manager.GetSlotInstance(harness.PluginId, 0));
            Assert.NotEmpty(harness.Manager.LastErrors);
            Assert.True(harness.Manager.GetSpawnAttempts(harness.PluginId) >= 1);

            var deadline = Environment.TickCount64 + 12_000;
            while (Environment.TickCount64 < deadline &&
                   harness.Manager.GetSpawnAttempts(harness.PluginId) < 2)
            {
                Thread.Sleep(100);
            }

            var attempts = harness.Manager.GetSpawnAttempts(harness.PluginId);
            Assert.True(attempts >= 2, $"ретрай спавна не состоялся за12 с (попыток: {attempts})");
            Assert.Null(harness.Manager.GetSlotInstance(harness.PluginId, 0));
        }
        finally
        {
            harness.Dispose();
        }
    }

    [Fact]
    public void ManagerRestartPluginNode_ReplacesExecutor_AndStatusTracksIt()
    {
        var dll = Path.Combine(AppContext.BaseDirectory, "test-plugin-vst3.dll");
        if (ProcessBridgePlugin.FindAppExe() is null || !File.Exists(dll))
        {
            return; // вне репозитория — проба моста пропущена
        }

        var descriptor = Vst3Loader.Enumerate(dll).Single(p => p.Name == "Parrhesia Test Gain");
        var harness = CreateHarness(new PluginSlot
        {
            Format = PluginFormat.Vst3,
            Path = descriptor.Path,
            PluginId = descriptor.PluginId,
            Name = descriptor.Name,
        });

        try
        {
            var before = harness.Manager.GetPluginStatus(harness.PluginId);
            Assert.True(before is { Loaded: true, Alive: true }, $"статус после спавна: {before}");
            Assert.True(before!.ProcessId > 0);
            Assert.Equal(1, before.SpawnAttempts);

            // Кнопка «Перезапустить» инспектора: флаг + немедленная сверка.
            harness.Manager.RestartPluginNode(harness.PluginId);

            PluginNodeStatus? after = null;
            var deadline = Environment.TickCount64 + 15_000;
            while (Environment.TickCount64 < deadline)
            {
                var status = harness.Manager.GetPluginStatus(harness.PluginId);
                if (status is { Loaded: true, Alive: true } && status.ProcessId != before.ProcessId)
                {
                    after = status;
                    break;
                }

                Thread.Sleep(100);
            }

            Assert.True(after is not null, "рестарт через инспектор не заменил исполнителя за15 с");
            Assert.True(after!.SpawnAttempts >= 2, $"попыток: {after.SpawnAttempts}");

            // Старый исполнитель обязан умереть — сирот после рестарта нет.
            var oldPid = before.ProcessId;
            deadline = Environment.TickCount64 + 5_000;
            while (ProcessAlive(oldPid) && Environment.TickCount64 < deadline)
            {
                Thread.Sleep(50);
            }

            Assert.False(ProcessAlive(oldPid), "старый исполнитель пережил рестарт");
        }
        finally
        {
            harness.Dispose();
        }
    }

    [Fact]
    public void PluginNode_Clap_RunsInChildProcess()
    {
        // S5: CLAP-ветка исполнителя — настоящий тестовый модуль в ребёнке.
        var dll = Path.Combine(AppContext.BaseDirectory, "test-plugin.dll");
        if (ProcessBridgePlugin.FindAppExe() is null || !File.Exists(dll))
        {
            return; // вне репозитория — проба моста пропущена
        }

        var harness = CreateHarness(new PluginSlot
        {
            Format = PluginFormat.Clap,
            Path = dll,
            PluginId = "com.parrhesia.test.latency",
            Name = "Тест CLAP",
        });

        try
        {
            Assert.Empty(harness.Manager.LastErrors);
            var bridge = Assert.IsType<ProcessBridgePlugin>(
                harness.Manager.GetSlotInstance(harness.PluginId, 0));
            Assert.True(bridge.ChildPid > 0, "исполнитель CLAP не запущен");

            // Ребёнок гоняет блоки через настоящий CLAP-бэкенд.
            harness.Processor.SetInput(harness.SourceId, new ConstantInput(0.5f));
            var deadline = Environment.TickCount64 + 10_000;
            while (Environment.TickCount64 < deadline && bridge.ProcessedBlocks < 1)
            {
                Process(harness.Processor, harness.SinkId);
                Thread.Sleep(10);
            }

            Assert.True(
                bridge.ProcessedBlocks >= 1,
                $"ребёнок не обработал ни одного блока (CLAP): drops={bridge.Drops}");
        }
        finally
        {
            harness.Dispose();
        }
    }

    /// <summary>Серия source → plugin-узел → sink со слотом; менеджер уже Prepare.</summary>
    private static Harness CreateHarness(PluginSlot slot)
    {
        var graph = new AudioGraph();
        var source = graph.AddNode("Вход", NodeKind.Source);
        var plugin = graph.AddNode("Обработка", NodeKind.Plugin);
        var sink = graph.AddNode("Выход", NodeKind.Sink);
        graph.AddRoute(source.Id, plugin.Id, out _);
        graph.AddRoute(plugin.Id, sink.Id, out _);
        graph.AddSlot(plugin.Id, slot);

        var processor = new GraphProcessor(graph);
        var manager = new SlotChainManager(graph, processor);
        manager.Prepare(48000, BlockFrames, 2);
        return new Harness(graph, processor, manager, source.Id, plugin.Id, sink.Id);
    }

    private sealed record Harness(
        AudioGraph Graph,
        GraphProcessor Processor,
        SlotChainManager Manager,
        Guid SourceId,
        Guid PluginId,
        Guid SinkId) : IDisposable
    {
        public void Dispose()
        {
            Manager.Dispose();
            Processor.Dispose();
        }
    }

    private static float[] Process(GraphProcessor processor, Guid sinkId, int frames = BlockFrames)
    {
        // Один вызов = один аудио-цикл: вручную переводим эпоху.
        processor.Invalidate();
        var output = new float[frames * 2];
        processor.ProcessBlock(sinkId, output, frames);
        return output;
    }

    /// <summary>Постоянный сигнал для проверки wet-ветки (0,5 → ×2 = 1,0).</summary>
    private sealed class ConstantInput(float value) : ISampleInput
    {
        public long UnderrunSamples => 0;

        public int Read(Span<float> destination)
        {
            destination.Fill(value);
            return destination.Length;
        }
    }

    private static bool ProcessAlive(int pid)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch
        {
            return false; // процесса нет — значит, завершился
        }
    }

    /// <summary>Есть ли окно процесса с заголовком-префиксом (S4b: «Редактор: …»).</summary>
    private static bool WindowWithTitleExists(int pid, string prefix)
    {
        var found = false;
        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out var windowPid);
            if (windowPid != (uint)pid)
            {
                return true;
            }

            var title = new StringBuilder(256);
            GetWindowText(hwnd, title, title.Capacity);
            if (title.ToString().StartsWith(prefix, StringComparison.Ordinal))
            {
                found = true;
                return false;
            }

            return true;
        }, IntPtr.Zero);

        return found;
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hwnd, System.Text.StringBuilder text, int maxCount);

    private static void TryKill(int pid)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Уже завершился.
        }
    }
}
