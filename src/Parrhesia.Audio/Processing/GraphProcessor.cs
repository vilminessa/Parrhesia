using System.Collections.Concurrent;
using System.Diagnostics;
using Parrhesia.Core.Graph;
using Parrhesia.Plugins;

namespace Parrhesia.Audio.Processing;

/// <summary>Метрики микшера для отчёта задержки (M-волна).</summary>
public sealed record MixerStats(double CpuUsPerBlock, double BlocksPerSec, int LastFrames);

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
    /// <summary>Стерео-диагональ для быстрого пути (сплошное сложение буферов).</summary>
    private static readonly ChannelMap StraightStereo = ChannelMap.Diagonal(2, 2);

    private readonly AudioGraph _graph;
    private readonly int _channels;
    private readonly Dictionary<Guid, ISampleInput> _inputs = [];
    private readonly Dictionary<Guid, float[]> _buffers = [];
    private readonly ConcurrentDictionary<Guid, float> _peaks = [];

    /// <summary>Активные цепочки плагинов по узлам (см. SetSlotChain).</summary>
    private readonly ConcurrentDictionary<Guid, SlotChainHolder> _slotChains = [];

    /// <summary>Компенсирующие задержки на ребрах (параллельные ветки).</summary>
    private readonly ConcurrentDictionary<(Guid From, Guid To), EdgeDelayLine> _edgeDelays = [];

    private GraphSnapshot _snapshot;

    /// <summary>
    /// Смешивание (мьютекс пулов): раньше здесь был эпохальный кэш (2мс) —
    /// он добавлял повторы/дыры при частых или разных-по-размеру пулах сников.
    /// Нынешняя модель: каждый пулл СВЕЖИЙ, но читает входы РОВНО СВОЕЙ
    /// ветки (upstream по рёбрам, включая disabled-маршруты — их источники
    /// остаются «живыми» для метрик). Для disjoint-схемы (у владельца:
    /// Микрофон→In1, Захват→Динамики) каждый вход читается ровно СВОИМ
    /// потребителем — двойного дренажа и пустых блоков нет.
    /// Внимание: общий (shared) вход, питающий несколько сников, читается
    /// каждым из них — для таких схем позже нужен отдельный латч.
    /// </summary>
    private readonly object _mixGate = new();

    // --- Метрики микшера (под _mixGate) ---
    private long _mixBlocks;
    private long _mixCpuTicks;
    private int _mixLastFrames;
    private long _mixWindowStamp;
    private long _mixWindowBlocks;
    private double _mixBlocksPerSec;

    public GraphProcessor(AudioGraph graph, int channels = 2)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentOutOfRangeException.ThrowIfLessThan(channels, 1);

        _graph = graph;
        _channels = channels;
        _snapshot = GraphSnapshot.Build(graph, ChainLatency);
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

    /// <summary>
    /// Атомарно публикует цепочку плагинов узла: RT-поток читает свежий
    /// массив со следующего блока. Выгружается с отложенным грейсом (500 мс)
    /// только то, чего НЕТ в новой цепочке — ре-публикация тех же экземпляров
    /// (каждый graph.Changed) не должна их убивать; RT мог ещё держать ссылку.
    /// </summary>
    public void SetSlotChain(Guid nodeId, IAudioPlugin?[] chain)
    {
        ArgumentNullException.ThrowIfNull(chain);
        var holder = _slotChains.GetOrAdd(nodeId, static _ => new SlotChainHolder());
        var previous = holder.Plugins;
        if (ReferenceEquals(previous, chain))
        {
            return;
        }

        holder.Plugins = chain;
        foreach (var old in previous)
        {
            if (old is not null && Array.IndexOf(chain, old) < 0)
            {
                ScheduleDispose(old);
            }
        }
    }

    private static void ScheduleDispose(IAudioPlugin plugin) =>
        _ = Task.Delay(500).ContinueWith(
            _ =>
            {
                try
                {
                    plugin.Dispose();
                }
                catch
                {
                    // Ошибка деструктора плагина не должна валить процесс.
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.Default);

    /// <summary>Суммарная латентность активной цепочки узла (в кадрах).</summary>
    public int ChainLatency(Guid nodeId)
    {
        if (!_slotChains.TryGetValue(nodeId, out var holder))
        {
            return 0;
        }

        var chain = holder.Plugins;
        var total = 0;
        foreach (var plugin in chain)
        {
            if (plugin is not null)
            {
                total += plugin.LatencySamples;
            }
        }

        return total;
    }

    /// <summary>Пик (linear, |x|) сигнала узла после гейна за последний блок.</summary>
    public float GetPeak(Guid nodeId) =>
        _peaks.TryGetValue(nodeId, out var peak) ? peak : 0f;

    /// <summary>Пересобрать снимок вручную (обычно делает подписка на Changed).</summary>
    public void Invalidate()
    {
        var snapshot = GraphSnapshot.Build(_graph, ChainLatency);
        PrepareEdgeDelays(snapshot);
        Volatile.Write(ref _snapshot, snapshot);
    }

    /// <summary>
    /// Предаллокация линий задержек ВНЕ RT-потока (graph.Changed приходит с UI)
    /// и чистка устаревших рёбер.
    /// </summary>
    private void PrepareEdgeDelays(GraphSnapshot snapshot)
    {
        var active = new HashSet<(Guid, Guid)>();
        foreach (var edge in snapshot.Edges)
        {
            active.Add((edge.From, edge.To));
            if (edge.CompensationDelay <= 0)
            {
                continue;
            }

            var line = _edgeDelays.GetOrAdd((edge.From, edge.To), static _ => new EdgeDelayLine());
            line.Ensure(edge.CompensationDelay * _channels, 8192);
        }

        foreach (var key in _edgeDelays.Keys)
        {
            if (!active.Contains(key))
            {
                _edgeDelays.TryRemove(key, out _);
            }
        }
    }

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

        lock (_mixGate)
        {
            var started = Stopwatch.GetTimestamp();
            var snapshot = Volatile.Read(ref _snapshot);
            EnsureBuffers(snapshot, samples);

            // Только своя ветка: чужие входы не читаются (иначе каждый пулл
            // дренажил бы ВСЕ кольца — при2+ выходах второй пулл видел пустоту).
            var upstream = CollectUpstream(sinkId, snapshot);
            foreach (var node in snapshot.Nodes) // топологический порядок
            {
                if (!upstream.Contains(node.Id))
                {
                    continue;
                }

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
                var sinkChannels = SnapshotChannelCount(snapshot, sinkId);
                if (sinkChannels == 1 && _channels > 1)
                {
                    // Моно-назначение: первый канал дублируется на все,
                    // иначе звук уходил бы только в левый динамик.
                    var monoFrames = samples / _channels;
                    for (var frame = 0; frame < monoFrames; frame++)
                    {
                        var value = sinkBuffer[frame * _channels];
                        for (var channel = 0; channel < _channels; channel++)
                        {
                            output[(frame * _channels) + channel] = value;
                        }
                    }
                }
                else
                {
                    sinkBuffer.AsSpan(0, samples).CopyTo(output[..samples]);
                }
            }
            else
            {
                output[..samples].Clear();
            }

            if (output.Length > samples)
            {
                output[samples..].Clear();
            }

            _mixBlocks++;
            _mixCpuTicks += Stopwatch.GetTimestamp() - started;
            _mixLastFrames = frames;
        }
    }

    /// <summary>Снимок метрик микшера (для отчёта задержки; окно ~0.5 с для частоты пуллов).</summary>
    public MixerStats GetMixerStats()
    {
        lock (_mixGate)
        {
            var now = Stopwatch.GetTimestamp();
            if (_mixWindowStamp == 0)
            {
                _mixWindowStamp = now;
                _mixWindowBlocks = _mixBlocks;
            }
            else
            {
                var elapsedSec = (now - _mixWindowStamp) / (double)Stopwatch.Frequency;
                if (elapsedSec >= 0.5)
                {
                    _mixBlocksPerSec = (_mixBlocks - _mixWindowBlocks) / elapsedSec;
                    _mixWindowStamp = now;
                    _mixWindowBlocks = _mixBlocks;
                }
            }

            var cpuUs = _mixBlocks > 0
                ? (_mixCpuTicks * 1_000_000.0 / Stopwatch.Frequency) / _mixBlocks
                : 0;

            return new MixerStats(cpuUs, _mixBlocksPerSec, _mixLastFrames);
        }
    }

    /// <summary>
    /// Узлы, из которых достижим <paramref name="sinkId"/> (обход назад по
    /// рёбрам). Рёбра берутся ВСЕ (включая disabled): источник с выключенным
    /// маршрутом остаётся в ветке — его сигнал читается и метрится.
    /// </summary>
    private static HashSet<Guid> CollectUpstream(Guid sinkId, GraphSnapshot snapshot)
    {
        var result = new HashSet<Guid> { sinkId };
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var edge in snapshot.Edges)
            {
                if (result.Contains(edge.To) && result.Add(edge.From))
                {
                    changed = true;
                }
            }
        }

        return result;
    }

    private static int SnapshotChannelCount(GraphSnapshot snapshot, Guid nodeId)
    {
        foreach (var node in snapshot.Nodes)
        {
            if (node.Id == nodeId)
            {
                return node.ChannelCount;
            }
        }

        return -1;
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

            var source = from.AsSpan(0, samples);
            if (edge.CompensationDelay > 0)
            {
                // Параллельные ветки: выравнивание по самой латентной (см. ComputeLatencies).
                source = DelayEdge(edge, source);
            }

            if (edge.Map.Bits == StraightStereo.Bits)
            {
                // Стерео-диагональ по прямой — данные лежат в буфере сплошняком.
                Accumulate(span, source, edge.Gain);
            }
            else
            {
                foreach (var assignment in edge.Assignments)
                {
                    AccumulateChannel(
                        span,
                        assignment.To,
                        source,
                        assignment.From,
                        edge.Gain * assignment.Scale);
                }
            }
        }

        // Цепочка слотов-вставок шины: суммарный сигнал → плагины → стадия узла.
        // Обход узла (Bypassed) пропускает и цепочку — полная прозрачность.
        if (!node.Bypassed && _slotChains.TryGetValue(node.Id, out var chainHolder))
        {
            var chain = chainHolder.Plugins;
            if (chain.Length > 0)
            {
                var frames = samples / _channels;
                foreach (var plugin in chain)
                {
                    plugin?.Process(span, frames);
                }
            }
        }

        ApplyNodeStage(node, span);
    }

    private Span<float> DelayEdge(EdgeInfo edge, ReadOnlySpan<float> source)
    {
        var line = _edgeDelays.GetOrAdd((edge.From, edge.To), static _ => new EdgeDelayLine());
        return line.Process(source, edge.CompensationDelay * _channels);
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

    /// <summary>
    /// Складывает один канал источника в один канал назначения.
    /// Буферы interleaved: [L0 R0 L1 R1 ...], шаг по кадру = число каналов.
    /// </summary>
    private void AccumulateChannel(
        Span<float> target,
        int targetChannel,
        ReadOnlySpan<float> source,
        int sourceChannel,
        float gain)
    {
        if (sourceChannel >= _channels || targetChannel >= _channels)
        {
            return;
        }

        var frames = target.Length / _channels;
        if (gain == 1f)
        {
            for (var frame = 0; frame < frames; frame++)
            {
                target[(frame * _channels) + targetChannel] += source[(frame * _channels) + sourceChannel];
            }
        }
        else
        {
            for (var frame = 0; frame < frames; frame++)
            {
                target[(frame * _channels) + targetChannel] += source[(frame * _channels) + sourceChannel] * gain;
            }
        }
    }

    /// <summary>
    /// Обход (bypass) — полная прозрачность: сигнал проходит без гейна,
    /// mute/solo игнорируются. Обычный путь: гейн → метр (пик) → обнуление
    /// при mute/solo.
    /// </summary>
    private void ApplyNodeStage(NodeInfo node, Span<float> span)
    {
        if (node.Bypassed)
        {
            _peaks[node.Id] = PeakOf(span);
            return;
        }

        if (node.Gain != 1f)
        {
            for (var i = 0; i < span.Length; i++)
            {
                span[i] *= node.Gain;
            }
        }

        _peaks[node.Id] = PeakOf(span);

        if (node.Muted)
        {
            span.Clear();
        }
    }

    private static float PeakOf(ReadOnlySpan<float> span)
    {
        var peak = 0f;
        for (var i = 0; i < span.Length; i++)
        {
            var absolute = MathF.Abs(span[i]);
            if (absolute > peak)
            {
                peak = absolute;
            }
        }

        return peak;
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

    /// <summary>Атомарно-свопаемая цепочка плагинов узла (RT читает ссылку).</summary>
    private sealed class SlotChainHolder
    {
        public volatile IAudioPlugin?[] Plugins = [];
    }

    /// <summary>
    /// Компенсирующая задержка ребра: классическое кольцо длиной D сэмплов —
    /// выход[i] = вход[i − D] (стартовые D сэмплов — нули, «истории ещё нет»).
    /// Буферы аллоцируются в Ensure (вне RT); scratch — для копии с задержкой.
    /// </summary>
    private sealed class EdgeDelayLine
    {
        private float[] _delay = [];
        private float[] _scratch = [];
        private int _position;

        public void Ensure(int delaySamples, int minScratch)
        {
            if (delaySamples > 0 && _delay.Length != delaySamples)
            {
                _delay = new float[delaySamples];
                _position = 0;
            }

            if (_scratch.Length < minScratch)
            {
                _scratch = new float[minScratch];
            }
        }

        public Span<float> Process(ReadOnlySpan<float> source, int delaySamples)
        {
            if (delaySamples <= 0)
            {
                return _scratch.AsSpan(0, source.Length); // не зовётся: вызывающий фильтрует
            }

            if (_delay.Length != delaySamples || _scratch.Length < source.Length)
            {
                // Фолбэк: линия не готова (ребро появилось без Invalidate) —
                // аллокация в RT только в аномалии.
                Ensure(delaySamples, source.Length);
            }

            var buffer = _delay;
            var capacity = buffer.Length;
            var position = _position;
            for (var i = 0; i < source.Length; i++)
            {
                _scratch[i] = buffer[position];
                buffer[position] = source[i];
                position++;
                if (position == capacity)
                {
                    position = 0;
                }
            }

            _position = position;
            return _scratch.AsSpan(0, source.Length);
        }
    }
}
