using System.Diagnostics;
using Parrhesia.Core.Graph;
using Parrhesia.Plugins;
using Parrhesia.Plugins.Clap;
using Parrhesia.Plugins.Vst3;

namespace Parrhesia.Audio.Processing;

/// <summary>
/// Синхронизация слотов-вставок модели с рантайм-экземплярами плагинов.
/// Реагирует на graph.Changed КОАЛЕСЦИРУЮЩЕЙ фоновой задачей: dlopen и
/// инициализация VST3/CLAP — секунды, UI-поток их не ждёт (P-волна).
/// Быстрая сверка сигнатур «формат|путь|id» — загрузка/выгрузка только при
/// реальных изменениях, переключение Enabled переиспользует экземпляры;
/// цепочки публикуются в <see cref="GraphProcessor"/> атомарно.
/// Владение экземплярами: менеджер; выгрузка — после подтверждения RT-покоя
/// («снял цепочку → ждал пару блоков → dispose»), а не по фиксированному
/// таймеру. Перед getState/prepare узел ПАУЗИРУЕТСЯ (VST3-контракт: state/setup
/// вызовы вне process) — иначе data race с аудио-потоком роняет рантайм.
/// </summary>
public sealed class SlotChainManager : IDisposable
{
    private readonly AudioGraph _graph;
    private readonly GraphProcessor _processor;
    private readonly Func<PluginSlot, IAudioPlugin> _factory;
    private readonly object _gate = new();
    private readonly Dictionary<Guid, NodeState> _states = [];
    private readonly HashSet<Guid> _paused = [];

    private int _sampleRate;
    private int _maxBlockFrames;
    private int _channels;
    private bool _prepared;
    private bool _disposed;
    private bool _syncQueued;

    /// <summary>Сообщения неудачных загрузок за последнюю синхронизацию.</summary>
    public IReadOnlyList<string> LastErrors { get; private set; } = [];

    /// <summary>Последняя фоновая задача синхронизации (тесты ожидают её).</summary>
    public Task SyncTask { get; private set; } = Task.CompletedTask;

    public SlotChainManager(
        AudioGraph graph,
        GraphProcessor processor,
        Func<PluginSlot, IAudioPlugin>? factory = null)
    {
        _graph = graph ?? throw new ArgumentNullException(nameof(graph));
        _processor = processor ?? throw new ArgumentNullException(nameof(processor));
        _factory = factory ?? LoadPluginByFormat;
        _graph.Changed += OnGraphChanged;
    }

    /// <summary>Дефолтная фабрика: формат слота определяет хост (CLAP/VST3).</summary>
    private static IAudioPlugin LoadPluginByFormat(PluginSlot slot) => slot.Format switch
    {
        PluginFormat.Clap => ClapLoader.Load(slot.Path, slot.PluginId),
        PluginFormat.Vst3 => Vst3Loader.Load(slot.Path, slot.PluginId),
        _ => throw new PluginLoadException($"Неизвестный формат плагина: {slot.Format}"),
    };

    /// <summary>
    /// Запоминает формат движка и готовит (в т.ч. новые) экземпляры;
    /// завершается полной синхронизацией. Живые узлы пере-готовятся на
    /// паузе (вне process — VST3-контракт).
    /// </summary>
    public void Prepare(int sampleRate, int maxBlockFrames, int channels)
    {
        Guid[] targets;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _sampleRate = sampleRate;
            _maxBlockFrames = maxBlockFrames;
            _channels = channels;
            _prepared = true;

            targets = [.. _states
                .Where(static kv => kv.Value.Instances.Exists(static i => i is not null))
                .Select(static kv => kv.Key)];
        }

        foreach (var nodeId in targets)
        {
            WithPausedChain(nodeId, () => PrepareInstances(nodeId));
        }

        Sync();
    }

    /// <summary>Сверяет модель со словарём экземпляров и публикует цепочки.</summary>
    public void Sync()
    {
        var dropped = new List<IAudioPlugin?>();
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

                if (_paused.Contains(node.Id))
                {
                    // Узел на паузе: RT не должен трогать его экземпляры.
                    publish.Add((node.Id, []));
                    continue;
                }

                var slots = node.Slots;

                if (slots.Count == 0)
                {
                    if (_states.Remove(node.Id, out var emptyState))
                    {
                        CollectDropped(emptyState, dropped);
                    }

                    publish.Add((node.Id, []));
                    continue;
                }

                _states.TryGetValue(node.Id, out var state);
                var identity = BuildIdentity(slots);

                if (state is null || state.Identity != identity)
                {
                    state = Rebuild(node.Id, state, slots, identity, errors, dropped);
                }
                else
                {
                    state.Enabled = slots.Select(s => s.Enabled).ToArray();
                }

                publish.Add((node.Id, BuildChain(state)));
            }

            // Материализуем ключи до удалений (Dictionary запрещает мутации при перечислении).
            foreach (var id in _states.Keys.ToList())
            {
                if (seen.Contains(id))
                {
                    continue;
                }

                if (_states.Remove(id, out var orphan))
                {
                    CollectDropped(orphan, dropped);
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

        // Выгрузка вне лока: dispose ждёт RT-покоя (см. DeferredDispose).
        foreach (var plugin in dropped)
        {
            DeferredDispose(plugin);
        }
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
                foreach (var instance in state.Instances)
                {
                    _pendingDispose.Add(instance);
                }
            }

            _states.Clear();
            _paused.Clear();
        }

        foreach (var instance in _pendingDispose)
        {
            DeferredDispose(instance);
        }

        _pendingDispose.Clear();
    }

    private readonly List<IAudioPlugin?> _pendingDispose = [];

    /// <summary>Снимает state живых плагинов в модель (вызывается перед сохранением
    /// профиля). Изменения, равные текущим, не поднимают Changed — без
    /// лишних пересчётов и лишних автосейвов. Каждый узел собирается на
    /// паузе: getState параллельно с process — data race (VST3-контракт).</summary>
    public void CollectStates()
    {
        Guid[] nodes;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            nodes = [.. _states.Keys];
        }

        foreach (var nodeId in nodes)
        {
            WithPausedChain(nodeId, () => CollectOne(nodeId));
        }
    }

    /// <summary>Живой экземпляр слота (null — слот не загружен). UI-поток, для редактора.</summary>
    public IAudioPlugin? GetSlotInstance(Guid nodeId, int slotIndex)
    {
        lock (_gate)
        {
            if (_disposed ||
                !_states.TryGetValue(nodeId, out var state) ||
                slotIndex < 0 ||
                slotIndex >= state.Instances.Count)
            {
                return null;
            }

            return state.Instances[slotIndex];
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

    private void OnGraphChanged(object? sender, GraphChange e) => QueueSync();

    /// <summary>Коалесцирующая фоновая синхронизация: загрузка плагинов (dlopen,
    /// инициализация, COM) — секунды; UI-поток обязан оставаться живым.</summary>
    public void QueueSync()
    {
        lock (_gate)
        {
            if (_syncQueued || _disposed)
            {
                return;
            }

            _syncQueued = true;
            SyncTask = Task.Run(() =>
            {
                lock (_gate)
                {
                    _syncQueued = false;
                }

                Sync();
            });
        }
    }

    /// <summary>
    /// Пауза цепочки узла: убирает узел из публикации, ждёт, пока RT-поток
    /// отработает снятую цепочку (VST3-контракт: getState/prepare — вне
    /// process), выполняет действие и возвращает цепочку синхронизацией.
    /// Движок не тянет блоки — выходим по таймауту (RT в таком случае не держит).
    /// </summary>
    private void WithPausedChain(Guid nodeId, Action action)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _paused.Add(nodeId);
            _processor.SetSlotChain(nodeId, []);
        }

        WaitBlocks(_processor.PullCount, count: 2, timeoutMs: 500);

        try
        {
            action();
        }
        finally
        {
            lock (_gate)
            {
                _paused.Remove(nodeId);
            }

            Sync();
        }
    }

    /// <summary>Ждёт приращения счётчика RT-блоков (или таймаута). Вызывать вне _gate.</summary>
    private void WaitBlocks(long from, int count, int timeoutMs)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (_processor.PullCount - from >= count)
            {
                return;
            }

            Thread.Sleep(2);
        }
    }

    /// <summary>Выгрузка после подтверждения RT-покоя: цепочки уже пере-опубликованы,
    /// ждём пару RT-блоков (или таймаут, если движок не тянет) — вместо прежних
    /// «500 мс и надейся».</summary>
    private void DeferredDispose(IAudioPlugin? instance)
    {
        if (instance is null)
        {
            return;
        }

        _ = Task.Run(() =>
        {
            WaitBlocks(_processor.PullCount, count: 2, timeoutMs: 500);
            try
            {
                instance.Dispose();
            }
            catch
            {
                // Ошибка деструктора плагина не должна валить процесс.
            }
        });
    }

    private void CollectOne(Guid nodeId)
    {
        lock (_gate)
        {
            if (_disposed || !_states.TryGetValue(nodeId, out var state))
            {
                return;
            }

            var slots = _graph.FindNode(nodeId)?.Slots;
            if (slots is null)
            {
                return;
            }

            for (var i = 0; i < slots.Count && i < state.Instances.Count; i++)
            {
                var instance = state.Instances[i];
                if (instance is null)
                {
                    continue;
                }

                try
                {
                    var bytes = instance.GetState();
                    if (bytes is null || bytes.SequenceEqual(slots[i].State ?? []))
                    {
                        continue;
                    }

                    _graph.SetSlotState(nodeId, i, bytes);
                }
                catch
                {
                    // Плагин может отказаться от state — не критично.
                }
            }
        }
    }

    private static string BuildIdentity(IReadOnlyList<PluginSlot> slots) =>
        string.Join('\n', slots.Select(Signature));

    private static string Signature(PluginSlot slot) =>
        $"{(int)slot.Format}|{slot.Path}|{slot.PluginId}";

    private static void CollectDropped(NodeState state, List<IAudioPlugin?> dropped)
    {
        foreach (var instance in state.Instances)
        {
            dropped.Add(instance);
        }
    }

    private NodeState Rebuild(
        Guid nodeId,
        NodeState? previous,
        IReadOnlyList<PluginSlot> slots,
        string identity,
        List<string> errors,
        List<IAudioPlugin?> dropped)
    {
        var next = new NodeState
        {
            Identity = identity,
            Signatures = slots.Select(Signature).ToArray(),
            Enabled = slots.Select(s => s.Enabled).ToArray(),
        };

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

                var stopwatch = Stopwatch.StartNew();
                Trace.WriteLine(
                    $"[Parrhesia.Audio][Info] слот: загрузка «{slots[i].Name}» ({slots[i].Path})…");
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

                    Trace.WriteLine(
                        $"[Parrhesia.Audio][Info] слот: «{slots[i].Name}» загружен за " +
                        $"{stopwatch.ElapsedMilliseconds} мс");
                }
                catch (Exception ex)
                {
                    // Сломанный плагин не должен ронять граф: слот молчит.
                    Trace.WriteLine(
                        $"[Parrhesia.Audio][Error] слот: «{slots[i].Name}» ({slots[i].Path}) " +
                        $"не загрузился за {stopwatch.ElapsedMilliseconds} мс: {ex.Message}");
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

    private void PrepareInstances(Guid nodeId)
    {
        lock (_gate)
        {
            if (_disposed || !_states.TryGetValue(nodeId, out var state))
            {
                return;
            }

            foreach (var instance in state.Instances)
            {
                if (instance is null)
                {
                    continue;
                }

                try
                {
                    instance.Prepare(_sampleRate, _maxBlockFrames, _channels);
                }
                catch (Exception ex)
                {
                    // Prepare-отказ оставляет слот «молчащим» (шим вернёт Fail в Process).
                    Trace.WriteLine($"[Parrhesia.Audio][Error] слот: prepare не прошёл: {ex.Message}");
                }
            }
        }
    }
}
