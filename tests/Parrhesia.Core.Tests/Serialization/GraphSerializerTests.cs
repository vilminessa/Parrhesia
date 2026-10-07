using Parrhesia.Core.Graph;
using Parrhesia.Core.Serialization;

namespace Parrhesia.Core.Tests.Serialization;

public class GraphSerializerTests
{
    [Fact]
    public void RoundTrip_PreservesNodesRoutesAndLayout()
    {
        var graph = BuildSampleGraph();

        var json = GraphSerializer.Serialize(graph);
        var restored = GraphSerializer.Deserialize(json);

        Assert.Equal(3, restored.Nodes.Count);
        Assert.Equal(2, restored.Routes.Count);

        var mic = Assert.Single(restored.Nodes, n => n.Name == "Микрофон");
        Assert.Equal(NodeKind.Source, mic.Kind);
        Assert.Equal(DeviceSpec, mic.DeviceId);
        Assert.Equal(0.7f, mic.Gain, 3);
        Assert.True(mic.Mute);
        Assert.Equal(60.0, mic.X);
        Assert.Equal(156.0, mic.Y);

        var sink = Assert.Single(restored.Nodes, n => n.Kind == NodeKind.Sink);
        Assert.Equal("default:render", sink.DeviceId);
        Assert.True(sink.Solo);

        var stereo = Assert.Single(restored.Routes, r => r.Enabled);
        Assert.Equal(ChannelMap.Diagonal(2, 2), stereo.Map);
        Assert.Equal(0.5f, stereo.Gain, 3);

        var cross = Assert.Single(restored.Routes, r => !r.Enabled);
        Assert.Equal(ChannelMap.Pair(0, 1), cross.Map);
        Assert.False(cross.Enabled);
    }

    [Fact]
    public void Serialize_ProducesVersionedReadableJson()
    {
        var json = GraphSerializer.Serialize(BuildSampleGraph());

        Assert.Contains("\"version\": 3", json);
        Assert.Contains("\"Source\"", json);
        Assert.Contains("\"default:capture\"", json);
        Assert.Contains("\"channelCount\": 2", json);
        Assert.Contains("Микрофон", json); // кириллица пишется как есть
    }

    [Fact]
    public void LegacyV1Json_RouteMapIsMigratedToNewLayout()
    {
        // v1: map — биты раскладки 2×2 (бит = from*2 + to);9 = L→L + R→R.
        const string json = """
        {
          "version": 1,
          "nodes": [
            { "id": "11111111-1111-1111-1111-111111111111", "name": "Вход", "kind": "Source" },
            { "id": "22222222-2222-2222-2222-222222222222", "name": "Выход", "kind": "Sink" }
          ],
          "routes": [
            { "from": "11111111-1111-1111-1111-111111111111", "to": "22222222-2222-2222-2222-222222222222", "map": 9 }
          ]
        }
        """;

        var graph = GraphSerializer.Deserialize(json);

        var route = Assert.Single(graph.Routes);
        Assert.Equal(ChannelMap.Diagonal(2, 2), route.Map);
        Assert.Equal(2, graph.Nodes[0].ChannelCount);
        Assert.False(graph.Nodes[0].Bypassed);
    }

    [Fact]
    public void UnknownFields_AreIgnored()
    {
        const string json = """
        {
          "version": 2,
          "futureField": { "x": 1 },
          "nodes": [
            { "id": "11111111-1111-1111-1111-111111111111", "name": "Вход", "kind": "Source", "extra": 42 }
          ],
          "routes": []
        }
        """;

        var graph = GraphSerializer.Deserialize(json);

        Assert.Single(graph.Nodes);
        Assert.Equal("Вход", graph.Nodes[0].Name);
    }

    [Theory]
    [InlineData("{ \"version\": 99, \"nodes\": [] }", "версия")]
    [InlineData("{ \"version\": 1, \"nodes\": [], \"routes\": [ { \"from\": \"11111111-1111-1111-1111-111111111111\", \"to\": \"22222222-2222-2222-2222-222222222222\", \"map\": 9 } ] }", "нет узлов")]
    [InlineData("не json вовсе", "JSON")]
    public void InvalidDocuments_FailWithReason(string json, string expectedFragment)
    {
        Assert.False(GraphSerializer.TryDeserialize(json, out _, out var error));
        Assert.Contains(expectedFragment, error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UnknownEndpoint_Fails()
    {
        const string json = """
        {
          "version": 2,
          "nodes": [ { "id": "11111111-1111-1111-1111-111111111111", "name": "Вход", "kind": "Source" } ],
          "routes": [ { "from": "11111111-1111-1111-1111-111111111111", "to": "22222222-2222-2222-2222-222222222222", "map": 513 } ]
        }
        """;

        Assert.False(GraphSerializer.TryDeserialize(json, out _, out var error));
        Assert.Contains("неизвестный узел", error);
    }

    [Fact]
    public void EmptyChannelMap_Fails()
    {
        const string json = """
        {
          "version": 2,
          "nodes": [
            { "id": "11111111-1111-1111-1111-111111111111", "name": "Вход", "kind": "Source" },
            { "id": "22222222-2222-2222-2222-222222222222", "name": "Выход", "kind": "Sink" }
          ],
          "routes": [ { "from": "11111111-1111-1111-1111-111111111111", "to": "22222222-2222-2222-2222-222222222222", "map": 0 } ]
        }
        """;

        Assert.False(GraphSerializer.TryDeserialize(json, out _, out var error));
        Assert.Contains("карта каналов", error);
    }

    [Fact]
    public void DuplicateNodeIds_Fail()
    {
        const string json = """
        {
          "version": 2,
          "nodes": [
            { "id": "11111111-1111-1111-1111-111111111111", "name": "A", "kind": "Source" },
            { "id": "11111111-1111-1111-1111-111111111111", "name": "B", "kind": "Bus" }
          ]
        }
        """;

        Assert.False(GraphSerializer.TryDeserialize(json, out _, out var error));
        Assert.Contains("Дублирующийся", error);
    }

    [Fact]
    public void CyclicRoutes_Fail()
    {
        const string json = """
        {
          "version": 2,
          "nodes": [
            { "id": "11111111-1111-1111-1111-111111111111", "name": "A", "kind": "Bus" },
            { "id": "22222222-2222-2222-2222-222222222222", "name": "B", "kind": "Bus" },
            { "id": "33333333-3333-3333-3333-333333333333", "name": "C", "kind": "Bus" }
          ],
          "routes": [
            { "from": "11111111-1111-1111-1111-111111111111", "to": "22222222-2222-2222-2222-222222222222", "map": 513 },
            { "from": "22222222-2222-2222-2222-222222222222", "to": "33333333-3333-3333-3333-333333333333", "map": 513 },
            { "from": "33333333-3333-3333-3333-333333333333", "to": "11111111-1111-1111-1111-111111111111", "map": 513 }
          ]
        }
        """;

        Assert.False(GraphSerializer.TryDeserialize(json, out _, out var error));
        Assert.Contains("Cycle", error);
    }

    [Fact]
    public void Deserialize_ThrowsGraphSerializerException_OnBadJson()
    {
        var exception = Assert.Throws<GraphSerializerException>(() => GraphSerializer.Deserialize("{"));
        Assert.Contains("JSON", exception.Message);
    }

    [Fact]
    public void RoundTrip_PreservesPluginSlots()
    {
        var graph = new AudioGraph();
        var bus = graph.AddNode("Шина", NodeKind.Bus);
        graph.AddSlot(bus.Id, new PluginSlot
        {
            Format = PluginFormat.Clap,
            Path = @"C:\plugins\test.clap",
            PluginId = "com.parrhesia.test",
            Name = "Тест",
            State = [1, 2, 3, 255],
        });
        graph.AddSlot(bus.Id, new PluginSlot
        {
            Format = PluginFormat.Vst3,
            Path = @"C:\plugins\reverb.vst3",
            PluginId = "ABCD1234",
            Name = "Реверб",
            Enabled = false,
        });

        var restored = GraphSerializer.Deserialize(GraphSerializer.Serialize(graph));

        var restoredBus = Assert.Single(restored.Nodes, n => n.Name == "Шина");
        Assert.Equal(2, restoredBus.Slots.Count);

        var first = restoredBus.Slots[0];
        Assert.Equal(PluginFormat.Clap, first.Format);
        Assert.Equal(@"C:\plugins\test.clap", first.Path);
        Assert.Equal("com.parrhesia.test", first.PluginId);
        Assert.Equal("Тест", first.Name);
        Assert.True(first.Enabled);
        Assert.Equal(new byte[] { 1, 2, 3, 255 }, first.State);

        var second = restoredBus.Slots[1];
        Assert.Equal(PluginFormat.Vst3, second.Format);
        Assert.False(second.Enabled);
        Assert.Null(second.State);
    }

    [Fact]
    public void V2Json_WithoutSlots_LoadsWithEmptyChains()
    {
        const string json = """
        {
          "version": 2,
          "nodes": [ { "id": "11111111-1111-1111-1111-111111111111", "name": "Шина", "kind": "Bus" } ],
          "routes": []
        }
        """;

        var graph = GraphSerializer.Deserialize(json);

        Assert.Empty(Assert.Single(graph.Nodes).Slots);
    }

    [Fact]
    public void Slots_OnNonBusNode_Fail()
    {
        const string json = """
        {
          "version": 3,
          "nodes": [
            {
              "id": "11111111-1111-1111-1111-111111111111",
              "name": "Вход",
              "kind": "Source",
              "slots": [ { "format": "Clap", "path": "x.clap", "pluginId": "a.b" } ]
            }
          ],
          "routes": []
        }
        """;

        Assert.False(GraphSerializer.TryDeserialize(json, out _, out var error));
        Assert.Contains("только у шин", error);
    }

    [Fact]
    public void SlotWithoutPath_Fails()
    {
        const string json = """
        {
          "version": 3,
          "nodes": [
            {
              "id": "11111111-1111-1111-1111-111111111111",
              "name": "Шина",
              "kind": "Bus",
              "slots": [ { "format": "Clap", "pluginId": "a.b" } ]
            }
          ],
          "routes": []
        }
        """;

        Assert.False(GraphSerializer.TryDeserialize(json, out _, out var error));
        Assert.Contains("пути", error);
    }

    [Fact]
    public void SlotWithBadStateBase64_Fails()
    {
        const string json = """
        {
          "version": 3,
          "nodes": [
            {
              "id": "11111111-1111-1111-1111-111111111111",
              "name": "Шина",
              "kind": "Bus",
              "slots": [ { "format": "Clap", "path": "x.clap", "pluginId": "a.b", "state": "не-base64!" } ]
            }
          ],
          "routes": []
        }
        """;

        Assert.False(GraphSerializer.TryDeserialize(json, out _, out var error));
        Assert.Contains("base64", error);
    }

    private static AudioGraph BuildSampleGraph()
    {
        var graph = new AudioGraph();
        var mic = graph.AddNode("Микрофон", NodeKind.Source);
        var bus = graph.AddNode("Шина", NodeKind.Bus);
        var sink = graph.AddNode("Наушники", NodeKind.Sink);

        graph.SetNodeDevice(mic.Id, DeviceSpec);
        graph.SetNodeDevice(sink.Id, "default:render");
        graph.SetNodeGain(mic.Id, 0.7f);
        graph.SetNodeMute(mic.Id, true);
        graph.SetNodeSolo(sink.Id, true);
        graph.SetNodePosition(mic.Id, 60, 156);
        graph.SetNodePosition(sink.Id, 660, 156);

        graph.AddRoute(mic.Id, bus.Id, out _);
        graph.SetRouteGain(mic.Id, bus.Id, 0.5f);

        graph.AddRoute(bus.Id, 0, sink.Id, 1, out _);
        graph.SetRouteEnabled(bus.Id, sink.Id, false);

        return graph;
    }

    private const string DeviceSpec = "default:capture";
}
