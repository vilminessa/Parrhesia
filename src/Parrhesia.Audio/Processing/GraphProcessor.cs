using System.Collections.Concurrent;
using Parrhesia.Core.Graph;

namespace Parrhesia.Audio.Processing;

/// <summary>
/// Микширующее ядро: обход графа за один аудиоблок.
/// Источники читают свои входы, шины суммируют входящие маршруты,
/// назначение отдаётся на вывод. Гейн узла применяется к его сигналу,
/// метр снимается после гейна, но до мьюта (видно сигнал и при mute).
/// Структурные изменения публикуются неизменяемым снимком — рендер-поток
/// не блокируется и не видит мутирующихся коллекций.
/// </summary>
public sealed class GraphProcessor : IDisposable
{
    private readonly AudioGraph _graph;
    private readonly int _channels;
    private readonly Dictionary<Guid, ISampleInput> _inputs = [];
    private readonly Dictionary<Guid, float[]> _buffers = [];
    private readonly ConcurrentDictionary<Guid, float> _peaks = [];

    private GraphSnapshot _snapshot;

    public GraphProcessor(AudioGraph graph, int channels = 2)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentOutOfRangeException.ThrowIfLessThan(channels, 1);

        _graph = graph;
        _channels = channels;
        _snapshot = GraphSnapshot.Build(graph);
        _graph.Changed += OnGraphChanged;
    }

    /// <summary>Привязать сэмпловый вход к узлу-источнику (null — отвязать). До Start/Stop.</summary>
    public void SetInput(Guid nodeId, ISampleInput? input)
    {
        if (input is null)
        {
            _inputs.Remove(nodeId);
        }
        else
        {
            _inputs[nodeId] = input;
        }
    }

    public void ClearInputs() => _inputs.Clear();

    /// <summary>Пик (linear, |x|) сигнала узла после гейна за последний блок.</summary>
    public float GetPeak(Guid nodeId) =>
        _peaks.TryGetValue(nodeId, out var peak) ? peak : 0f;

    /// <summary>Пересобрать снимок вручную (обычно делает подписка на Changed).</summary>
    public void Invalidate() => Volatile.Write(ref _snapshot, GraphSnapshot.Build(_graph));

    /// <summary>
    /// Смикшировать один блок для назначения <paramref name="sinkId"/>
    /// в <paramref name="output"/> (frames × channels сэмплов).
    /// </summary>
    public void ProcessBlock(Guid sinkId, Span<float> output, int frames)
    {
        var samples = frames * _channels;
        if (samples > output.Length)
        {
            samples = (output.Length / _channels) * _channels;
            frames = samples / _channels;
        }

        var snapshot = Volatile.Read(ref _snapshot);
        EnsureBuffers(snapshot, samples);

        foreach (var node in snapshot.Nodes)
        {
            if (node.Kind == NodeKind.Source)
            {
                ProcessSource(node, snapshot, samples);
            }
            else
            {
                ProcessSumming(node, snapshot, samples);
            }
        }

        if (samples == 0)
        {
            return;
        }

        if (_buffers.TryGetValue(sinkId, out var sinkBuffer))
        {
            sinkBuffer.AsSpan(0, samples).CopyTo(output[..samples]);
        }
        else
        {
            output[..samples].Clear();
        }

        if (output.Length > samples)
        {
            output[samples..].Clear();
        }
    }

    public void Dispose() => _graph.Changed -= OnGraphChanged;

    private void OnGraphChanged(object? sender, GraphChange e) => Invalidate();

    private void ProcessSource(NodeInfo node, GraphSnapshot snapshot, int samples)
    {
        var buffer = _buffers[node.Id];
        var span = buffer.AsSpan(0, samples);

        if (_inputs.TryGetValue(node.Id, out var input))
        {
            input.Read(span);
        }
        else
        {
            span.Clear();
        }

        ApplyNodeStage(node, span);
    }

    private void ProcessSumming(NodeInfo node, GraphSnapshot snapshot, int samples)
    {
        var buffer = _buffers[node.Id];
        var span = buffer.AsSpan(0, samples);
        span.Clear();

        foreach (var edge in snapshot.Edges)
        {
            if (edge.To != node.Id || !edge.Enabled)
            {
                continue;
            }

            if (!_buffers.TryGetValue(edge.From, out var from))
            {
                continue;
            }

            Accumulate(span, from.AsSpan(0, samples), edge.Gain);
        }

        ApplyNodeStage(node, span);
    }

    private static void Accumulate(Span<float> target, ReadOnlySpan<float> source, float gain)
    {
        if (gain == 1f)
        {
            for (var i = 0; i < target.Length; i++)
            {
                target[i] += source[i];
            }
        }
        else
        {
            for (var i = 0; i < target.Length; i++)
            {
                target[i] += source[i] * gain;
            }
        }
    }

    /// <summary>Гейн узла → метр (пик) → обнуление, если узел не звучит (mute/solo).</summary>
    private void ApplyNodeStage(NodeInfo node, Span<float> span)
    {
        if (node.Gain != 1f)
        {
            for (var i = 0; i < span.Length; i++)
            {
                span[i] *= node.Gain;
            }
        }

        var peak = 0f;
        for (var i = 0; i < span.Length; i++)
        {
            var absolute = MathF.Abs(span[i]);
            if (absolute > peak)
            {
                peak = absolute;
            }
        }

        _peaks[node.Id] = peak;

        if (node.Muted)
        {
            span.Clear();
        }
    }

    private void EnsureBuffers(GraphSnapshot snapshot, int samples)
    {
        foreach (var node in snapshot.Nodes)
        {
            if (_buffers.TryGetValue(node.Id, out var existing) && existing.Length >= samples)
            {
                continue;
            }

            var size = 1;
            while (size < samples)
            {
                size <<= 1;
            }

            _buffers[node.Id] = new float[size];
        }
    }
}
