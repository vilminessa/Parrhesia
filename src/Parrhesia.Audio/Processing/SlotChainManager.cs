using Parrhesia.Core.Graph;
using Parrhesia.Plugins;
using Parrhesia.Plugins.Clap;

namespace Parrhesia.Audio.Processing;

/// <summary>
/// Синхронизация слотов-вставок модели с рантайм-экземплярами плагинов.
/// Реагирует на graph.Changed (вне RT): быстрая сверка сигнатур
/// «формат|путь|id» — загрузка/выгрузка только при реальных изменениях,
/// переключение Enabled переиспользует экземпляры; цепочки публикуются в
/// <see cref="GraphProcessor"/> атомарно. Владение экземплярами: менеджер;
/// выгрузка отложенная (500 мс) — RT мог ещё держать цепочку.
/// </summary>
public sealed class SlotChainManager : IDisposable
{
    private readonly AudioGraph _graph;
    private readonly GraphProcessor _processor;
    private readonly Func<PluginSlot, IAudioPlugin> _factory;
    private readonly object _gate = new();
    private readonly Dictionary<Guid, NodeState> _states = [];

    private int _sampleRate;
    private int _maxBlockFrames;
    private int _channels;
    private bool _prepared;
    private bool _disposed;

    /// <summary>Сообщения неудачных загрузок за последнюю синхронизацию.</summary>
    public IReadOnlyList<string> LastErrors { get; private set; } = [];

    public SlotChainManager(
        AudioGraph graph,
        GraphProcessor processor,
        Func<PluginSlot, IAudioPlugin>? factory = null)
    {
        _graph = graph ?? throw new ArgumentNullException(nameof(graph));
        _processor = processor ?? throw new ArgumentNullException(nameof(processor));
        _factory = factory ?? (slot => ClapLoader.Load(slot.Path, slot.PluginId));
        _graph.Changed += OnGraphChanged;
    }

    /// <summary>
    /// Запоминает формат движка и готовит (в т.ч. новые) экземпляры;
    /// завершается полной синхронизацией.
    /// </summary>
    public void Prepare(int sampleRate, int maxBlockFrames, int channels)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _sampleRate = sampleRate;
            _maxBlockFrames = maxBlockFrames;
            _channels = channels;
            _prepared = true;

            // Экземпляры, загруженные ДО готовности формата, готовим сейчас;
            // новые (в Sync) Prepare получат в Rebuild.
            foreach (var state in _states.Values)
            {
                foreach (var instance in state.Instances)
                {
                    instance?.Prepare(sampleRate, maxBlockFrames, channels);
                }
            }
        }

        Sync();
    }

    /// <summary>Сверяет модель со словарём экземпляров и публикует цепочки.</summary>
    public void Sync()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            var errors = new List<string>();
            var seen = new HashSet<Guid>();
            var publish = new List<(Guid NodeId, IAudioPlugin?[] Chain)>();

            foreach (var node in _graph.Nodes)
            {
                if (node.Kind != NodeKind.Bus)
                {
                    continue;
                }

                seen.Add(node.Id);
                var slots = node.Slots;

                if (slots.Count == 0)
                {
                    if (_states.Remove(node.Id, out var emptyState))
                    {
                        DisposeAll(emptyState);
                    }

                    publish.Add((node.Id, []));
                    continue;
                }

                _states.TryGetValue(node.Id, out var state);
                var identity = BuildIdentity(slots);

                if (state is null || state.Identity != identity)
                {
                    state = Rebuild(node.Id, state, slots, identity, errors);
                }
                else
                {
                    state.Enabled = slots.Select(s => s.Enabled).ToArray();
                }

                publish.Add((node.Id, BuildChain(state)));
            }

            foreach (var id in _states.Keys)
            {
                if (seen.Contains(id))
                {
                    continue;
                }

                if (_states.Remove(id, out var orphan))
                {
                    DisposeAll(orphan);
                }

                publish.Add((id, []));
            }

            LastErrors = errors;
            foreach (var (nodeId, chain) in publish)
            {
                _processor.SetSlotChain(nodeId, chain);
            }
        }

        // Латентности могли измениться — пересбор снимка с компенсацией.
        _processor.Invalidate();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _graph.Changed -= OnGraphChanged;
            foreach (var state in _states.Values)
            {
                DisposeAll(state);
            }

            _states.Clear();
        }
    }

    // ===== Внутреннее =====

    private sealed class NodeState
    {
        public string Identity = string.Empty;
        public string[] Signatures = [];
        public bool[] Enabled = [];
        public List<IAudioPlugin?> Instances = [];
    }

    private void OnGraphChanged(object? sender, GraphChange e) => Sync();

    private static string BuildIdentity(IReadOnlyList<PluginSlot> slots) =>
        string.Join('\n', slots.Select(Signature));

    private static string Signature(PluginSlot slot) =>
        $"{(int)slot.Format}|{slot.Path}|{slot.PluginId}";

    private NodeState Rebuild(
        Guid nodeId,
        NodeState? previous,
        IReadOnlyList<PluginSlot> slots,
        string identity,
        List<string> errors)
    {
        var next = new NodeState
        {
            Identity = identity,
            Signatures = slots.Select(Signature).ToArray(),
            Enabled = slots.Select(s => s.Enabled).ToArray(),
        };

        var dropped = new List<IAudioPlugin?>();
        for (var i = 0; i < slots.Count; i++)
        {
            IAudioPlugin? instance = null;
            if (previous is not null && i < previous.Instances.Count &&
                i < previous.Signatures.Length && previous.Signatures[i] == next.Signatures[i])
            {
                instance = previous.Instances[i];
            }
            else
            {
                if (previous is not null && i < previous.Instances.Count)
                {
                    dropped.Add(previous.Instances[i]);
                }

                try
                {
                    instance = _factory(slots[i]);
                    if (slots[i].State is not null)
                    {
                        instance.SetState(slots[i].State);
                    }

                    if (_prepared)
                    {
                        instance.Prepare(_sampleRate, _maxBlockFrames, _channels);
                    }
                }
                catch (Exception ex)
                {
                    // Сломанный плагин не должен ронять граф: слот молчит.
                    errors.Add($"«{slots[i].Name}» ({slots[i].Path}): {ex.Message}");
                    try
                    {
                        instance?.Dispose();
                    }
                    catch
                    {
                        // Загрузка упала посередине — глотаем ошибку деструктора.
                    }

                    instance = null;
                }
            }

            next.Instances.Add(instance);
        }

        if (previous is not null)
        {
            // Несовпавшие старые экземпляры (включая хвост удалённых слотов).
            for (var i = next.Instances.Count; i < previous.Instances.Count; i++)
            {
                dropped.Add(previous.Instances[i]);
            }

            foreach (var old in dropped)
            {
                DeferredDispose(old);
            }
        }

        _states[nodeId] = next;
        return next;
    }

    private IAudioPlugin?[] BuildChain(NodeState state)
    {
        var chain = new List<IAudioPlugin?>(state.Instances.Count);
        for (var i = 0; i < state.Instances.Count; i++)
        {
            if (state.Enabled[i] && state.Instances[i] is not null)
            {
                chain.Add(state.Instances[i]);
            }
        }

        return [.. chain];
    }

    private void DisposeAll(NodeState state)
    {
        foreach (var instance in state.Instances)
        {
            DeferredDispose(instance);
        }
    }

    private static void DeferredDispose(IAudioPlugin? instance)
    {
        if (instance is null)
        {
            return;
        }

        _ = Task.Delay(500).ContinueWith(
            _ =>
            {
                try
                {
                    instance.Dispose();
                }
                catch
                {
                    // Ошибка деструктора плагина не должна валить процесс.
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.Default);
    }
}
