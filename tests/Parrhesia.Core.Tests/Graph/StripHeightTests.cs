using Parrhesia.Core.Graph;
using Parrhesia.Core.Serialization;

namespace Parrhesia.Core.Tests.Graph;

/// <summary>
/// Высота пульта в модели узла (M-волна «модульность»): персистится в профиле,
/// меняется косметическим событием AppearanceChanged (движок его игнорирует).
/// </summary>
public class StripHeightTests
{
    [Fact]
    public void SetNodeStripHeight_RaisesAppearanceChanged_AndKeepsValue()
    {
        var graph = new AudioGraph();
        var node = graph.AddNode("Микрофон", NodeKind.Source);
        Assert.Null(node.StripHeight);

        List<GraphChangeKind> changes = [];
        graph.Changed += (_, e) => changes.Add(e.Kind);

        graph.SetNodeStripHeight(node.Id, 240);

        Assert.Equal(240, node.StripHeight);
        Assert.Contains(GraphChangeKind.AppearanceChanged, changes);

        // То же значение — без события (нет лишних автосейвов).
        changes.Clear();
        graph.SetNodeStripHeight(node.Id, 240);
        Assert.DoesNotContain(GraphChangeKind.AppearanceChanged, changes);
    }

    [Fact]
    public void SetNodeStripHeight_UnknownNode_Throws()
    {
        var graph = new AudioGraph();
        Assert.Throws<ArgumentException>(() => graph.SetNodeStripHeight(Guid.NewGuid(), 200));
    }

    [Fact]
    public void Serialize_WithHeight_RoundTrip()
    {
        var graph = new AudioGraph();
        var node = graph.AddNode("Шина", NodeKind.Bus);
        graph.SetNodeStripHeight(node.Id, 360);

        var json = GraphSerializer.Serialize(graph);
        var restored = GraphSerializer.Deserialize(json);

        Assert.Contains("\"stripHeight\": 360", json);
        Assert.Equal(360, Assert.Single(restored.Nodes).StripHeight);
    }

    [Fact]
    public void Serialize_WithoutHeight_FieldAbsentAndValueNull()
    {
        var graph = new AudioGraph();
        graph.AddNode("Вывод", NodeKind.Sink);

        var json = GraphSerializer.Serialize(graph);

        Assert.DoesNotContain("stripHeight", json);

        var restored = GraphSerializer.Deserialize(json);
        Assert.Null(Assert.Single(restored.Nodes).StripHeight);
    }

    [Fact]
    public void LegacyJson_WithoutField_LoadsAsDefault()
    {
        // Старый профиль (до H-волны) — поля нет → null → высота по умолчанию.
        const string json = """
        {
          "version": 3,
          "nodes": [
            { "id": "11111111-1111-1111-1111-111111111111", "name": "Вход", "kind": "Source" }
          ]
        }
        """;

        var restored = GraphSerializer.Deserialize(json);

        Assert.Null(Assert.Single(restored.Nodes).StripHeight);
    }

    [Fact]
    public void ReplaceWith_CopiesHeight()
    {
        var source = new AudioGraph();
        var node = source.AddNode("Микрофон", NodeKind.Source);
        source.SetNodeStripHeight(node.Id, 180);

        var target = new AudioGraph();
        target.ReplaceWith(source);

        Assert.Equal(180, Assert.Single(target.Nodes).StripHeight);
    }
}
