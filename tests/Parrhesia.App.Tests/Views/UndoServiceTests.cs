using Parrhesia.App.Views;
using Parrhesia.Core.Graph;

namespace Parrhesia.App.Tests.Views;

/// <summary>
/// U2: отмена последнего удаления (UndoService) — одиночный кабель с гейном
/// и узел-плагин со слотами, свойствами и его кабелями.
/// </summary>
public class UndoServiceTests
{
    public UndoServiceTests()
    {
        // Статический буфер между тестами: сливаем в пустой граф (хлам не важен).
        UndoService.TryUndo(new AudioGraph());
    }

    [Fact]
    public void Undo_RestoresDeletedRoute_WithGainAndEnabled()
    {
        var graph = new AudioGraph();
        var source = graph.AddNode("Вход", NodeKind.Source);
        var sink = graph.AddNode("Выход", NodeKind.Sink);
        Assert.Equal(RouteError.None, graph.AddRoute(source.Id, sink.Id, out _));
        graph.SetRouteGain(source.Id, sink.Id, 0.5f);

        UndoService.StoreDeletedRoute(graph, source.Id, sink.Id);
        Assert.True(graph.RemoveRoute(source.Id, sink.Id));
        Assert.Empty(graph.Routes);

        Assert.True(UndoService.TryUndo(graph));

        var route = Assert.Single(graph.Routes);
        Assert.Equal(source.Id, route.FromId);
        Assert.Equal(sink.Id, route.ToId);
        Assert.Equal(0.5f, route.Gain, 3);
        Assert.False(UndoService.HasBuffer);
    }

    [Fact]
    public void Undo_RestoresDeletedNode_WithSlotsPropertiesAndRoutes()
    {
        var graph = new AudioGraph();
        var source = graph.AddNode("Вход", NodeKind.Source);
        var plugin = graph.AddNode("Компрессор", NodeKind.Plugin);
        var sink = graph.AddNode("Выход", NodeKind.Sink);
        graph.AddRoute(source.Id, plugin.Id, out _);
        graph.AddRoute(plugin.Id, sink.Id, out _);
        graph.AddSlot(plugin.Id, new PluginSlot
        {
            Path = "x.vst3",
            PluginId = "abc",
            Name = "X",
            State = [1, 2],
        });
        graph.SetNodePosition(plugin.Id, 123, 45);
        graph.SetNodeGain(plugin.Id, 0.25f);

        UndoService.StoreDeletedNode(graph, plugin.Id);
        Assert.True(graph.RemoveNode(plugin.Id));
        Assert.Equal(2, graph.Nodes.Count); // Вход и Выход остались
        Assert.DoesNotContain(graph.Nodes, n => n.Kind == NodeKind.Plugin);
        Assert.Empty(graph.Routes); // оба кабеля узла ушли вместе с ним

        Assert.True(UndoService.TryUndo(graph));

        var restored = Assert.Single(graph.Nodes, n => n.Name == "Компрессор");
        Assert.Equal("Компрессор", restored.Name);
        Assert.Equal(NodeKind.Plugin, restored.Kind);
        Assert.Equal(0.25f, restored.Gain, 3);
        Assert.Equal(123d, restored.X!.Value);

        var slot = Assert.Single(restored.Slots);
        Assert.Equal("x.vst3", slot.Path);
        Assert.Equal(new byte[] { 1, 2 }, slot.State);

        Assert.Equal(2, graph.Routes.Count);
        Assert.Contains(graph.Routes, r => r.FromId == source.Id && r.ToId == plugin.Id);
        Assert.Contains(graph.Routes, r => r.FromId == plugin.Id && r.ToId == sink.Id);
    }

    [Fact]
    public void Undo_EmptyBuffer_ReturnsFalse()
    {
        Assert.False(UndoService.TryUndo(new AudioGraph()));
    }

    [Fact]
    public void Undo_LastDeletionWins()
    {
        // Буфер одиночный: после удаления узла поверх кабеля вернётся узел.
        var graph = new AudioGraph();
        var source = graph.AddNode("Вход", NodeKind.Source);
        var sink = graph.AddNode("Выход", NodeKind.Sink);
        graph.AddRoute(source.Id, sink.Id, out _);
        UndoService.StoreDeletedRoute(graph, source.Id, sink.Id);
        graph.RemoveRoute(source.Id, sink.Id);

        var extra = graph.AddNode("Лишний", NodeKind.Sink);
        UndoService.StoreDeletedNode(graph, extra.Id);
        graph.RemoveNode(extra.Id);

        Assert.True(UndoService.TryUndo(graph));
        Assert.Contains(graph.Nodes, n => n.Name == "Лишний"); // вернулся узел
        Assert.Empty(graph.Routes); // кабель НЕ вернулся — буфер одиночный

        Assert.False(UndoService.TryUndo(graph));
    }
}
