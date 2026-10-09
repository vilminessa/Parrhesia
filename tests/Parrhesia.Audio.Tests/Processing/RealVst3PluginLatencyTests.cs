using Parrhesia.Audio.Processing;
using Parrhesia.Core.Graph;
using Parrhesia.Plugins;
using Parrhesia.Plugins.Vst3;

namespace Parrhesia.Audio.Tests.Processing;

/// <summary>
/// Сквозной тест A4: настоящий VST3-плагин (нативная задержка128) в цепочке
/// шины через дефолтную фабрику SlotChainManager (ветка PluginFormat.Vst3).
/// Компенсация латентности графа выравнивает сухую и влажную ветки:
/// импульс выходит в третьем блоке с суммой2,0.
/// </summary>
public class RealVst3PluginLatencyTests
{
    private const int BlockFrames = 64;

    [Fact]
    public void RealVst3LatencyPlugin_AlignsWithDryPath()
    {
        var dll = Path.Combine(AppContext.BaseDirectory, "test-plugin-vst3.dll");
        Assert.True(
            File.Exists(dll),
            "test-plugin-vst3.dll не найден — соберите tests/Parrhesia.Plugins.Native/vst3-test-plugin/build-vst3-test-plugin.bat");

        // Идентичность берём из реального перечисления — не хардкодим CID.
        var descriptor = Vst3Loader.Enumerate(dll).Single(p => p.Name == "Parrhesia Test Latency");

        var graph = new AudioGraph();
        var source = graph.AddNode("Вход", NodeKind.Source);
        var bus = graph.AddNode("Шина", NodeKind.Bus);
        var sink = graph.AddNode("Выход", NodeKind.Sink);
        graph.AddRoute(source.Id, sink.Id, out _);    // сухая
        graph.AddRoute(source.Id, bus.Id, out _);     // влажная
        graph.AddRoute(bus.Id, sink.Id, out _);
        graph.AddSlot(bus.Id, new PluginSlot
        {
            Format = PluginFormat.Vst3,
            Path = descriptor.Path,
            PluginId = descriptor.PluginId,
            Name = descriptor.Name,
        });

        using var processor = new GraphProcessor(graph);

        // Дефолтная фабрика менеджера — без кастомного factory: именно это
        // и проверяется (LoadPluginByFormat → Vst3Loader).
        using var manager = new SlotChainManager(graph, processor);
        manager.Prepare(48000, 480, 2);
        Assert.Empty(manager.LastErrors);

        processor.SetInput(source.Id, new ImpulseInput());

        var first = Process(processor, sink.Id);
        var second = Process(processor, sink.Id);
        var third = Process(processor, sink.Id);

        Assert.All(first, s => Assert.Equal(0f, s));
        Assert.All(second, s => Assert.Equal(0f, s));
        Assert.Equal(2f, third[0], 3);
        Assert.Equal(2f, third[1], 3);
        Assert.Equal(0f, third[2], 3);
    }

    private static float[] Process(GraphProcessor processor, Guid sinkId, int frames = BlockFrames)
    {
        // Один вызов = один аудио-цикл: вручную переводим эпоху.
        processor.Invalidate();
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
}
