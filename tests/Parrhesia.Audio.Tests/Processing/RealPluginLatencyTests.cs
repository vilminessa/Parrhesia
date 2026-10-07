using Parrhesia.Audio.Processing;
using Parrhesia.Core.Graph;
using Parrhesia.Plugins;
using Parrhesia.Plugins.Clap;

namespace Parrhesia.Audio.Tests.Processing;

/// <summary>
/// Сквозной тест: настоящий нативный CLAP-плагин (задержка128) в цепочке шины —
/// компенсация латентности выравнивает сухую и влажную ветки по фазе.
/// Требуется tests/Parrhesia.Plugins.Native/build-test-plugin.bat.
/// </summary>
public class RealPluginLatencyTests
{
    private const int BlockFrames = 64;

    [Fact]
    public void RealClapLatency_AlignsWithDryPath()
    {
        var dll = Path.Combine(AppContext.BaseDirectory, "test-plugin.dll");
        Assert.True(
            File.Exists(dll),
            "test-plugin.dll не найден — соберите tests/Parrhesia.Plugins.Native/build-test-plugin.bat");

        var graph = new AudioGraph();
        var source = graph.AddNode("Вход", NodeKind.Source);
        var bus = graph.AddNode("Шина", NodeKind.Bus);
        var sink = graph.AddNode("Выход", NodeKind.Sink);
        graph.AddRoute(source.Id, sink.Id, out _);    // сухая
        graph.AddRoute(source.Id, bus.Id, out _);     // влажная
        graph.AddRoute(bus.Id, sink.Id, out _);
        graph.AddSlot(bus.Id, new PluginSlot
        {
            Format = PluginFormat.Clap,
            Path = dll,
            PluginId = "com.parrhesia.test.latency",
            Name = "Тест Latency",
        });

        using var processor = new GraphProcessor(graph);
        using var manager = new SlotChainManager(
            graph,
            processor,
            slot => ClapLoader.Load(slot.Path, slot.PluginId));
        manager.Prepare(48000, 480, 2);
        Assert.Empty(manager.LastErrors);

        processor.SetInput(source.Id, new ImpulseInput());

        var first = Process(processor, sink.Id);
        var second = Process(processor, sink.Id);
        var third = Process(processor, sink.Id);

        // Латентность128 кадров = ровно2 блока по64: импульс (кадр0) выходит
        // в третьем блоке, и сухая ветка (компенсация128) совпадает с плагином.
        Assert.All(first, s => Assert.Equal(0f, s));
        Assert.All(second, s => Assert.Equal(0f, s));
        Assert.Equal(2f, third[0], 3);
        Assert.Equal(2f, third[1], 3);
        Assert.Equal(0f, third[2], 3);
    }

    private static float[] Process(GraphProcessor processor, Guid sinkId, int frames = BlockFrames)
    {
        var output = new float[frames * 2];
        processor.ProcessBlock(sinkId, output, frames);
        return output;
    }

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
