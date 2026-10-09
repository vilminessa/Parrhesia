using System.Diagnostics;
using Parrhesia.Audio.Processing;
using Parrhesia.Core.Graph;
using Parrhesia.Plugins;
using Parrhesia.Plugins.Vst3;

namespace Parrhesia.Audio.Tests.Processing;

/// <summary>
/// S2 (S-волна): сквозной тест — узел-плагин (NodeKind.Plugin) исполняется
/// в дочернем процессе (App.exe --plugin-host) через реальный VST3-модуль.
/// Единый механизм без исключений: спавн, state через управляющий канал,
/// wet-обработка по мосту, смерть исполнителя при Dispose узла.
/// Пропускается, если App.exe или тестовый плагин не собраны.
/// </summary>
public class ProcessBridgePluginTests
{
    private const int BlockFrames = 480; //10мс @48кГц

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
            Assert.Empty(manager.LastErrors);

            var bridge = Assert.IsType<ProcessBridgePlugin>(manager.GetSlotInstance(plugin.Id, 0));
            pid = bridge.ChildPid;
            Assert.True(pid > 0, "процесс-исполнитель не запущен");

            // Латентность моста — один аудио-блок конвейера (для компенсации S3).
            Assert.Equal(BlockFrames, bridge.LatencySamples);

            // State хост → ребёнок → хост (named pipe) на живом VST3.
            var state = bridge.GetState();
            Assert.NotNull(state);
            bridge.SetState(state);
            Assert.Equal(state, bridge.GetState());

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
