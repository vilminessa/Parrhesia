using Parrhesia.Core.Graph;
using Parrhesia.Core.Serialization;

namespace Parrhesia.Core.Tests.Graph;

/// <summary>
/// S-волна: узел-плагин (NodeKind.Plugin) — обработка VST3/CLAP в отдельном
/// процессе; слот-вставка единственная; маршруты в/из как у шины.
/// </summary>
public class PluginNodeTests
{
    [Fact]
    public void PluginNode_HasBothPorts()
    {
        var graph = new AudioGraph();
        var node = graph.AddNode("Эквалайзер", NodeKind.Plugin);

        Assert.True(node.HasInput);
        Assert.True(node.HasOutput);
    }

    [Fact]
    public void AddSlot_OnPluginNode_Works()
    {
        var graph = new AudioGraph();
        var node = graph.AddNode("Эквалайзер", NodeKind.Plugin);

        graph.AddSlot(node.Id, new PluginSlot { Path = "x.vst3", PluginId = "abc", Name = "X" });

        Assert.Single(node.Slots);
    }

    [Fact]
    public void AddSlot_OnSink_Throws()
    {
        var graph = new AudioGraph();
        var sink = graph.AddNode("Выход", NodeKind.Sink);

        Assert.Throws<ArgumentException>(() =>
            graph.AddSlot(sink.Id, new PluginSlot { Path = "x.vst3", PluginId = "abc", Name = "X" }));
    }

    [Fact]
    public void AddSlot_OnBus_Throws_S5()
    {
        // S5: слоты живут только на узлах-плагинах — шина их не принимает
        // (старые профили с шиной+слотом мигрируются при загрузке).
        var graph = new AudioGraph();
        var bus = graph.AddNode("Шина", NodeKind.Bus);

        Assert.Throws<ArgumentException>(() =>
            graph.AddSlot(bus.Id, new PluginSlot { Path = "x.vst3", PluginId = "abc", Name = "X" }));
    }

    [Fact]
    public void Routes_InAndOut_AreValid()
    {
        var graph = new AudioGraph();
        var source = graph.AddNode("Микрофон", NodeKind.Source);
        var plugin = graph.AddNode("Компрессор", NodeKind.Plugin);
        var sink = graph.AddNode("Динамики", NodeKind.Sink);

        Assert.Equal(RouteError.None, graph.AddRoute(source.Id, plugin.Id, out _));
        Assert.Equal(RouteError.None, graph.AddRoute(plugin.Id, sink.Id, out _));
    }

    [Fact]
    public void Serialize_PluginKind_RoundTrip()
    {
        var graph = new AudioGraph();
        graph.AddNode("Эквалайзер", NodeKind.Plugin);

        var json = GraphSerializer.Serialize(graph);

        Assert.Contains("\"Plugin\"", json);
        Assert.Equal(NodeKind.Plugin, Assert.Single(GraphSerializer.Deserialize(json).Nodes).Kind);
    }

    [Fact]
    public void Deserialize_BusWithSlots_MigratesToPlugin()
    {
        // Старый профиль: шина со слотом → узел-плагин (слот — единственная обработка).
        const string json = """
        {
          "version": 3,
          "nodes": [
            {
              "id": "11111111-1111-1111-1111-111111111111",
              "name": "Шина с плагином",
              "kind": "Bus",
              "slots": [
                { "format": "Vst3", "path": "x.vst3", "pluginId": "abc", "name": "X", "enabled": true }
              ]
            }
          ]
        }
        """;

        var node = Assert.Single(GraphSerializer.Deserialize(json).Nodes);

        Assert.Equal(NodeKind.Plugin, node.Kind);
        Assert.Equal("Шина с плагином", node.Name);
        Assert.Single(node.Slots);
    }

    [Fact]
    public void Deserialize_BusWithoutSlots_StaysBus()
    {
        const string json = """
        {
          "version": 3,
          "nodes": [
            { "id": "11111111-1111-1111-1111-111111111111", "name": "Шина", "kind": "Bus" }
          ]
        }
        """;

        Assert.Equal(NodeKind.Bus, Assert.Single(GraphSerializer.Deserialize(json).Nodes).Kind);
    }
}
