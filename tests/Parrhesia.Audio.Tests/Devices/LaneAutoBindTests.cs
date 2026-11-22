using Parrhesia.Audio.Devices;
using Parrhesia.Audio.Engine;
using Parrhesia.Core.Graph;

namespace Parrhesia.Audio.Tests.Devices;

/// <summary>Ф4: авто-биндинг стартовых узлов на кабели.</summary>
public class LaneAutoBindTests
{
    private static LaneEndpoint In(int lane, string id) =>
        new(lane, id, DeviceFlow.Render, $"Parrhesia In{lane}");

    private static LaneEndpoint Out(int lane, string id) =>
        new(lane, id, DeviceFlow.Capture, $"Parrhesia Out{lane}");

    [Fact]
    public void Source_BindsLoopbackOfFirstInLane()
    {
        var lanes = new List<LaneEndpoint> { In(1, "{in1}"), Out(1, "{out1}"), In(2, "{in2}") };

        Assert.Equal("loopback:{in1}", LaneAutoBind.SourceDeviceId(lanes));
    }

    [Fact]
    public void Source_NoRenderLane_ReturnsNull()
    {
        Assert.Null(LaneAutoBind.SourceDeviceId([]));
        Assert.Null(LaneAutoBind.SourceDeviceId([Out(1, "{out1}")]));
    }

    [Fact]
    public void Sink_BindsVirtualMicrophone_WhenCaptureLaneExists()
    {
        var lanes = new List<LaneEndpoint> { In(1, "{in1}"), Out(1, "{out1}") };

        Assert.Equal(DeviceSpec.VirtualParrhesia, LaneAutoBind.SinkDeviceId(lanes));
    }

    [Fact]
    public void Sink_NoCaptureLane_ReturnsNull()
    {
        Assert.Null(LaneAutoBind.SinkDeviceId([]));
        Assert.Null(LaneAutoBind.SinkDeviceId([In(1, "{in1}")]));
    }

    [Fact]
    public void BindUnbound_BindsByContract_KeepsExistingAndRenamed()
    {
        var graph = new AudioGraph();
        var browser = graph.AddNode("Браузер", NodeKind.Source);
        var already = graph.AddNode("Браузер", NodeKind.Source);
        graph.SetNodeDevice(already.Id, DeviceSpec.DefaultCapture);
        var renamed = graph.AddNode("Мой плеер", NodeKind.Source);
        var discord = graph.AddNode("Discord (вирт.)", NodeKind.Sink);

        var bound = LaneAutoBind.BindUnbound(graph, [In(1, "{in1}"), Out(1, "{out1}")]);

        Assert.Equal(2, bound);
        Assert.Equal("loopback:{in1}", graph.FindNode(browser.Id)!.DeviceId);
        Assert.Equal(DeviceSpec.DefaultCapture, graph.FindNode(already.Id)!.DeviceId); // не тронут
        Assert.Null(graph.FindNode(renamed.Id)!.DeviceId); // вне контракта — не тронут
        Assert.Equal(DeviceSpec.VirtualParrhesia, graph.FindNode(discord.Id)!.DeviceId);
    }

    [Fact]
    public void BindUnbound_NoDriver_BindsNothing()
    {
        var graph = new AudioGraph();
        graph.AddNode("Браузер", NodeKind.Source);
        graph.AddNode("Discord (вирт.)", NodeKind.Sink);

        Assert.Equal(0, LaneAutoBind.BindUnbound(graph, []));
        Assert.All(graph.Nodes, n => Assert.Null(n.DeviceId));
    }
}
