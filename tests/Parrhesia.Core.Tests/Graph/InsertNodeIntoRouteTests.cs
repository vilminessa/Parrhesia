using Parrhesia.Core.Graph;

namespace Parrhesia.Core.Tests.Graph;

/// <summary>
/// S4 (S-волна): авто-wire — врезка узла-обработки в разрыв кабеля
/// с сохранением гейна и корректным откатом при невалидной врезке.
/// </summary>
public class InsertNodeIntoRouteTests
{
    [Fact]
    public void Insert_SplitsRoute_AndCarriesGain()
    {
        var graph = new AudioGraph();
        var source = graph.AddNode("Вход", NodeKind.Source);
        var sink = graph.AddNode("Выход", NodeKind.Sink);
        var plugin = graph.AddNode("Плагин", NodeKind.Plugin);
        Assert.Equal(RouteError.None, graph.AddRoute(source.Id, sink.Id, out _));
        graph.SetRouteGain(source.Id, sink.Id, 0.5f);

        Assert.True(graph.InsertNodeIntoRoute(source.Id, sink.Id, plugin.Id));

        Assert.DoesNotContain(graph.Routes, r => r.FromId == source.Id && r.ToId == sink.Id);
        var first = Assert.Single(graph.Routes, r => r.FromId == source.Id && r.ToId == plugin.Id);
        Assert.Equal(0.5f, first.Gain, 3);
        Assert.Contains(graph.Routes, r => r.FromId == plugin.Id && r.ToId == sink.Id);
    }

    [Fact]
    public void Insert_MissingRoute_ReturnsFalse()
    {
        var graph = new AudioGraph();
        var a = graph.AddNode("A", NodeKind.Source);
        var b = graph.AddNode("B", NodeKind.Sink);
        var middle = graph.AddNode("Середина", NodeKind.Plugin);

        Assert.False(graph.InsertNodeIntoRoute(a.Id, b.Id, middle.Id));
        Assert.Empty(graph.Routes);
    }

    [Fact]
    public void Insert_SinkAsMiddle_RollsBackOriginalRoute()
    {
        var graph = new AudioGraph();
        var source = graph.AddNode("Вход", NodeKind.Source);
        var sink = graph.AddNode("Выход", NodeKind.Sink);
        var other = graph.AddNode("Вывод2", NodeKind.Sink);
        Assert.Equal(RouteError.None, graph.AddRoute(source.Id, sink.Id, out _));

        // Приёмник нельзя поставить в разрыв (нет выхода) — кабель должен вернуться.
        Assert.False(graph.InsertNodeIntoRoute(source.Id, sink.Id, other.Id));

        Assert.Contains(graph.Routes, r => r.FromId == source.Id && r.ToId == sink.Id);
        Assert.DoesNotContain(graph.Routes, r => r.ToId == other.Id);
    }
}
