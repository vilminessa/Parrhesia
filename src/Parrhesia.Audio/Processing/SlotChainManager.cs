using System.Diagnostics;
using Parrhesia.Core.Graph;
using Parrhesia.Plugins;
using Parrhesia.Plugins.Clap;
using Parrhesia.Plugins.Vst3;

namespace Parrhesia.Audio.Processing;

/// <summary>
/// Синхронизация слотов-вставок модели с рантайм-экземплярами плагинов.
/// Реагирует на graph.Changed коалесцирующей фоновой задачей; полные
/// синхронизации сериализуются <c>_syncWork</c>, но dlopen/COM-инициализация
/// (секунды) идёт ВНЕ <c>_gate</c> — UI-пути (автосейв, инспектор) ждут лок
/// только микросекунды (R-волна: зависания на «+ слот»).
/// Фазы одной синхронизации: A) снимок модели под локом; B) сверка/загрузка
/// вне локов; C) атомарное применение + публикация под локом (со свежей
/// paused-проверкой — пауза узла не должна быть перекрыта устаревшей
/// публикацией). Перед getState/prepare узел ПАУЗИРУЕТСЯ (VST3-контракт:
/// state/setup вызовы вне process). Владение экземплярами: менеджер;
/// выгрузка — после RT-подтверждения покоя.
/// </summary>
public sealed class SlotChainManager : IDisposable
{
    private readonly AudioGraph _graph;
    private readonly GraphProcessor _processor;
    private readonly Func<PluginSlot, IAudioPlugin> _factory;
    private readonly Func<Guid, PluginSlot, IAudioPlugin> _bridgeFactory;
    private readonly object _gate = new();
    private readonly object _syncWork = new();
    private readonly Dictionary<Guid, NodeState> _states = [];
    private readonly HashSet<Guid> _paused = [];

    /// <summary>Бэкофф ретраев спавна исполнителей (только узлы-плагины).</summary>
    private readonly Dictionary<Guid, RetryState> _retries = [];

    /// <summary>Число попыток создания экземпляров по узлам-плагинам (диагностика/тесты).</summary>
    private readonly Dictionary<Guid, int> _spawnAttempts = [];

    private readonly CancellationTokenSource _tickCts = new();

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
        Func<PluginSlot, IAudioPlugin>? factory = null,
        Func<Guid, PluginSlot, IAudioPlugin>? bridgeFactory = null)
    {
        _graph = graph ?? throw new ArgumentNullException(nameof(graph));
        _processor = processor ?? throw new ArgumentNullException(nameof(processor));
        _factory = factory ?? LoadPluginByFormat;

        // Узлы-плагины (S-волна) всегда идут через процесс-исполнитель —
        // единый механизм для всех VST3/CLAP без исключений.
        _bridgeFactory = bridgeFactory ?? ProcessBridgePlugin.Create;
        _graph.Changed += OnGraphChanged;

        // Фоновый тик (S3): подхватывает смерть/зависание исполнителя
        // (NeedsRestart) и ретраи спавна с бэкоффом — без изменений в модели
        // graph.Changed не срабатывает, тикер коалесцируется в QueueSync.
        _ = Task.Run(TickLoopAsync);
    }

    private async Task TickLoopAsync()
    {
        while (true)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), _tickCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            QueueSync();
        }
    }

    /// <summary>Дефолтная фабрика: формат слота определяет хост (CLAP/VST3).</summary>
    private static IAudioPlugin LoadPluginByFormat(PluginSlot slot) => slot.Format switch
    {
        PluginFormat.Clap => ClapLoader.Load(slot.Path, slot.PluginId),
        PluginFormat.Vst3 => LoadVst3(slot),
        _ => throw new PluginLoadException($"Неизвестный формат плагина: {slot.Format}"),
    };

    /// <summary>
    /// VST3: сначала проба модуля в дочернем процессе (песочница T-волны —
    /// Clear и подобные крашат рантайм при Module::create); мусорный
    /// pluginId (в профиле сохранялся путь) заменяется CID из перечисления.
    /// </summary>
    private static IAudioPlugin LoadVst3(PluginSlot slot)
    {
        var probeError = Vst3Sandbox.Probe(slot.Path);
        if (probeError is not null)
        {
            throw new PluginLoadException($"VST3-песочница: {probeError}");
        }

        var classId = LooksLikeClassId(slot.PluginId)
            ? slot.PluginId
            : Vst3Loader.Enumerate(slot.Path).FirstOrDefault()?.PluginId
              ?? throw new PluginLoadException($"VST3: в модуле нет аудио-классов ({slot.Path})");

        return Vst3Loader.Load(slot.Path, classId);
    }

    /// <summary>VST3 class id —32 hex-символа (без дефисов).</summary>
    internal static bool LooksLikeClassId(string value)
    {
        if (value.Length !=32)
        {
            return false;
        }

        foreach (var character in value)
        {
            if (!Uri.IsHexDigit(character))
            {
                return false;
            }
        }

        return true;
    }

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

    /// <summary>Полная сверка модели с экземплярами (сериализована _syncWork).</summary>
    public void Sync()
    {
        lock (_syncWork)
        {
            if (_disposed)
            {
                return;
            }

            // Модель могла меняться, пока грузили — повторяем до стабильности.
            while (SyncOnce() && !_disposed)
            {
            }
        }
    }

    /// <summary>Одна итерация сверки. true — модель изменилась во время
    /// загрузки (устаревший результат отброшен, нужен повтор).</summary>
    private bool SyncOnce()
    {
        // Фаза A: снимок модели (под локом, μs — без dlopen).
        var plan = new List<PlanItem>();
        var emptyDropped = new List<IAudioPlugin?>();
        lock (_gate)
        {
            if (_disposed)
            {
                return false;
            }

            foreach (var node in _graph.Nodes)
            {
                if (node.Kind is not (NodeKind.Bus or NodeKind.Plugin))
                {
                    continue;
                }

                var slots = node.Slots;
                if (slots.Count == 0)
                {
                    // Пустой шине слоты не нужны — состояние выгружаем сразу.
                    if (_states.Remove(node.Id, out var emptyState))
                    {
                        CollectDropped(emptyState, emptyDropped);
                    }

                    _retries.Remove(node.Id);
                    _spawnAttempts.Remove(node.Id);
                    plan.Add(new PlanItem(node.Id, [], string.Empty, null, false, false));
                    continue;
                }

                _states.TryGetValue(node.Id, out var current);
                plan.Add(new PlanItem(
                    node.Id,
                    [.. slots],
                    BuildIdentity(slots),
                    current,
                    false,
                    node.Kind == NodeKind.Plugin));
            }

            // Материализуем ключи до удалений (Dictionary запрещает мутации при перечислении).
            foreach (var id in _states.Keys.ToList())
            {
                if (plan.All(p => p.Id != id))
                {
                    if (_states.Remove(id, out var orphan))
                    {
                        CollectDropped(orphan, emptyDropped);
                    }

                    _retries.Remove(id);
                    _spawnAttempts.Remove(id);
                    plan.Add(new PlanItem(id, [], string.Empty, null, false, false));
                }
            }
        }

        // Фаза B: ВНЕ локов — обновление Enabled либо загрузка (dlopen/COM).
        var built = new List<(Guid Id, NodeState? State)>();
        foreach (var item in plan)
        {
            if (item.Slots.Count == 0)
            {
                built.Add((item.Id, null));
                continue;
            }

            if (item.Current is not null &&
                item.Current.Identity == item.Identity &&
                !PluginNeedsRebuild(item))
            {
                item.Current.Enabled = item.Slots.Select(static s => s.Enabled).ToArray();
                built.Add((item.Id, item.Current));
                continue;
            }

            var stopwatch = Stopwatch.StartNew();
            Trace.WriteLine(
                $"[Parrhesia.Audio][Info] слот: загрузка «{item.Slots[0].Name}» ({item.Slots.Count} шт.)…");
            var state = BuildInstances(item, stopwatch);
            built.Add((item.Id, state));
        }

        // Фаза C: атомарное применение + публикация (под локом, со свежей
        // paused-проверкой: устаревший plan не должен перекрыть паузу узла).
        var dropped = new List<IAudioPlugin?>();
        var errors = new List<string>();
        var retry = false;
        lock (_gate)
        {
            if (_disposed)
            {
                foreach (var (_, state) in built)
                {
                    CollectDropped(state, emptyDropped);
                }

                Retry(emptyDropped);
                return false;
            }

            var seen = new HashSet<Guid>();
            var publish = new List<(Guid NodeId, IAudioPlugin?[] Chain)>();

            foreach (var item in plan)
            {
                seen.Add(item.Id);

                if (item.Slots.Count == 0)
                {
                    publish.Add((item.Id, []));
                    continue;
                }

                if (_paused.Contains(item.Id))
                {
                    // Узел на паузе: RT не должен трогать его экземпляры.
                    publish.Add((item.Id, []));
                    continue;
                }

                // Свежая сверка: слоты могли измениться, пока грузили.
                var node = _graph.FindNode(item.Id);
                var slots = node?.Slots;
                var identity = slots is null ? null : BuildIdentity(slots);
                if (identity != item.Identity)
                {
                    retry = true;
                    var stale = built.FirstOrDefault(b => b.Id == item.Id).State;
                    CollectDropped(stale, dropped);
                    continue;
                }

                var state = built.FirstOrDefault(b => b.Id == item.Id).State!;
                _states[item.Id] = state;
                publish.Add((item.Id, BuildChain(state)));
                errors.AddRange(state.Errors);
            }

            LastErrors = errors;
            foreach (var (nodeId, chain) in publish)
            {
                _processor.SetSlotChain(nodeId, chain);
            }
        }

        Retry(emptyDropped);

        // Латентности могли измениться — пересбор снимка с компенсацией.
        _processor.Invalidate();
        return retry;
    }

    public void Dispose()
    {
        _tickCts.Cancel();

        var pending = new List<IAudioPlugin?>();
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
                CollectDropped(state, pending);
            }

            _states.Clear();
            _paused.Clear();
        }

        Retry(pending);
    }

    /// <summary>Снимает state живых плагинов в модель (вызывается перед сохранением
    /// профиля). Изменения, равные текущим, не поднимают Changed. Каждый узел
    /// собирается на паузе: getState параллельно с process — data race
    /// (VST3-контракт).</summary>
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

    /// <summary>Число попыток создания экземпляров узла (в т.ч. ретраи спавна исполнителя).</summary>
    public int GetSpawnAttempts(Guid nodeId)
    {
        lock (_gate)
        {
            return _spawnAttempts.GetValueOrDefault(nodeId);
        }
    }

    // ===== Внутреннее =====

    /// <summary>План одной итерации: слоты узла на момент снимка + прежнее состояние.</summary>
    private sealed record PlanItem(
        Guid Id,
        IReadOnlyList<PluginSlot> Slots,
        string Identity,
        NodeState? Current,
        bool Paused,
        bool IsPluginNode);

    private sealed class NodeState
    {
        public string Identity = string.Empty;
        public string[] Signatures = [];
        public bool[] Enabled = [];
        public List<IAudioPlugin?> Instances = [];
        public List<string> Errors = [];
    }

    private void OnGraphChanged(object? sender, GraphChange e) => QueueSync();

    /// <summary>Коалесцирующая фоновая синхронизация: загрузка плагинов (dlopen,
    /// COM) — секунды; UI-поток обязан оставаться живым.</summary>
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
    /// ждём пару RT-блоков (или таймаут, если движок не тянет).</summary>
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

    private void Retry(List<IAudioPlugin?> instances)
    {
        foreach (var instance in instances)
        {
            DeferredDispose(instance);
        }

        instances.Clear();
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

    /// <summary>Бэкофф-состояние ретрая спавна исполнителя узла-плагина.</summary>
    private sealed class RetryState
    {
        public long At;

        public int Delay;
    }

    /// <summary>
    /// S3: узел-плагин требует пересоздания экземпляров — исполнитель
    /// умер/завис (NeedsRestart) либо спавн падал и бэкофф-интервал вышел.
    /// </summary>
    private bool PluginNeedsRebuild(PlanItem item)
    {
        if (!item.IsPluginNode || item.Current is null)
        {
            return false;
        }

        for (var i = 0; i < item.Slots.Count; i++)
        {
            if (SlotNeedsRebuild(item, i))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Пересоздание конкретного слота (null — спавн падал: ждём бэкофф).</summary>
    private bool SlotNeedsRebuild(PlanItem item, int index)
    {
        if (!item.IsPluginNode || item.Current is null)
        {
            return false;
        }

        var instance = index < item.Current.Instances.Count ? item.Current.Instances[index] : null;
        if (instance is not null)
        {
            return instance is ProcessBridgePlugin { NeedsRestart: true };
        }

        return !_retries.TryGetValue(item.Id, out var retry) ||
               Environment.TickCount64 >= retry.At;
    }

    private static string Signature(PluginSlot slot) =>
        $"{(int)slot.Format}|{slot.Path}|{slot.PluginId}";

    private static void CollectDropped(NodeState? state, List<IAudioPlugin?> dropped)
    {
        if (state is null)
        {
            return;
        }

        foreach (var instance in state.Instances)
        {
            dropped.Add(instance);
        }
    }

    /// <summary>Загрузка/пересоздание экземпляров плана ВНЕ локов (dlopen/COM).</summary>
    private NodeState BuildInstances(PlanItem item, Stopwatch stopwatch)
    {
        var next = new NodeState
        {
            Identity = item.Identity,
            Signatures = item.Slots.Select(Signature).ToArray(),
            Enabled = item.Slots.Select(static s => s.Enabled).ToArray(),
        };

        // Несовпавшие старые экземпляры (включая хвост удалённых слотов) —
        // к выгрузке после публикации (DeferredDispose ждёт RT-покой).
        var replaced = new List<IAudioPlugin?>();

        for (var i = 0; i < item.Slots.Count; i++)
        {
            IAudioPlugin? instance = null;
            if (item.Current is not null && i < item.Current.Instances.Count &&
                i < item.Current.Signatures.Length && item.Current.Signatures[i] == next.Signatures[i] &&
                !SlotNeedsRebuild(item, i))
            {
                instance = item.Current.Instances[i];
            }
            else
            {
                // Последнее известное состояние умирающего исполнителя —
                // переезжает в новый экземпляр (только при ТОМ ЖЕ плагине:
                // при смене слота state чужого плагина не нужен).
                byte[]? inherited = null;
                if (item.Current is not null && i < item.Current.Instances.Count)
                {
                    if (i < item.Current.Signatures.Length &&
                        item.Current.Signatures[i] == next.Signatures[i])
                    {
                        inherited = (item.Current.Instances[i] as ProcessBridgePlugin)?.TakePendingState();
                    }

                    replaced.Add(item.Current.Instances[i]);
                }

                if (item.IsPluginNode)
                {
                    _spawnAttempts[item.Id] = _spawnAttempts.GetValueOrDefault(item.Id) + 1;
                }

                try
                {
                    // Узел-плагин (S-волна) — всегда процесс-исполнитель;
                    // шина (легаси) — прежняя in-process фабрика.
                    instance = item.IsPluginNode
                        ? _bridgeFactory(item.Id, item.Slots[i])
                        : _factory(item.Slots[i]);

                    var state = item.Slots[i].State ?? inherited;
                    if (state is not null)
                    {
                        instance.SetState(state);
                    }

                    if (_prepared)
                    {
                        instance.Prepare(_sampleRate, _maxBlockFrames, _channels);
                    }

                    if (item.IsPluginNode)
                    {
                        _retries.Remove(item.Id); // успех — бэкофф сброшен
                    }

                    if (stopwatch.ElapsedMilliseconds > 10_000)
                    {
                        // Наблюдаемость вечно-висящей загрузки (отменить нельзя).
                        Trace.WriteLine(
                            $"[Parrhesia.Audio][Warn] слот: «{item.Slots[i].Name}» загружается " +
                            $"уже {stopwatch.ElapsedMilliseconds} мс — плагин висит?");
                    }

                    Trace.WriteLine(
                        $"[Parrhesia.Audio][Info] слот: «{item.Slots[i].Name}» загружен за " +
                        $"{stopwatch.ElapsedMilliseconds} мс");
                }
                catch (Exception ex)
                {
                    // Сломанный плагин не должен ронять граф: слот молчит.
                    Trace.WriteLine(
                        $"[Parrhesia.Audio][Error] слот: «{item.Slots[i].Name}» ({item.Slots[i].Path}) " +
                        $"не загрузился за {stopwatch.ElapsedMilliseconds} мс: {ex.Message}");
                    next.Errors.Add($"«{item.Slots[i].Name}» ({item.Slots[i].Path}): {ex.Message}");

                    if (item.IsPluginNode)
                    {
                        // Ретрай спавна с бэкоффом:2с →4с →… →2мин.
                        var delay = _retries.TryGetValue(item.Id, out var retry)
                            ? Math.Min(retry.Delay * 2, 120_000)
                            : 2_000;
                        _retries[item.Id] = new RetryState
                        {
                            At = Environment.TickCount64 + delay,
                            Delay = delay,
                        };
                    }

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

        // Хвост удалённых слотов: старые экземпляры выгружаются после публикации.
        if (item.Current is not null)
        {
            for (var i = next.Instances.Count; i < item.Current.Instances.Count; i++)
            {
                replaced.Add(item.Current.Instances[i]);
            }
        }

        foreach (var old in replaced)
        {
            DeferredDispose(old);
        }

        return next;
    }

    private static IAudioPlugin?[] BuildChain(NodeState state)
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
        IAudioPlugin?[] instances;
        int rate, maxBlockFrames, channels;
        lock (_gate)
        {
            if (_disposed || !_states.TryGetValue(nodeId, out var state))
            {
                return;
            }

            instances = [.. state.Instances];
            rate = _sampleRate;
            maxBlockFrames = _maxBlockFrames;
            channels = _channels;
        }

        // Вне лока: пере-подготовка узла-плагина спавнит процесс-исполнитель
        // (секунды) — UI-поток (автосейв/инспектор) не должен ждать.
        foreach (var instance in instances)
        {
            if (instance is null)
            {
                continue;
            }

            try
            {
                instance.Prepare(rate, maxBlockFrames, channels);
            }
            catch (Exception ex)
            {
                // Prepare-отказ оставляет слот «молчащим» (шим вернёт Fail в Process).
                Trace.WriteLine($"[Parrhesia.Audio][Error] слот: prepare не прошёл: {ex.Message}");
            }
        }
    }
}
