using Parrhesia.Core.Graph;

namespace Parrhesia.Core.Tests.Graph;

public class AudioGraphTests
{
    [Fact]
    public void AddNode_AddsToCollection_AndRaisesChanged()
    {
        var graph = new AudioGraph();
        GraphChange? change = null;
        graph.Changed += (_, e) => change = e;

        var node = graph.AddNode("Микрофон", NodeKind.Source);

        Assert.Single(graph.Nodes);
        Assert.Equal(GraphChangeKind.NodeAdded, change?.Kind);
        Assert.Same(node, change?.Node);
        Assert.True(node.HasOutput);
        Assert.False(node.HasInput);
    }

    [Fact]
    public void AddNode_DuplicateId_Throws()
    {
        var graph = new AudioGraph();
        var id = Guid.NewGuid();
        graph.AddNode("A", NodeKind.Source, id);

        Assert.Throws<InvalidOperationException>(() => graph.AddNode("B", NodeKind.Source, id));
    }

    [Fact]
    public void AddRoute_SourceToBus_Succeeds()
    {
        var graph = new AudioGraph();
        var source = graph.AddNode("Вход", NodeKind.Source);
        var bus = graph.AddNode("Шина", NodeKind.Bus);

        var error = graph.AddRoute(source.Id, bus.Id, out var route);

        Assert.Equal(RouteError.None, error);
        Assert.NotNull(route);
        Assert.Single(graph.Routes);
        Assert.Equal(1f, route!.Gain);
        Assert.True(route.Enabled);
    }

    [Fact]
    public void AddRoute_SourceToSink_Succeeds()
    {
        var graph = new AudioGraph();
        var source = graph.AddNode("Вход", NodeKind.Source);
        var sink = graph.AddNode("Выход", NodeKind.Sink);

        Assert.Equal(RouteError.None, graph.AddRoute(source.Id, sink.Id, out _));
    }

    [Fact]
    public void AddRoute_Duplicate_ReturnsDuplicate()
    {
        var graph = new AudioGraph();
        var source = graph.AddNode("Вход", NodeKind.Source);
        var bus = graph.AddNode("Шина", NodeKind.Bus);
        graph.AddRoute(source.Id, bus.Id, out _);

        Assert.Equal(RouteError.Duplicate, graph.AddRoute(source.Id, bus.Id, out var route));
        Assert.Null(route);
        Assert.Single(graph.Routes);
    }

    [Fact]
    public void AddRoute_SelfConnection_ReturnsSelfLoop()
    {
        var graph = new AudioGraph();
        var bus = graph.AddNode("Шина", NodeKind.Bus);

        Assert.Equal(RouteError.SelfLoop, graph.AddRoute(bus.Id, bus.Id, out _));
    }

    [Fact]
    public void AddRoute_SourceAsTarget_ReturnsNoInputPort()
    {
        var graph = new AudioGraph();
        var a = graph.AddNode("A", NodeKind.Source);
        var b = graph.AddNode("B", NodeKind.Source);

        Assert.Equal(RouteError.NoInputPort, graph.AddRoute(a.Id, b.Id, out _));
    }

    [Fact]
    public void AddRoute_SinkAsSource_ReturnsNoOutputPort()
    {
        var graph = new AudioGraph();
        var sink = graph.AddNode("Выход", NodeKind.Sink);
        var bus = graph.AddNode("Шина", NodeKind.Bus);

        Assert.Equal(RouteError.NoOutputPort, graph.AddRoute(sink.Id, bus.Id, out _));
    }

    [Fact]
    public void AddRoute_UnknownNode_ReturnsNodeNotFound()
    {
        var graph = new AudioGraph();
        var source = graph.AddNode("Вход", NodeKind.Source);

        Assert.Equal(RouteError.NodeNotFound, graph.AddRoute(source.Id, Guid.NewGuid(), out _));
    }

    [Fact]
    public void AddRoute_ClosingCycle_ReturnsCycle()
    {
        var graph = new AudioGraph();
        var a = graph.AddNode("A", NodeKind.Bus);
        var b = graph.AddNode("B", NodeKind.Bus);
        var c = graph.AddNode("C", NodeKind.Bus);
        graph.AddRoute(a.Id, b.Id, out _);
        graph.AddRoute(b.Id, c.Id, out _);

        Assert.Equal(RouteError.Cycle, graph.AddRoute(c.Id, a.Id, out _));
        Assert.Equal(2, graph.Routes.Count);
    }

    [Fact]
    public void RemoveNode_RemovesAttachedRoutes()
    {
        var graph = new AudioGraph();
        var source = graph.AddNode("Вход", NodeKind.Source);
        var bus = graph.AddNode("Шина", NodeKind.Bus);
        var sink = graph.AddNode("Выход", NodeKind.Sink);
        graph.AddRoute(source.Id, bus.Id, out _);
        graph.AddRoute(bus.Id, sink.Id, out _);

        var removed = graph.RemoveNode(bus.Id);

        Assert.True(removed);
        Assert.Empty(graph.Routes);
        Assert.Equal(2, graph.Nodes.Count);
        Assert.False(graph.RemoveNode(bus.Id));
    }

    [Fact]
    public void HasPath_TraversesForward_Only()
    {
        var graph = new AudioGraph();
        var a = graph.AddNode("A", NodeKind.Bus);
        var b = graph.AddNode("B", NodeKind.Bus);
        var c = graph.AddNode("C", NodeKind.Bus);
        graph.AddRoute(a.Id, b.Id, out _);
        graph.AddRoute(b.Id, c.Id, out _);

        Assert.True(graph.HasPath(a.Id, c.Id));
        Assert.False(graph.HasPath(c.Id, a.Id));
        Assert.True(graph.HasPath(a.Id, a.Id));
    }

    [Fact]
    public void SetNodeMute_MakesNodeEffectivelyMuted()
    {
        var graph = new AudioGraph();
        var a = graph.AddNode("A", NodeKind.Source);
        var b = graph.AddNode("B", NodeKind.Source);

        graph.SetNodeMute(a.Id, true);

        Assert.True(graph.IsEffectivelyMuted(graph.FindNode(a.Id)!));
        Assert.False(graph.IsEffectivelyMuted(graph.FindNode(b.Id)!));
    }

    [Fact]
    public void Solo_MutesOtherNodes_UntilCleared()
    {
        var graph = new AudioGraph();
        var a = graph.AddNode("A", NodeKind.Source);
        var b = graph.AddNode("B", NodeKind.Source);
        var bus = graph.AddNode("Шина", NodeKind.Bus);

        graph.SetNodeSolo(a.Id, true);

        Assert.False(graph.IsEffectivelyMuted(graph.FindNode(a.Id)!));
        Assert.True(graph.IsEffectivelyMuted(graph.FindNode(b.Id)!));
        Assert.False(graph.IsEffectivelyMuted(graph.FindNode(bus.Id)!),
            "Соло не должно глушить шины и назначения");

        graph.SetNodeSolo(a.Id, false);

        Assert.False(graph.IsEffectivelyMuted(graph.FindNode(b.Id)!));
    }

    [Fact]
    public void SetRouteGain_ClampsToZero_AndRaisesChanged()
    {
        var graph = new AudioGraph();
        var source = graph.AddNode("Вход", NodeKind.Source);
        var sink = graph.AddNode("Выход", NodeKind.Sink);
        graph.AddRoute(source.Id, sink.Id, out var route);

        GraphChange? change = null;
        graph.Changed += (_, e) => change = e;
        graph.SetRouteGain(source.Id, sink.Id, -3f);

        Assert.Equal(0f, route!.Gain);
        Assert.Equal(GraphChangeKind.RouteChanged, change?.Kind);
        Assert.Same(route, change?.Route);
    }

    [Fact]
    public void SetRouteEnabled_TogglesRoute()
    {
        var graph = new AudioGraph();
        var source = graph.AddNode("Вход", NodeKind.Source);
        var sink = graph.AddNode("Выход", NodeKind.Sink);
        graph.AddRoute(source.Id, sink.Id, out var route);

        graph.SetRouteEnabled(source.Id, sink.Id, false);

        Assert.False(route!.Enabled);
    }

    [Fact]
    public void SetNodeDevice_BindsAndRaisesChanged()
    {
        var graph = new AudioGraph();
        var node = graph.AddNode("Вход", NodeKind.Source);
        GraphChange? change = null;
        graph.Changed += (_, e) => change = e;

        graph.SetNodeDevice(node.Id, "default:capture");

        Assert.Equal("default:capture", node.DeviceId);
        Assert.Equal(GraphChangeKind.NodeChanged, change?.Kind);

        graph.SetNodeDevice(node.Id, null);
        Assert.Null(node.DeviceId);
    }

    [Fact]
    public void NodeGain_IsClampedToNonNegative()
    {
        var graph = new AudioGraph();
        var node = graph.AddNode("Вход", NodeKind.Source);

        graph.SetNodeGain(node.Id, -1f);

        Assert.Equal(0f, node.Gain);
    }
}
