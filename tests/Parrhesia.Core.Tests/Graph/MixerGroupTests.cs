using Parrhesia.Core.Graph;
using Parrhesia.Core.Serialization;

namespace Parrhesia.Core.Tests.Graph;

public class MixerGroupTests
{
    [Fact]
    public void AddGroup_InitializesGroupsAndRaisesEvent()
    {
        var graph = new AudioGraph();
        Assert.False(graph.GroupsInitialized);

        List<GraphChangeKind> changes = [];
        graph.Changed += (_, e) => changes.Add(e.Kind);

        var group = graph.AddGroup("Отдельные программы", autoFill: true);

        Assert.True(graph.GroupsInitialized);
        var added = Assert.Single(graph.Groups);
        Assert.Equal(group.Id, added.Id);
        Assert.Equal("Отдельные программы", added.Name);
        Assert.True(added.AutoFill);
        Assert.False(string.IsNullOrEmpty(added.Id));
        Assert.Contains(GraphChangeKind.GroupsChanged, changes);
    }

    [Fact]
    public void SetNodeGroup_MovesBetweenGroups_OneMembershipMax()
    {
        var graph = new AudioGraph();
        var node = graph.AddNode("Браузер", NodeKind.Source);
        var first = graph.AddGroup("Первая");
        var second = graph.AddGroup("Вторая");

        graph.SetNodeGroup(node.Id, first.Id);
        graph.SetNodeGroup(node.Id, second.Id);

        // Move-семантика: узел виден ровно в одной группе (иначе стрип дублируется).
        Assert.Empty(Assert.Single(graph.Groups, g => g.Id == first.Id).NodeIds);
        Assert.Single(graph.Groups, g => g.NodeIds.Contains(node.Id));
        Assert.Equal(second.Id, graph.FindGroupOf(node.Id)!.Id);
    }

    [Fact]
    public void SetNodeGroup_Null_RemovesFromEverywhere()
    {
        var graph = new AudioGraph();
        var node = graph.AddNode("Discord", NodeKind.Source);
        var group = graph.AddGroup("Программы");

        graph.SetNodeGroup(node.Id, group.Id);
        graph.SetNodeGroup(node.Id, null);

        Assert.Null(graph.FindGroupOf(node.Id));
        Assert.Empty(Assert.Single(graph.Groups).NodeIds);
    }

    [Fact]
    public void SetNodeGroup_UnknownGroup_Throws()
    {
        var graph = new AudioGraph();
        var node = graph.AddNode("X", NodeKind.Source);

        Assert.Throws<ArgumentException>(() => graph.SetNodeGroup(node.Id, "nope"));
    }

    [Fact]
    public void RemoveNode_PrunesMembershipAndRaisesGroupsChanged()
    {
        var graph = new AudioGraph();
        var node = graph.AddNode("Микрофон", NodeKind.Source);
        var group = graph.AddGroup("Ввод");
        graph.SetNodeGroup(node.Id, group.Id);

        List<GraphChangeKind> changes = [];
        graph.Changed += (_, e) => changes.Add(e.Kind);

        Assert.True(graph.RemoveNode(node.Id));

        Assert.Empty(Assert.Single(graph.Groups).NodeIds);
        Assert.Contains(GraphChangeKind.GroupsChanged, changes);
        Assert.Contains(GraphChangeKind.NodeRemoved, changes);
    }

    [Fact]
    public void RenameAndRemoveGroup_RaiseEvent()
    {
        var graph = new AudioGraph();
        var group = graph.AddGroup("Старое имя");

        Assert.True(graph.RenameGroup(group.Id, "Новое имя"));
        Assert.Equal("Новое имя", Assert.Single(graph.Groups).Name);

        Assert.True(graph.RemoveGroup(group.Id));
        Assert.Empty(graph.Groups);
        Assert.False(graph.RemoveGroup(group.Id)); // уже удалена
    }

    [Fact]
    public void ReplaceWith_KeepsGroupsButDropsDeadNodeIds()
    {
        var graph = new AudioGraph();
        var doomed = graph.AddNode("Старый узел", NodeKind.Source);
        var survivor = graph.AddNode("Живой узел", NodeKind.Source);
        var group = graph.AddGroup("Ручная");
        graph.SetNodeGroup(survivor.Id, group.Id);
        graph.SetNodeGroup(doomed.Id, group.Id);

        var preset = new AudioGraph();
        preset.AddNode("Живой узел", NodeKind.Source, survivor.Id);

        graph.ReplaceWith(preset);

        // Группа пережила замену, мёртвые ссылки вычищены.
        var restored = Assert.Single(graph.Groups);
        Assert.Equal("Ручная", restored.Name);
        Assert.Equal(new[] { survivor.Id }, restored.NodeIds);
    }

    [Fact]
    public void Serialize_WithoutGroups_KeepsThemUninitialized()
    {
        var graph = new AudioGraph();
        graph.AddNode("Узел", NodeKind.Source);

        Assert.False(graph.GroupsInitialized);

        var json = GraphSerializer.Serialize(graph);
        var restored = GraphSerializer.Deserialize(json);

        Assert.DoesNotContain("\"groups\"", json);
        Assert.False(restored.GroupsInitialized);
        Assert.Empty(restored.Groups);
    }

    [Fact]
    public void Serialize_Groups_RoundTrip()
    {
        var graph = new AudioGraph();
        var browser = graph.AddNode("Браузер", NodeKind.Source);
        var mic = graph.AddNode("Микрофон", NodeKind.Source);
        var programs = graph.AddGroup("Отдельные программы", autoFill: true);
        graph.SetNodeGroup(browser.Id, programs.Id);

        var json = GraphSerializer.Serialize(graph);
        var restored = GraphSerializer.Deserialize(json);

        Assert.True(restored.GroupsInitialized);
        var group = Assert.Single(restored.Groups);
        Assert.Equal(programs.Id, group.Id);
        Assert.Equal("Отдельные программы", group.Name);
        Assert.True(group.AutoFill);

        var restoredBrowser = Assert.Single(restored.Nodes, n => n.Name == "Браузер");
        var restoredMic = Assert.Single(restored.Nodes, n => n.Name == "Микрофон");
        Assert.Equal(new[] { restoredBrowser.Id }, group.NodeIds);
        Assert.DoesNotContain(restoredMic.Id, group.NodeIds);
    }

    [Fact]
    public void Deserialize_GroupWithUnknownNodeIds_DropsDeadLinks()
    {
        const string json = """
        {
          "version": 3,
          "nodes": [
            { "id": "11111111-1111-1111-1111-111111111111", "name": "Вход", "kind": "Source" }
          ],
          "groups": [
            {
              "id": "abc",
              "name": "Прочее",
              "autoFill": false,
              "nodeIds": [
                "11111111-1111-1111-1111-111111111111",
                "99999999-9999-9999-9999-999999999999"
              ]
            }
          ]
        }
        """;

        var restored = GraphSerializer.Deserialize(json);

        var group = Assert.Single(restored.Groups);
        var node = Assert.Single(restored.Nodes);
        Assert.Equal(new[] { node.Id }, group.NodeIds);
    }

    [Fact]
    public void Deserialize_EmptyGroupsList_IsInitializedButEmpty()
    {
        // Пользователь удалил обе группы — это не «никогда не заводили».
        const string json = """
        {
          "version": 3,
          "nodes": [
            { "id": "11111111-1111-1111-1111-111111111111", "name": "Вход", "kind": "Source" }
          ],
          "groups": []
        }
        """;

        var restored = GraphSerializer.Deserialize(json);

        Assert.True(restored.GroupsInitialized);
        Assert.Empty(restored.Groups);
    }
}
