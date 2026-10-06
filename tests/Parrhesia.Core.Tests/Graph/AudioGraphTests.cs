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
    public void AddRoute_Default_HasDirectChannelMap()
    {
        var graph = new AudioGraph();
        var source = graph.AddNode("Вход", NodeKind.Source);
        var bus = graph.AddNode("Шина", NodeKind.Bus);

        graph.AddRoute(source.Id, bus.Id, out var route);

        Assert.Equal(ChannelMap.Diagonal(2, 2), route!.Map);
    }

    [Fact]
    public void AddRoute_WithChannel_CreatesRouteWithSinglePair()
    {
        var graph = new AudioGraph();
        var source = graph.AddNode("Вход", NodeKind.Source);
        var sink = graph.AddNode("Выход", NodeKind.Sink);

        var error = graph.AddRoute(source.Id, fromChannel: ChannelMap.Left, sink.Id, toChannel: ChannelMap.Right, out var route);

        Assert.Equal(RouteError.None, error);
        Assert.Equal(ChannelMap.Pair(ChannelMap.Left, ChannelMap.Right), route!.Map);
        Assert.Single(graph.Routes);
    }

    [Fact]
    public void AddRoute_WithChannel_ExtendsExistingRouteMap()
    {
        var graph = new AudioGraph();
        var source = graph.AddNode("Вход", NodeKind.Source);
        var sink = graph.AddNode("Выход", NodeKind.Sink);
        graph.AddRoute(source.Id, sink.Id, out var route);

        GraphChange? change = null;
        graph.Changed += (_, e) => change = e;

        // Повторное добавление той же пары — идемпотентно, без события.
        Assert.Equal(RouteError.None, graph.AddRoute(source.Id, 0, sink.Id, 0, out var same));
        Assert.Same(route, same);
        Assert.Null(change);

        // Новая пара расширяет карту.
        Assert.Equal(RouteError.None, graph.AddRoute(source.Id, 0, sink.Id, 1, out _));
        Assert.True(route!.Map.Has(0, 0));
        Assert.True(route.Map.Has(0, 1));
        Assert.Equal(GraphChangeKind.RouteChanged, change?.Kind);
        Assert.Single(graph.Routes);
    }

    [Fact]
    public void AddRoute_WithChannel_StillValidatesTopology()
    {
        var graph = new AudioGraph();
        var source = graph.AddNode("Вход", NodeKind.Source);
        var otherSource = graph.AddNode("Вход2", NodeKind.Source);
        var sink = graph.AddNode("Выход", NodeKind.Sink);

        Assert.Equal(RouteError.NoOutputPort, graph.AddRoute(sink.Id, 1, source.Id, 0, out _));
        Assert.Equal(RouteError.NoInputPort, graph.AddRoute(source.Id, 0, otherSource.Id, 1, out _));
        Assert.Empty(graph.Routes);
    }

    [Fact]
    public void AddRoute_InvalidChannel_Throws()
    {
        var graph = new AudioGraph();
        var source = graph.AddNode("Вход", NodeKind.Source);
        var sink = graph.AddNode("Выход", NodeKind.Sink);

        Assert.Throws<ArgumentOutOfRangeException>(() => graph.AddRoute(source.Id, 8, sink.Id, 0, out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => ChannelMap.Pair(0, 8));
    }

    [Fact]
    public void SetRouteChannel_DisablingOne_KeepsRoute_WithMonoMap()
    {
        var graph = new AudioGraph();
        var source = graph.AddNode("Вход", NodeKind.Source);
        var sink = graph.AddNode("Выход", NodeKind.Sink);
        graph.AddRoute(source.Id, sink.Id, out var route);

        GraphChange? change = null;
        graph.Changed += (_, e) => change = e;
        graph.SetRouteChannel(source.Id, sink.Id, ChannelMap.Left, ChannelMap.Left, enabled: false);

        Assert.Equal(ChannelMap.Pair(ChannelMap.Right, ChannelMap.Right), route!.Map);
        Assert.Equal(GraphChangeKind.RouteChanged, change?.Kind);
        Assert.Single(graph.Routes);
    }

    [Fact]
    public void SetRouteChannel_DisablingLastPair_RemovesRoute()
    {
        var graph = new AudioGraph();
        var source = graph.AddNode("Вход", NodeKind.Source);
        var sink = graph.AddNode("Выход", NodeKind.Sink);
        graph.AddRoute(source.Id, sink.Id, out _);

        GraphChange? change = null;
        graph.Changed += (_, e) => change = e;
        graph.SetRouteChannel(source.Id, sink.Id, ChannelMap.Left, ChannelMap.Left, enabled: false);
        graph.SetRouteChannel(source.Id, sink.Id, ChannelMap.Right, ChannelMap.Right, enabled: false);

        Assert.Empty(graph.Routes);
        Assert.Equal(GraphChangeKind.RouteRemoved, change?.Kind);
    }

    [Fact]
    public void SetRouteChannel_UnknownRoute_Throws()
    {
        var graph = new AudioGraph();

        Assert.Throws<ArgumentException>(() =>
            graph.SetRouteChannel(Guid.NewGuid(), Guid.NewGuid(), 0, 0, enabled: false));
    }

    [Fact]
    public void SetNodePosition_StoresCoordinates_AndRaisesChanged()
    {
        var graph = new AudioGraph();
        var node = graph.AddNode("Вход", NodeKind.Source);
        GraphChange? change = null;
        graph.Changed += (_, e) => change = e;

        graph.SetNodePosition(node.Id, 120.5, -40.25);

        Assert.Equal(120.5, node.X);
        Assert.Equal(-40.25, node.Y);
        Assert.Equal(GraphChangeKind.NodeChanged, change?.Kind);

        Assert.Throws<ArgumentOutOfRangeException>(() => graph.SetNodePosition(node.Id, double.NaN, 0));
        Assert.Throws<ArgumentException>(() => graph.SetNodePosition(Guid.NewGuid(), 1, 1));
    }

    [Fact]
    public void ReplaceWith_CopiesEverything_AndRaisesSingleReset()
    {
        var source = new AudioGraph();
        var a = source.AddNode("A", NodeKind.Source);
        var b = source.AddNode("B", NodeKind.Sink);
        source.SetNodeDevice(a.Id, "default:capture");
        source.SetNodePosition(a.Id, 10, 20);
        source.SetNodeGain(a.Id, 0.5f);
        source.AddRoute(a.Id, 0, b.Id, 1, out _);

        var target = new AudioGraph();
        var old = target.AddNode("Старый", NodeKind.Bus);
        target.AddNode("Старый2", NodeKind.Bus);
        target.AddRoute(old.Id, target.Nodes[1].Id, out _);

        var changes = new List<GraphChangeKind>();
        target.Changed += (_, e) => changes.Add(e.Kind);

        target.ReplaceWith(source);

        Assert.Equal(new[] { GraphChangeKind.Reset }, changes);
        Assert.Equal(2, target.Nodes.Count);
        var restoredA = target.FindNode(a.Id);
        Assert.NotNull(restoredA);
        Assert.Equal("default:capture", restoredA!.DeviceId);
        Assert.Equal(10.0, restoredA.X);
        Assert.Equal(0.5f, restoredA.Gain, 3);
        Assert.Single(target.Routes);
        Assert.Equal(ChannelMap.Pair(0, 1), target.Routes[0].Map);
    }

    [Fact]
    public void ReplaceWith_Self_IsNoOpWithoutEvent()
    {
        var graph = new AudioGraph();
        graph.AddNode("A", NodeKind.Source);
        var raised = false;
        graph.Changed += (_, _) => raised = true;

        graph.ReplaceWith(graph);

        Assert.False(raised);
        Assert.Single(graph.Nodes);
    }

    [Fact]
    public void SetRouteMap_ReplacesAtomically_WithoutIntermediateRemoval()
    {
        var graph = new AudioGraph();
        var source = graph.AddNode("Вход", NodeKind.Source);
        var sink = graph.AddNode("Выход", NodeKind.Sink);
        graph.AddRoute(source.Id, sink.Id, out var route);

        var kinds = new List<GraphChangeKind>();
        graph.Changed += (_, e) => kinds.Add(e.Kind);

        // Прямое переключение на кросс: промежуточного пустого состояния нет.
        graph.SetRouteMap(source.Id, sink.Id, ChannelMap.Pair(0, 1).With(1, 0, true));

        Assert.Equal(ChannelMap.Pair(0, 1).With(1, 0, enabled: true).Bits, route!.Map.Bits);
        Assert.Equal(new[] { GraphChangeKind.RouteChanged }, kinds);
        Assert.Single(graph.Routes);
    }

    [Fact]
    public void SetRouteMap_Empty_RemovesRoute_SameMap_NoEvent()
    {
        var graph = new AudioGraph();
        var source = graph.AddNode("Вход", NodeKind.Source);
        var sink = graph.AddNode("Выход", NodeKind.Sink);
        graph.AddRoute(source.Id, sink.Id, out _);

        var kinds = new List<GraphChangeKind>();
        graph.Changed += (_, e) => kinds.Add(e.Kind);

        graph.SetRouteMap(source.Id, sink.Id, ChannelMap.Diagonal(2, 2)); // как есть — без события
        Assert.Empty(kinds);

        graph.SetRouteMap(source.Id, sink.Id, new ChannelMap(0));
        Assert.Equal(new[] { GraphChangeKind.RouteRemoved }, kinds);
        Assert.Empty(graph.Routes);
    }

    [Fact]
    public void AddRoute_ChannelOutOfRange_ReturnsError()
    {
        var graph = new AudioGraph();
        var mono = graph.AddNode("Моно", NodeKind.Source);
        var sink = graph.AddNode("Выход", NodeKind.Sink);
        graph.SetNodeChannels(mono.Id, 1);

        Assert.Equal(RouteError.ChannelOutOfRange, graph.AddRoute(mono.Id, 1, sink.Id, 1, out _));
        Assert.Equal(RouteError.None, graph.AddRoute(mono.Id, 0, sink.Id, 1, out _));
    }

    [Fact]
    public void SetNodeChannels_MonoToStereo_ExtendsNamesAndKeepsRoutes()
    {
        var graph = new AudioGraph();
        var source = graph.AddNode("Вход", NodeKind.Source);
        var sink = graph.AddNode("Выход", NodeKind.Sink);
        graph.AddRoute(source.Id, sink.Id, out var route);

        graph.SetNodeChannels(source.Id, 1, ["Микрофон"]);

        Assert.Equal(1, graph.FindNode(source.Id)!.ChannelCount);
        Assert.Equal(new[] { "Микрофон" }, graph.FindNode(source.Id)!.ChannelNames);
        // Стерео-карта обрезана до моно: остаётся только 1→1.
        Assert.Equal(ChannelMap.Pair(0, 0), route!.Map);

        graph.SetNodeChannels(source.Id, 2);

        Assert.Equal(new[] { "Микрофон", "2" }, graph.FindNode(source.Id)!.ChannelNames);
        Assert.Equal(ChannelMap.Pair(0, 0), route!.Map);
    }

    [Fact]
    public void SetNodeChannels_RemovesRoutesThatBecameEmpty()
    {
        var graph = new AudioGraph();
        var source = graph.AddNode("Вход", NodeKind.Source);
        var sink = graph.AddNode("Выход", NodeKind.Sink);

        // Связь только по второму каналу.
        graph.AddRoute(source.Id, 1, sink.Id, 1, out _);
        Assert.Single(graph.Routes);

        graph.SetNodeChannels(source.Id, 1);

        Assert.Empty(graph.Routes);
    }

    [Fact]
    public void SetNodeChannels_RejectsBadCountAndNameLength()
    {
        var graph = new AudioGraph();
        var node = graph.AddNode("Вход", NodeKind.Source);

        Assert.Throws<ArgumentOutOfRangeException>(() => graph.SetNodeChannels(node.Id, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => graph.SetNodeChannels(node.Id, 3));
        Assert.Throws<ArgumentException>(() => graph.SetNodeChannels(node.Id, 1, ["a", "b"]));
    }

    [Fact]
    public void SetNodeChannelName_RenamesAndRaisesChanged()
    {
        var graph = new AudioGraph();
        var node = graph.AddNode("Вход", NodeKind.Source);

        GraphChange? change = null;
        graph.Changed += (_, e) => change = e;
        graph.SetNodeChannelName(node.Id, 1, "Правый");

        Assert.Equal(new[] { "1", "Правый" }, node.ChannelNames);
        Assert.Equal(GraphChangeKind.NodeChanged, change?.Kind);

        Assert.Throws<ArgumentOutOfRangeException>(() => graph.SetNodeChannelName(node.Id, 5, "x"));
        Assert.Throws<ArgumentException>(() => graph.SetNodeChannelName(node.Id, 0, "  "));
    }

    [Fact]
    public void SetNodeBypass_TogglesAndRaisesChanged()
    {
        var graph = new AudioGraph();
        var node = graph.AddNode("Шина", NodeKind.Bus);
        GraphChange? change = null;
        graph.Changed += (_, e) => change = e;

        graph.SetNodeBypass(node.Id, true);

        Assert.True(node.Bypassed);
        Assert.Equal(GraphChangeKind.NodeChanged, change?.Kind);

        graph.SetNodeBypass(node.Id, false);
        Assert.False(node.Bypassed);
    }

    [Fact]
    public void SetRouteMap_SanitizesPairsOutsideChannelCounts()
    {
        var graph = new AudioGraph();
        var source = graph.AddNode("Вход", NodeKind.Source);
        var sink = graph.AddNode("Выход", NodeKind.Sink);
        graph.AddRoute(source.Id, sink.Id, out var route);

        // Пытаемся навесить пару в несуществующий второй канал... у 2-канального она валидна,
        // поэтому сначала делаем назначение моно.
        graph.SetNodeChannels(sink.Id, 1);
        graph.SetRouteMap(source.Id, sink.Id, ChannelMap.Pair(1, 1));

        // (1,1) отброшен парой Restrict(2,1)... остаётся пусто → маршрут удалён.
        Assert.Empty(graph.Routes);
        _ = route;
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
