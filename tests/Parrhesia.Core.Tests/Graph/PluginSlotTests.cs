using Parrhesia.Core.Graph;

namespace Parrhesia.Core.Tests.Graph;

public class PluginSlotTests
{
    [Fact]
    public void AddSlot_OnlyAllowedOnBus()
    {
        var graph = new AudioGraph();
        var source = graph.AddNode("Вход", NodeKind.Source);

        Assert.Throws<ArgumentException>(() =>
            graph.AddSlot(source.Id, new PluginSlot { Path = "x.clap", PluginId = "a.b" }));
    }

    [Fact]
    public void AddSlot_RequiresPathAndId()
    {
        var graph = new AudioGraph();
        var bus = graph.AddNode("Шина", NodeKind.Bus);

        Assert.Throws<ArgumentException>(() => graph.AddSlot(bus.Id, new PluginSlot()));
        Assert.Throws<ArgumentException>(() =>
            graph.AddSlot(bus.Id, new PluginSlot { Path = "x.clap" }));
        Assert.Throws<ArgumentException>(() =>
            graph.AddSlot(bus.Id, new PluginSlot { PluginId = "a.b" }));
        Assert.Empty(bus.Slots);
    }

    [Fact]
    public void AddSlot_RaisesNodeChanged()
    {
        var graph = new AudioGraph();
        var bus = graph.AddNode("Шина", NodeKind.Bus);
        GraphChange? change = null;
        graph.Changed += (_, e) => change = e;

        graph.AddSlot(bus.Id, new PluginSlot { Path = "x.clap", PluginId = "a.b" });

        Assert.NotNull(change);
        Assert.Equal(GraphChangeKind.NodeChanged, change.Kind);
        Assert.Equal(bus.Id, change.Node?.Id);
    }

    [Fact]
    public void RemoveSlot_ByIndex()
    {
        var graph = new AudioGraph();
        var bus = graph.AddNode("Шина", NodeKind.Bus);
        graph.AddSlot(bus.Id, new PluginSlot { Path = "a.clap", PluginId = "a" });
        graph.AddSlot(bus.Id, new PluginSlot { Path = "b.clap", PluginId = "b" });

        Assert.False(graph.RemoveSlot(bus.Id, 5));
        Assert.True(graph.RemoveSlot(bus.Id, 0));

        var slot = Assert.Single(bus.Slots);
        Assert.Equal("b.clap", slot.Path);
    }

    [Fact]
    public void MoveSlot_Reorders()
    {
        var graph = new AudioGraph();
        var bus = graph.AddNode("Шина", NodeKind.Bus);
        graph.AddSlot(bus.Id, new PluginSlot { Path = "a.clap", PluginId = "a" });
        graph.AddSlot(bus.Id, new PluginSlot { Path = "b.clap", PluginId = "b" });
        graph.AddSlot(bus.Id, new PluginSlot { Path = "c.clap", PluginId = "c" });

        graph.MoveSlot(bus.Id, 2, 0);

        Assert.Equal(["c.clap", "a.clap", "b.clap"], bus.Slots.Select(s => s.Path).ToArray());
        Assert.Throws<ArgumentOutOfRangeException>(() => graph.MoveSlot(bus.Id, 0, 9));
    }

    [Fact]
    public void SetSlotEnabled_ReplacesSlotWithoutTouchingOthers()
    {
        var graph = new AudioGraph();
        var bus = graph.AddNode("Шина", NodeKind.Bus);
        graph.AddSlot(bus.Id, new PluginSlot { Path = "a.clap", PluginId = "a", State = [7] });
        graph.AddSlot(bus.Id, new PluginSlot { Path = "b.clap", PluginId = "b" });

        graph.SetSlotEnabled(bus.Id, 0, false);

        Assert.False(bus.Slots[0].Enabled);
        Assert.Equal(new byte[] { 7 }, bus.Slots[0].State);
        Assert.True(bus.Slots[1].Enabled);
        Assert.True(bus.Slots[1] != bus.Slots[0]);
    }

    [Fact]
    public void SetSlotState_StoresChunk()
    {
        var graph = new AudioGraph();
        var bus = graph.AddNode("Шина", NodeKind.Bus);
        graph.AddSlot(bus.Id, new PluginSlot { Path = "a.clap", PluginId = "a" });

        graph.SetSlotState(bus.Id, 0, [10, 20]);
        Assert.Equal(new byte[] { 10, 20 }, bus.Slots[0].State);

        graph.SetSlotState(bus.Id, 0, null);
        Assert.Null(bus.Slots[0].State);
    }

    [Fact]
    public void ReplaceWith_CopiesSlots()
    {
        var source = new AudioGraph();
        var bus = source.AddNode("Шина", NodeKind.Bus);
        source.AddSlot(bus.Id, new PluginSlot { Path = "a.clap", PluginId = "a", State = [5] });

        var target = new AudioGraph();
        target.ReplaceWith(source);

        var copied = Assert.Single(Assert.Single(target.Nodes).Slots);
        Assert.Equal("a.clap", copied.Path);
        Assert.Equal(new byte[] { 5 }, copied.State);

        // Копия независима: удаление в источнике не трогает цель.
        source.RemoveSlot(bus.Id, 0);
        Assert.Single(target.Nodes[0].Slots);
    }
}
