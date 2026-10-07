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

    public static GraphSnapshot Build(AudioGraph graph, Func<Guid, int>? chainLatency = null)
    {
        var nodes = graph.Nodes
            .Select(n => new NodeInfo(
                n.Id,
                n.Kind,
                n.Gain,
                graph.IsEffectivelyMuted(n),
                n.Bypassed,
                n.ChannelCount,
                ChainLatency: Math.Max(0, chainLatency?.Invoke(n.Id) ?? 0)))
            .ToArray();
        var edges = graph.Routes
            .Select(BuildEdge)
            .Where(e => e.Assignments.Length > 0)
            .ToArray();

        var ordered = TopologicalOrder(nodes, edges);
        return new GraphSnapshot(ordered, ComputeLatencies(ordered, edges));
    }

    /// <summary>
    /// Топологический проход латентностей: In(узел) = max Out(входящих источников)
    /// по активным рёбрам, Out = In + ChainLatency. Ребро получает компенсирующую
    /// задержку In(to) − Out(from) — параллельные ветки выравниваются по самой
    /// «медленной» (в духе DAW-компенсации вставок).
    /// </summary>
    private static EdgeInfo[] ComputeLatencies(NodeInfo[] ordered, EdgeInfo[] edges)
    {
        var outLatency = new Dictionary<Guid, int>(ordered.Length);
        var inLatency = new Dictionary<Guid, int>(ordered.Length);

        foreach (var node in ordered)
        {
            var incomingMax = 0;
            foreach (var edge in edges)
            {
                if (edge.To != node.Id || !edge.Enabled)
                {
                    continue;
                }

                var sourceOut = outLatency.GetValueOrDefault(edge.From);
                if (sourceOut > incomingMax)
                {
                    incomingMax = sourceOut;
                }
            }

            inLatency[node.Id] = incomingMax;
            outLatency[node.Id] = incomingMax + node.ChainLatency;
        }

        return edges.Select(e => e with
        {
            CompensationDelay = Math.Max(0, inLatency.GetValueOrDefault(e.To) - outLatency.GetValueOrDefault(e.From)),
        }).ToArray();
    }

    /// <summary>
    /// Разворачивает карту каналов в пары с масштабом: если маршрут кормит
    /// один канал назначения из нескольких своих (стерео→моно), каждый
    /// получает 1/n — как mix level в Cantabile.
    /// </summary>
    private static EdgeInfo BuildEdge(Route route)
    {
        var pairs = route.Map.Pairs().ToArray();
        var feedCounts = new int[ChannelMap.MaxChannels];
        foreach (var (_, to) in pairs)
        {
            feedCounts[to]++;
        }

        var assignments = new EdgeAssignment[pairs.Length];
        for (var i = 0; i < pairs.Length; i++)
        {
            var (from, to) = pairs[i];
            var scale = feedCounts[to] > 1 ? 1f / feedCounts[to] : 1f;
            assignments[i] = new EdgeAssignment(from, to, scale);
        }

        return new EdgeInfo(route.FromId, route.ToId, route.Gain, route.Enabled, route.Map, assignments);
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

internal sealed record NodeInfo(
    Guid Id,
    NodeKind Kind,
    float Gain,
    bool Muted,
    bool Bypassed,
    int ChannelCount,
    int ChainLatency = 0,
    int InLatency = 0);

/// <summary>Пара каналов маршрута с масштабом (1/n для N→1-суммирования).</summary>
internal sealed record EdgeAssignment(int From, int To, float Scale);

internal sealed record EdgeInfo(
    Guid From,
    Guid To,
    float Gain,
    bool Enabled,
    ChannelMap Map,
    EdgeAssignment[] Assignments,
    int CompensationDelay = 0);
