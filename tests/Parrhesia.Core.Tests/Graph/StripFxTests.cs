using Parrhesia.Core.Graph;
using Parrhesia.Core.Serialization;

namespace Parrhesia.Core.Tests.Graph;

/// <summary>
/// Состояния эффекторов пульта (K-волна): вкл/выкл по стабильным id,
/// NodeChanged для UI/движка, персист в профиле.
/// </summary>
public class StripFxTests
{
    [Fact]
    public void SetNodeFx_Toggles_AndRaisesNodeChanged()
    {
        var graph = new AudioGraph();
        var node = graph.AddNode("Микрофон", NodeKind.Source);
        Assert.False(node.FxEnabled.ContainsKey("eq"));

        List<GraphChangeKind> changes = [];
        graph.Changed += (_, e) => changes.Add(e.Kind);

        graph.SetNodeFx(node.Id, "eq", true);
        Assert.True(node.FxEnabled["eq"]);
        Assert.Contains(GraphChangeKind.NodeChanged, changes);

        // То же значение — без события.
        changes.Clear();
        graph.SetNodeFx(node.Id, "eq", true);
        Assert.DoesNotContain(GraphChangeKind.NodeChanged, changes);

        graph.SetNodeFx(node.Id, "eq", false);
        Assert.False(node.FxEnabled["eq"]);
    }

    [Fact]
    public void SetNodeFx_UnknownNodeOrEmptyId_Throws()
    {
        var graph = new AudioGraph();
        var node = graph.AddNode("X", NodeKind.Source);

        Assert.Throws<ArgumentException>(() => graph.SetNodeFx(Guid.NewGuid(), "eq", true));
        Assert.Throws<ArgumentException>(() => graph.SetNodeFx(node.Id, " ", true));
    }

    [Fact]
    public void Serialize_FxState_RoundTrip_PreservesKeys()
    {
        var graph = new AudioGraph();
        var node = graph.AddNode("Шина", NodeKind.Bus);
        graph.SetNodeFx(node.Id, "eq", true);
        graph.SetNodeFx(node.Id, "comp", true);
        graph.SetNodeFx(node.Id, "gate", false); // выключенный тоже пишется — состояние явное

        var json = GraphSerializer.Serialize(graph);
        var restored = GraphSerializer.Deserialize(json);

        Assert.Contains("\"eq\": true", json);
        var fx = Assert.Single(restored.Nodes).FxEnabled;
        Assert.True(fx["eq"]);
        Assert.True(fx["comp"]);
        Assert.False(fx["gate"]);
    }

    [Fact]
    public void Serialize_WithoutFx_LegacyJson_LoadsEmpty()
    {
        const string json = """
        {
          "version": 3,
          "nodes": [
            { "id": "11111111-1111-1111-1111-111111111111", "name": "Вход", "kind": "Source" }
          ]
        }
        """;

        var restored = GraphSerializer.Deserialize(json);

        Assert.Empty(Assert.Single(restored.Nodes).FxEnabled);
    }

    [Fact]
    public void ReplaceWith_CopiesFxState()
    {
        var source = new AudioGraph();
        var node = source.AddNode("Микрофон", NodeKind.Source);
        source.SetNodeFx(node.Id, "denoise", true);

        var target = new AudioGraph();
        target.ReplaceWith(source);

        Assert.True(Assert.Single(target.Nodes).FxEnabled["denoise"]);
    }

    [Fact]
    public void SetNodeFxExpanded_TogglesPerStrip_AndRaisesAppearanceChanged()
    {
        var graph = new AudioGraph();
        var mic = graph.AddNode("Микрофон", NodeKind.Source);
        var bus = graph.AddNode("Шина", NodeKind.Bus);
        Assert.Null(mic.FxExpanded);

        List<GraphChange> changes = [];
        graph.Changed += (_, e) => changes.Add(e);

        graph.SetNodeFxExpanded(mic.Id, true);
        Assert.True(mic.FxExpanded);
        Assert.Null(bus.FxExpanded); // режим живёт в узле — у каждого пульта свой
        var change = Assert.Single(changes);
        Assert.Equal(GraphChangeKind.AppearanceChanged, change.Kind);
        Assert.Equal(mic.Id, change.Node!.Id);

        // Возврат в обычный вид хранится как null (JSON без поля).
        changes.Clear();
        graph.SetNodeFxExpanded(mic.Id, false);
        Assert.Null(mic.FxExpanded);
        Assert.Single(changes);

        // То же состояние — без события.
        changes.Clear();
        graph.SetNodeFxExpanded(mic.Id, false);
        Assert.Empty(changes);
    }

    [Fact]
    public void SetNodeFxExpanded_UnknownNode_Throws()
    {
        var graph = new AudioGraph();
        Assert.Throws<ArgumentException>(() => graph.SetNodeFxExpanded(Guid.NewGuid(), true));
    }

    [Fact]
    public void Serialize_FxExpanded_RoundTrip_AndLegacy()
    {
        var graph = new AudioGraph();
        var node = graph.AddNode("Микрофон", NodeKind.Source);
        graph.SetNodeFxExpanded(node.Id, true);

        var json = GraphSerializer.Serialize(graph);
        Assert.Contains("\"fxExpanded\": true", json);
        Assert.True(Assert.Single(GraphSerializer.Deserialize(json).Nodes).FxExpanded);

        // Обычный вид — поля нет (null → JSON-молчание).
        graph.SetNodeFxExpanded(node.Id, false);
        var plain = GraphSerializer.Serialize(graph);
        Assert.DoesNotContain("fxExpanded", plain);
        Assert.Null(Assert.Single(GraphSerializer.Deserialize(plain).Nodes).FxExpanded);
    }
}
