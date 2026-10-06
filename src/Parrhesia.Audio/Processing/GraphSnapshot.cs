using Parrhesia.Core.Graph;

namespace Parrhesia.Audio.Processing;

/// <summary>
/// Неизменяемый снимок графа для обработки в RT-потоке: копии значений
/// (гейны, мьют) и топологический порядок. Публикуется атомарно ссылкой —
/// рендер-поток всегда работает с целым согласованным снимком.
/// </summary>
internal sealed class GraphSnapshot
{
    public static readonly GraphSnapshot Empty = new([], []);

    private GraphSnapshot(NodeInfo[] nodes, EdgeInfo[] edges)
    {
        Nodes = nodes;
        Edges = edges;
    }

    /// <summary>Узлы в топологическом порядке: источники раньше потребителей.</summary>
    public NodeInfo[] Nodes { get; }

    public EdgeInfo[] Edges { get; }

    public static GraphSnapshot Build(AudioGraph graph)
    {
        var nodes = graph.Nodes
            .Select(n => new NodeInfo(n.Id, n.Kind, n.Gain, graph.IsEffectivelyMuted(n)))
            .ToArray();
        var edges = graph.Routes
            .Select(r => new EdgeInfo(r.FromId, r.ToId, r.Gain, r.Enabled, r.Map))
            .ToArray();

        return new GraphSnapshot(TopologicalOrder(nodes, edges), edges);
    }

    private static NodeInfo[] TopologicalOrder(NodeInfo[] nodes, EdgeInfo[] edges)
    {
        var inDegree = new Dictionary<Guid, int>(nodes.Length);
        var outgoing = new Dictionary<Guid, List<Guid>>(nodes.Length);
        foreach (var node in nodes)
        {
            inDegree[node.Id] = 0;
        }

        foreach (var edge in edges)
        {
            if (!inDegree.ContainsKey(edge.To) || !inDegree.ContainsKey(edge.From))
            {
                continue;
            }

            inDegree[edge.To]++;
            if (!outgoing.TryGetValue(edge.From, out var list))
            {
                list = [];
                outgoing[edge.From] = list;
            }

            list.Add(edge.To);
        }

        var order = new List<NodeInfo>(nodes.Length);
        var byId = nodes.ToDictionary(n => n.Id);
        var queue = new Queue<Guid>(nodes.Where(n => inDegree[n.Id] == 0).Select(n => n.Id));
        while (queue.Count > 0)
        {
            var id = queue.Dequeue();
            order.Add(byId[id]);
            if (!outgoing.TryGetValue(id, out var targets))
            {
                continue;
            }

            foreach (var target in targets)
            {
                if (--inDegree[target] == 0)
                {
                    queue.Enqueue(target);
                }
            }
        }

        // Граф ациклический по построению Core; страховка на случай рассинхрона.
        if (order.Count != nodes.Length)
        {
            return nodes;
        }

        return order.ToArray();
    }
}

internal sealed record NodeInfo(Guid Id, NodeKind Kind, float Gain, bool Muted);

internal sealed record EdgeInfo(Guid From, Guid To, float Gain, bool Enabled, ChannelMap Map);
