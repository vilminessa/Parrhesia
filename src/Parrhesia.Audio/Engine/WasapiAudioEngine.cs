using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Parrhesia.Audio.Buffers;
using Parrhesia.Audio.Devices;
using Parrhesia.Audio.Processing;
using Parrhesia.Core.Graph;
using Parrhesia.Plugins;

namespace Parrhesia.Audio.Engine;

/// <summary>
/// WASAPI-движок: открывает назначение (первый Sink с привязанным устройством),
/// источники (захват/loopback), ведёт микширование через <see cref="GraphProcessor"/>
/// и отдаёт звук в <see cref="WasapiPlayer"/>.
/// События устройств и графа приходят из UI-потока; все перезапуски сериализуются
/// замком. RT-поток (захват/вывод) замок не берёт.
/// </summary>
public sealed class WasapiAudioEngine : IAudioEngine
{
    // Фолбэк-латентность (мс), если IAudioClient3 low-latency недоступен:
    // запрос20мс (было50); сами клиенты идут через WithLowLatency(true).
    private const int OutputLatencyMs = 20;

    // Фолбэк-буфер capture (мс) при отказе low-latency; дефолт NAudio =100.
    private const int CaptureBufferMs = 30;
    private static readonly TimeSpan StatsResetDelay = TimeSpan.FromMilliseconds(1500);

    /// <summary>Кольцо на ~0.35 с — запас на джиттер и дрейф часов устройств.</summary>
    private const double RingSeconds = 0.35;

    private static readonly Guid IeeeFloatSubFormat = new("00000003-0000-0010-8000-00aa00389b71");

    private readonly AudioGraph _graph;
    private readonly GraphProcessor _processor;
    private readonly SlotChainManager _slotChains;
    private readonly IDeviceService _deviceService;
    private readonly MMDeviceEnumerator _enumerator = new();
    private readonly object _gate = new();

    private readonly List<SourceBinding> _sources = [];

    private readonly List<WasapiPlayer> _players = [];
    private DriverFeed? _feed;
    private VirtualSinkPump? _pump;
    private readonly List<MMDevice> _sinkDevices = [];
    private Guid _sinkId;
    private string? _sinkName;
    private string[] _sinkNames = [];
    private int _sampleRate;
    private int _channels;
    private bool _running;

    /// <summary>Явная частота движка из настройки; null — авто-выбор (см. EngineFormat).</summary>
    private int? _configuredSampleRate;

    /// <summary>Общий замок всех RT-потребителей GraphProcessor (помпа + монитор).</summary>
    private readonly object _renderGate = new();

    private string? _monitorDeviceId;
    private WasapiPlayer? _monitorPlayer;
    private MMDevice? _monitorDevice;
    private string? _monitorName;
    private bool _sinkIsVirtual;

    // Кэш «своих» эндпоинтов по InstanceId (М2; лениво, на время движка).
    private HashSet<string>? _ownVirtualEndpoints;

    private bool _restartPending;
    private string _startStage = string.Empty;
    private string[] _bindings = [];
    private Timer? _statsTimer;
    private long _lastLoggedUnderflow;
    private ulong _lastFeedDropped;
    private ulong _lastFeedUnderrun;
    private bool _disposed;

    public WasapiAudioEngine(AudioGraph graph, IDeviceService deviceService)
    {
        _graph = graph ?? throw new ArgumentNullException(nameof(graph));
        _deviceService = deviceService ?? throw new ArgumentNullException(nameof(deviceService));
        _processor = new GraphProcessor(graph);
        _slotChains = new SlotChainManager(graph, _processor);

        _graph.Changed += OnGraphChanged;
        _deviceService.DevicesChanged += OnDevicesChanged;
    }

    public AudioGraph Graph => _graph;

    public event EventHandler<EngineLogEntry>? Log;

    public event EventHandler? StatusChanged;

    public EngineStatus Status => BuildStatus();

    public float GetPeak(Guid nodeId) => _processor.GetPeak(nodeId);

    public void CollectPluginStates() => _slotChains.CollectStates();

    public IAudioPlugin? GetSlotInstance(Guid nodeId, int slotIndex) =>
        _slotChains.GetSlotInstance(nodeId, slotIndex);

    public PluginNodeStatus? GetPluginStatus(Guid nodeId) =>
        _slotChains.GetPluginStatus(nodeId);

    public void RestartPluginNode(Guid nodeId) =>
        _slotChains.RestartPluginNode(nodeId);

    public void Start()
    {
        lock (_gate)
        {
            if (_disposed || _running)
            {
                return;
            }

            StartCore();
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            _restartPending = false; // ручная остановка отменяет автоповтор старта

            if (!_running)
            {
                return;
            }

            Cleanup();
            LogMessage(EngineLogLevel.Info, "Движок остановлен");
            StatusChanged?.Invoke(this, EventArgs.Empty);
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
            _deviceService.DevicesChanged -= OnDevicesChanged;
            Cleanup();
            _processor.Dispose();
            _slotChains.Dispose();
            _enumerator.Dispose();
        }
    }

    private void StartCore()
    {
        _startStage = "инициализация";
        try
        {
            // Переименование — САМОЕ ПЕРВОЕ: после обновления драйвера
            // endpoints приходят с системными именами («Динамики»/«Набор
            // микрофонов»), а перепривязка по имени (E3) ищет «Parrhesia
            // InN/OutN». Раньше rename стоял ПОСЛЕ открытия сников: при
            // отказе сников он не достигался, а без него E3 не находил
            // совпадений — замкнутый круг.
            ApplyVirtualEndpointNames();

            // 1. Планы назначений: все сники с валидной привязкой (loopback как выход запрещён).
            var plans = new List<SinkPlan>();
            foreach (var node in _graph.Nodes)
            {
                if (node.Kind != NodeKind.Sink || node.DeviceId is null)
                {
                    continue;
                }

                if (!DeviceSpec.TryParse(node.DeviceId, out var spec) || spec.Loopback)
                {
                    LogMessage(EngineLogLevel.Warning, $"Недопустимая привязка назначения «{node.DeviceId}»");
                    continue;
                }

                plans.Add(new SinkPlan(node.Id, node.Name, spec));
            }

            if (plans.Count == 0)
            {
                LogMessage(EngineLogLevel.Warning, "Нет назначения с привязанным устройством — движок не запущен");
                return;
            }

            // 2. Открытие: виртуал → фид (физически один), реальные → устройство + игрок.
            //    Отказ одного назначения не валит остальные.
            var opened = new List<OpenedSink>();
            var skipped = 0;
            foreach (var plan in plans)
            {
                if (plan.Spec.Target == DeviceSpecTarget.Virtual)
                {
                    if (_feed is not null)
                    {
                        LogMessage(EngineLogLevel.Warning, $"Виртуальный вывод уже подключён — «{plan.Name}» пропущен");
                        skipped++;
                        continue;
                    }

                    try
                    {
                        // Feed своего инстанса (lanes, М2); при недоступности
                        // PnP — legacy-путь (старый драйвер до переустановки).
                        var suffix = VirtualEndpointResolver.TryGetFeedSuffix(out var s) ? s : null;
                        var feed = new DriverFeed(suffix);
                        if (suffix is not null)
                        {
                            LogMessage(EngineLogLevel.Info, $"Фид инстанса: {DriverFeed.PathFor(suffix)}");
                        }

                        feed.Open(); // бросает исключение, если драйвер не установлен
                        _feed = feed;
                        opened.Add(new OpenedSink(plan, null, null));
                    }
                    catch (Exception ex)
                    {
                        skipped++;
                        LogMessage(EngineLogLevel.Error, $"Назначение «{plan.Name}»: {ex.Message}");
                    }

                    continue;
                }

                if (!TryResolveDevice(plan.Spec, DataFlow.Render, out var device, plan.NodeId))
                {
                    skipped++;
                    continue;
                }

                try
                {
                    var player = new WasapiPlayerBuilder()
                        .WithDevice(device)
                        .WithSharedMode()
                        .WithEventSync()
                        .WithLowLatency(true)
                        .WithMmcssThreadPriority("Pro Audio")
                        .WithLatency(OutputLatencyMs)
                        .Build();

                    _players.Add(player);
                    _sinkDevices.Add(device);
                    opened.Add(new OpenedSink(plan, device, player));
                }
                catch (Exception ex)
                {
                    device.Dispose();
                    skipped++;
                    LogMessage(EngineLogLevel.Error, $"Назначение «{plan.Name}»: {ex.Message}");
                }
            }

            if (opened.Count == 0)
            {
                _feed?.Dispose();
                _feed = null;
                LogMessage(EngineLogLevel.Warning, "Ни одно назначение не открылось — движок не запущен");
                return;
            }

            _startStage = "формат движка";
            // 3. Формат движка: float32/2к; частота — настройка или авто (см. EngineFormat).
            //    Реальные выходы получают блок в формате движка — частоту/каналы
            //    доделывает WASAPI (shared, AutoConvertPcm).
            var hasVirtual = opened.Any(o => o.Plan.Spec.Target == DeviceSpecTarget.Virtual);
            var firstRealMix = opened.FirstOrDefault(o => o.Player is not null)?.Player?.DeviceMixFormat.SampleRate;
            var engineFormat = WaveFormat.CreateIeeeFloatWaveFormat(
                EngineFormat.Resolve(_configuredSampleRate, hasVirtual, firstRealMix),
                EngineFormat.Channels);

            // 4. Сборка выходов: каждый тянет свой блок, все сериализованы общим замком.
            var startStreaming = new List<Action>();
            for (var sinkIndex = 0; sinkIndex < opened.Count; sinkIndex++)
            {
                var sink = opened[sinkIndex];
                _startStage = "выходы";
                IWaveProvider provider = new GraphWaveProvider(_processor, sink.Plan.NodeId, engineFormat);

                if (sink.Plan.Spec.Target == DeviceSpecTarget.Virtual &&
                    (engineFormat.SampleRate != DriverFeed.Rate || engineFormat.Channels != DriverFeed.Channels))
                {
                    // Настройка задала частоту ≠ 48k, а фид требует канон — адаптируем сами.
                    var adapter = SourceFormatAdapter.Create(
                        engineFormat.SampleRate,
                        engineFormat.Channels,
                        DriverFeed.Rate,
                        DriverFeed.Channels);
                    if (adapter is not null)
                    {
                        provider = new AdaptedWaveProvider(provider, adapter);
                    }
                }

                provider = new SerializedWaveProvider(provider, _renderGate);
                if (sink.Plan.Spec.Target == DeviceSpecTarget.Virtual)
                {
                    var pump = new VirtualSinkPump(provider, _feed!);
                    _pump = pump;
                    startStreaming.Add(pump.Start);
                }
                else
                {
                    try
                    {
                        sink.Player!.Init(provider);
                    }
                    catch (Exception ex)
                    {
                        // Устройство с другим mix-форматом (напр. Speakers@44100 при
                        // движке48000): low-latency отказывает жёстко («was required»).
                        // Переоткрываем в обычном shared-режиме — WASAPI сам
                        // конвертирует (AutoConvertPcm), как и до low-latency.
                        LogMessage(
                            EngineLogLevel.Warning,
                            $"Назначение «{sink.Plan.Name}»: low-latency недоступен — обычный shared ({ex.Message})");

                        var oldPlayer = sink.Player!;
                        oldPlayer.Dispose();
                        var plain = new WasapiPlayerBuilder()
                            .WithDevice(sink.Device!)
                            .WithSharedMode()
                            .WithEventSync()
                            .WithLatency(OutputLatencyMs)
                            .Build();
                        plain.Init(provider);

                        var replaced = sink with { Player = plain };
                        opened[sinkIndex] = replaced;
                        for (var pi = 0; pi < _players.Count; pi++)
                        {
                            if (ReferenceEquals(_players[pi], oldPlayer))
                            {
                                _players[pi] = plain;
                                break;
                            }
                        }

                        sink = replaced;
                    }

                    startStreaming.Add(sink.Player!.Play);
                }
            }

            // 5. Идентичность выхода: для монитора и статуса приоритет — виртуальный сник.
            var primary = opened.FirstOrDefault(o => o.Plan.Spec.Target == DeviceSpecTarget.Virtual) ?? opened[0];
            _sinkId = primary.Plan.NodeId;
            _sinkIsVirtual = hasVirtual;
            _sampleRate = engineFormat.SampleRate;
            _channels = engineFormat.Channels;
            _sinkNames = opened.Select(Describe).ToArray();
            _sinkName = Describe(primary);

            // Цепочки слотов: загрузка/подготовка плагинов под формат движка
            // (max-блок100 мс — больше обоих путей вывода: player50 мс, помпа10 мс).
            _slotChains.Prepare(engineFormat.SampleRate, engineFormat.SampleRate / 10, engineFormat.Channels);

            var sinkRenderIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var sink in opened)
            {
                if (sink.Device is not null)
                {
                    sinkRenderIds.Add(sink.Device.ID);
                }
            }

            _startStage = "источники";
            OpenSources(engineFormat, sinkRenderIds);

            _startStage = "старт выходов";
            foreach (var start in startStreaming)
            {
                start();
            }

            StartMonitorIfConfigured();
            foreach (var source in _sources.ToArray())
            {
                source.StartTimestamp = Stopwatch.GetTimestamp();
                source.FirstDataLogged = false;
                try
                {
                    source.Recorder.StartRecording();
                }
                catch (Exception ex)
                {
                    // Один неудачный источник не должен ронять весь тракт (раньше исключение
                    // уходило в общий catch StartCore и валило запуск): сначала фолбэк
                    // на формат устройства, затем отключение источника.
                    if (!TryStartAtDeviceFormat(source, engineFormat, ex))
                    {
                        DisableSource(source, $"не удалось открыть поток: {ex.Message}");
                    }
                }
            }

            _running = true;
            _bindings = CaptureBindings();
            _lastLoggedUnderflow = 0;
            _lastFeedDropped = 0;
            _lastFeedUnderrun = 0;
            _cableDropBaselineSet = false; // база потерь кабеля — заново на каждом старте
            _statsTimer = new Timer(_ => LogStatsIfChanged(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
            var sinkLatency = _players.Count == 0
                ? string.Empty
                : ", латентности выходов " + string.Join(
                    "/",
                    _players.Select(p =>
                        $"{p.LatencyMilliseconds} мс{(p.LowLatencyActive ? string.Empty : " (стандарт)")}"));
            LogMessage(
                EngineLogLevel.Info,
                $"Движок запущен: {_sampleRate} Гц, выходов {_sinkNames.Length} ({string.Join(", ", _sinkNames)}), " +
                $"источников {_sources.Count}{sinkLatency}" +
                (skipped > 0 ? $", назначений пропущено {skipped}" : string.Empty));
            StatusChanged?.Invoke(this, EventArgs.Empty);
            _ = ResetStatsLater();
        }
        catch (Exception ex)
        {
            LogMessage(EngineLogLevel.Error, $"Ошибка запуска движка (этап: {_startStage}): {ex.Message}");
            Cleanup();
            ScheduleStartRetry();
        }
    }

    /// <summary>Человекочитаемое имя выхода для статуса.</summary>
    private static string Describe(OpenedSink sink) =>
        sink.Plan.Spec.Target == DeviceSpecTarget.Virtual
            ? "Parrhesia Out (виртуальный)"
            : sink.Device?.FriendlyName ?? sink.Plan.Name;

    /// <summary>
    /// Принадлежит ли capture-эндпоинт нашему виртуальному драйверу.
    /// Основной путь — InstanceId: PnP-дети root-devnode'а с Service=
    /// VirtualAudioDriver (устойчиво к переименованию и локализации);
    /// фолбэк — имя, если PnP-дерево недоступно.
    /// </summary>
    private bool IsOwnVirtualEndpoint(MMDevice device)
    {
        try
        {
            _ownVirtualEndpoints ??= VirtualEndpointResolver.ResolveVirtualEndpointIds();
            if (_ownVirtualEndpoints.Count > 0)
            {
                return _ownVirtualEndpoints.Contains(device.ID);
            }
        }
        catch
        {
            // PnP недоступно — имя-фолбэк ниже.
        }

        return device.FriendlyName.Contains("Parrhesia", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Ставит «Parrhesia InN/OutN» своим endpoints при старте (N — номер
    /// lanes,1..N по отсортированным instance-id) — только если имя ещё
    /// системное (ручные переименования владельца не трогаем).
    /// </summary>
    private void ApplyVirtualEndpointNames()
    {
        try
        {
            foreach (var (lane, id) in VirtualEndpointResolver.ResolveLanedEndpoints())
            {
                // MMDevice.ID: {0.0.0.…} — рендер, {0.0.1.…} — захват.
                var target = id.StartsWith("{0.0.0.", StringComparison.OrdinalIgnoreCase)
                    ? $"Parrhesia In{lane}"
                    : $"Parrhesia Out{lane}";

                string current;
                try
                {
                    using var enumerator = new MMDeviceEnumerator();
                    using var device = enumerator.GetDevice(id);
                    current = device.FriendlyName;
                }
                catch
                {
                    continue; // поток недоступен — пропускаем
                }

                var baseName = EndpointPolicy.StripAnyDeviceSuffix(current);
                if (!EndpointPolicy.ShouldAutoRename(baseName))
                {
                    continue; // владелец переименовал вручную
                }

                if (EndpointPolicy.TryRename(id, target))
                {
                    LogMessage(EngineLogLevel.Info, $"Эндпоинт переименован: «{baseName}» → «{target}»");
                }
                else
                {
                    LogMessage(
                        EngineLogLevel.Warning,
                        $"Не удалось переименовать эндпоинт «{baseName}»: {EndpointPolicy.LastError}");
                }
            }
        }
        catch (Exception ex)
        {
            LogMessage(EngineLogLevel.Warning, $"Авто-переименование endpoints: {ex.Message}");
        }
    }

    private void OpenSources(WaveFormat engineFormat, IReadOnlySet<string> sinkRenderIds)
    {
        foreach (var node in _graph.Nodes)
        {
            if (node.Kind != NodeKind.Source || node.DeviceId is null)
            {
                continue;
            }

            if (!DeviceSpec.TryParse(node.DeviceId, out var spec))
            {
                LogMessage(EngineLogLevel.Warning, $"Источник «{node.Name}»: привязка «{node.DeviceId}» не разобрана");
                continue;
            }

            if (!TryResolveDevice(
                    spec,
                    spec.Loopback ? DataFlow.Render : DataFlow.Capture,
                    out var device,
                    node.Id))
            {
                continue;
            }

            if (spec.Loopback && sinkRenderIds.Contains(device.ID))
            {
                LogMessage(
                    EngineLogLevel.Warning,
                    $"Источник «{node.Name}» захватывает то же устройство, куда идёт вывод, — это петля; источник пропущен");
                device.Dispose();
                continue;
            }

            // Петля через виртуальный вывод: при sink=фид наш Out-эндпоинт
            // кормится ИЗ этого же тракта — захват его замыкает цикл
            // (микшер → фид → Out → захват → микшер) и разгоняет переполнения.
            // Определение — по InstanceId (PnP-дети нашего root-devnode), имя —
            // только фолбэк при недоступности дерева (М2).
            if (_sinkIsVirtual &&
                device.DataFlow == DataFlow.Capture &&
                IsOwnVirtualEndpoint(device))
            {
                LogMessage(
                    EngineLogLevel.Warning,
                    $"Источник «{node.Name}» захватывает собственный виртуальный вывод «{device.FriendlyName}» — это петля; источник пропущен");
                device.Dispose();
                continue;
            }

            _startStage = $"источник «{node.Name}» («{device.FriendlyName}»)";
            var builder = new WasapiRecorderBuilder()
                .WithDevice(device)
                .WithEventSync()
                .WithLowLatency(true)
                .WithBufferLength(CaptureBufferMs)
                .WithMmcssThreadPriority("Pro Audio");
            // WithFormat НЕ передаём: low-latency shared capture требует
            // формат = device mix format (иначе NAudio отказывает от
            // IAudioClient3 и переоткрывает поток). Mix format наших
            // endpoints = float32/48к/2к = формат движка; для остальных
            // устройств расхождение съест SourceFormatAdapter (см. ниже).
            if (spec.Loopback)
            {
                builder = builder.WithLoopbackCapture();
            }

            var recorder = builder.Build();

            // Ресемплер вместо прежнего скипа: несовпадение формата больше не отключает
            // источник (44.1k-источник при движке 48k теперь звучит, а не пропускается).
            SourceFormatAdapter? adapter = null;
            if (!IsSameFormat(recorder.WaveFormat, engineFormat))
            {
                if (!IsFloat32(recorder.WaveFormat))
                {
                    // Единственная оставшаяся причина скипа: байты не разобрать как float32.
                    LogMessage(
                        EngineLogLevel.Error,
                        $"Источник «{node.Name}»: формат {recorder.WaveFormat.Encoding}/{recorder.WaveFormat.BitsPerSample} бит " +
                        "не поддерживается (нужен float32); источник пропущен");
                    recorder.Dispose();
                    device.Dispose();
                    continue;
                }

                adapter = SourceFormatAdapter.Create(
                    recorder.WaveFormat.SampleRate,
                    recorder.WaveFormat.Channels,
                    engineFormat.SampleRate,
                    engineFormat.Channels);

                if (adapter is not null)
                {
                    LogMessage(
                        EngineLogLevel.Info,
                        $"Источник «{node.Name}»: формат {recorder.WaveFormat.SampleRate} Гц/{recorder.WaveFormat.Channels} к ≠ " +
                        $"движку {engineFormat.SampleRate} Гц/{engineFormat.Channels} к — адаптация включена");
                }
            }

            var capacity = NextPowerOfTwo((int)(engineFormat.SampleRate * engineFormat.Channels * RingSeconds));
            var binding = new SourceBinding(
                node.Id,
                node.Name,
                recorder,
                new SampleRing(capacity),
                device,
                adapter,
                spec.Loopback);
            Attach(binding);
            _processor.SetInput(node.Id, binding.Ring);
            _sources.Add(binding);

            LogMessage(
                EngineLogLevel.Info,
                $"Источник «{node.Name}»: {recorder.WaveFormat.SampleRate} Гц, {recorder.WaveFormat.Channels} к " +
                $"({(spec.Loopback ? "loopback" : "захват")}, {node.DeviceId})" +
                (adapter is null ? string.Empty : " → ресемплер включён"));
        }
    }

    private void OnCaptureData(SourceBinding binding, ReadOnlySpan<byte> data, AudioClientBufferFlags flags)
    {
        if (!binding.FirstDataLogged)
        {
            binding.FirstDataLogged = true;
            var delayMs = (Stopwatch.GetTimestamp() - binding.StartTimestamp) * 1000.0 / Stopwatch.Frequency;
            LogMessage(
                EngineLogLevel.Info,
                $"«{binding.Name}»: первые данные через {delayMs:0} мс, " +
                $"латентность {binding.Recorder.LatencyMilliseconds} мс " +
                $"(low-latency: {binding.Recorder.LowLatencyActive})");
        }

        var source = MemoryMarshal.Cast<byte, float>(data);
        var adapter = binding.Adapter;

        if (flags.HasFlag(AudioClientBufferFlags.Silent))
        {
            var samples = source.Length;
            if (adapter is null)
            {
                Span<float> zeros = samples <= 16384 ? stackalloc float[samples] : new float[samples];
                binding.Ring.Write(zeros);
                return;
            }

            // Тишина идёт через адаптер тем же числом сэмплов источника: фаза ресемплера
            // обязана продвинуться как при реальном сигнале, иначе частота «уплывёт».
            if (binding.SourceScratch.Length < samples)
            {
                binding.SourceScratch = new float[samples];
            }
            else
            {
                binding.SourceScratch.AsSpan(0, samples).Clear();
            }

            WriteAdapted(binding, binding.SourceScratch.AsSpan(0, samples), adapter!);
            return;
        }

        if (adapter is null)
        {
            binding.Ring.Write(source);
            return;
        }

        WriteAdapted(binding, source, adapter);
    }

    /// <summary>Прогоняет блок источника через адаптер и кладёт результат в кольцо (формат движка).</summary>
    private void WriteAdapted(SourceBinding binding, ReadOnlySpan<float> source, SourceFormatAdapter adapter)
    {
        var needed = adapter.MaxDestinationSamples(source.Length);
        if (binding.OutputScratch.Length < needed)
        {
            binding.OutputScratch = new float[needed];
        }

        var written = adapter.Process(source, binding.OutputScratch);
        binding.Ring.Write(binding.OutputScratch.AsSpan(0, written));
    }

    /// <summary>Вешает обработчики потока на рекордер (создание и фолбэк-переоткрытие).</summary>
    private void Attach(SourceBinding binding)
    {
        binding.Recorder.DataAvailable += (data, flags, _, _) => OnCaptureData(binding, data, flags);
        binding.Recorder.RecordingStopped += (_, args) => OnRecordingStopped(binding, args);
    }

    /// <summary>
    /// Отказ потока после старта: помечаем источник неактивным (без изменения списка из
    /// RT-потока) и объясняем причину в логе. Тракт продолжает работать.
    /// </summary>
    private void OnRecordingStopped(SourceBinding binding, StoppedEventArgs args)
    {
        if (args.Exception is null)
        {
            // Штатная остановка при Cleanup — не событие.
            return;
        }

        binding.Failed = true;
        LogMessage(
            EngineLogLevel.Error,
            $"Источник «{binding.Name}»: запись остановлена: {args.Exception.Message}");
    }

    /// <summary>
    /// Фолбэк открытия источника: WASAPI не принял формат движка — переоткрываем поток
    /// в mix-формате устройства (shared-режим принимает его всегда) и ресемплим у себя.
    /// </summary>
    private bool TryStartAtDeviceFormat(SourceBinding binding, WaveFormat engineFormat, Exception error)
    {
        try
        {
            binding.Recorder.Dispose();

            var builder = new WasapiRecorderBuilder()
                .WithDevice(binding.Device)
                .WithEventSync()
                .WithLowLatency(true)
                .WithBufferLength(CaptureBufferMs)
                .WithMmcssThreadPriority("Pro Audio");
            if (binding.Loopback)
            {
                builder = builder.WithLoopbackCapture();
            }

            var recorder = builder.Build();
            SourceFormatAdapter? adapter;
            if (IsSameFormat(recorder.WaveFormat, engineFormat))
            {
                adapter = null;
            }
            else if (!IsFloat32(recorder.WaveFormat))
            {
                recorder.Dispose();
                return false;
            }
            else
            {
                adapter = SourceFormatAdapter.Create(
                    recorder.WaveFormat.SampleRate,
                    recorder.WaveFormat.Channels,
                    engineFormat.SampleRate,
                    engineFormat.Channels);
            }

            binding.Recorder = recorder;
            binding.Adapter = adapter;
            Attach(binding);
            recorder.StartRecording();

            LogMessage(
                EngineLogLevel.Warning,
                $"Источник «{binding.Name}»: формат движка не принят ({error.Message}) — переоткрыт на " +
                $"{recorder.WaveFormat.SampleRate} Гц/{recorder.WaveFormat.Channels} к" +
                (adapter is null ? string.Empty : ", ресемплер включён"));
            return true;
        }
        catch (Exception ex)
        {
            LogMessage(
                EngineLogLevel.Error,
                $"Источник «{binding.Name}»: фолбэк на формат устройства не удался: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Задаёт явную частоту движка (null — авто-выбор, см. <see cref="EngineFormat"/>).
    /// При работающем движке смена частоты = перезапуск тракта: кольца, слоты плагинов
    /// и формат устройств пересобираются заново.
    /// </summary>
    public void SetSampleRate(int? configuredRate)
    {
        var normalized = configuredRate is int rate && EngineFormat.IsSupportedRate(rate) ? (int?)rate : null;
        lock (_gate)
        {
            if (_configuredSampleRate == normalized)
            {
                return;
            }

            _configuredSampleRate = normalized;
            if (!_running)
            {
                return;
            }

            Restart("сменилась частота движка");
        }
    }

    /// <summary>
    /// Переключает устройство мониторинга. При работающем движке перезапускается
    /// только монитор-плеер — источники, помпа и ядро не трогаются (вкл/выкл
    /// не должно ронять тракт).
    /// </summary>
    public void SetMonitorDevice(string? deviceId)
    {
        var normalized = MonitorOutput.NormalizeDeviceId(deviceId);
        lock (_gate)
        {
            var unchanged =
                string.Equals(_monitorDeviceId, normalized, StringComparison.Ordinal) &&
                (normalized is null || _monitorPlayer is not null);
            if (unchanged)
            {
                _monitorDeviceId = normalized;
                return;
            }

            _monitorDeviceId = normalized;
            if (!_running)
            {
                return;
            }

            StopMonitor();
            StartMonitorIfConfigured();
            StatusChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Поднимает монитор-плеер, если задано устройство и основной выход виртуальный.
    /// Отказ устройства = лог и монитор выключен; тракт продолжает работать.
    /// </summary>
    private void StartMonitorIfConfigured()
    {
        if (!MonitorOutput.ShouldStart(_monitorDeviceId, _sinkIsVirtual))
        {
            return;
        }

        MMDevice? device = null;
        WasapiPlayer? player = null;
        try
        {
            if (!DeviceSpec.TryParse(_monitorDeviceId, out var spec) || spec.Loopback)
            {
                LogMessage(EngineLogLevel.Warning, $"Мониторинг: недопустимая привязка «{_monitorDeviceId}»");
                return;
            }

            if (!TryResolveDevice(spec, DataFlow.Render, out device))
            {
                // TryResolveDevice уже залогировал причину.
                return;
            }

            player = new WasapiPlayerBuilder()
                .WithDevice(device)
                .WithSharedMode()
                .WithEventSync()
                .WithLowLatency(true)
                .WithMmcssThreadPriority("Pro Audio")
                .WithLatency(OutputLatencyMs)
                .Build();

            // Монитор получает блок в формате движка: частоту/каналы доделает
            // WASAPI (shared, AutoConvertPcm), GraphProcessor не меняется.
            var format = WaveFormat.CreateIeeeFloatWaveFormat(_sampleRate, _channels);
            var provider = new SerializedWaveProvider(
                new GraphWaveProvider(_processor, _sinkId, format),
                _renderGate);
            player.Init(provider);
            player.Play();

            _monitorDevice = device;
            _monitorPlayer = player;
            _monitorName = device.FriendlyName;
            LogMessage(EngineLogLevel.Info, $"Мониторинг включён: «{_monitorName}»");
        }
        catch (Exception ex)
        {
            player?.Dispose();
            device?.Dispose();
            LogMessage(EngineLogLevel.Error, $"Мониторинг не запущен: {ex.Message}");
        }
    }

    /// <summary>Останавливает и освобождает монитор-плеер (вызывается под _gate).</summary>
    private void StopMonitor()
    {
        if (_monitorPlayer is null && _monitorDevice is null)
        {
            return;
        }

        try
        {
            _monitorPlayer?.Stop();
        }
        catch
        {
            // Устройство могло исчезнуть — не мешаем остановке.
        }

        _monitorPlayer?.Dispose();
        _monitorPlayer = null;
        _monitorDevice?.Dispose();
        _monitorDevice = null;
        _monitorName = null;
    }

    /// <summary>Отключает источник, который не удалось открыть: освобождает поток и убирает вход.</summary>
    private void DisableSource(SourceBinding binding, string reason)
    {
        try
        {
            binding.Recorder.Dispose();
        }
        catch
        {
            // Устройство могло исчезнуть — главное убрать источник из тракта.
        }

        try
        {
            binding.Device.Dispose();
        }
        catch
        {
            // Не мешает отключению.
        }

        _sources.Remove(binding);
        _processor.SetInput(binding.NodeId, null);
        LogMessage(EngineLogLevel.Error, $"Источник «{binding.Name}» отключён: {reason}");
    }

    private void Cleanup()
    {
        // Порядок: сначала глушим RT-потоки, потом освобождаем то, что они трогают.
        foreach (var source in _sources)
        {
            try
            {
                source.Recorder.StopRecording();
            }
            catch
            {
                // Устройство могло исчезнуть — не мешаем остальной очистке.
            }
        }

        foreach (var player in _players)
        {
            try
            {
                player.Stop();
            }
            catch
            {
                // Устройство могло исчезнуть — не мешаем остановке остальных.
            }
        }

        try
        {
            _pump?.Stop();
        }
        catch
        {
            // Фид мог закрыться — не мешаем остальной очистке.
        }

        StopMonitor();

        _processor.ClearInputs();

        foreach (var source in _sources)
        {
            source.Recorder.Dispose();
            source.Device.Dispose();
        }

        _sources.Clear();
        _statsTimer?.Dispose();
        _statsTimer = null;
        foreach (var player in _players)
        {
            player.Dispose();
        }

        _players.Clear();
        _pump?.Dispose();
        _pump = null;
        _feed?.Dispose();
        _feed = null;
        foreach (var device in _sinkDevices)
        {
            device.Dispose();
        }

        _sinkDevices.Clear();
        _sinkId = Guid.Empty;
        _sinkName = null;
        _sinkNames = [];
        _sampleRate = 0;
        _channels = 0;
        _running = false;
        _sinkIsVirtual = false;
        _bindings = [];
    }

    private bool TryResolveDevice(
        DeviceSpec spec,
        DataFlow expectedFlow,
        out MMDevice device,
        Guid? rebindNode = null)
    {
        device = null!;
        if (spec.Target == DeviceSpecTarget.ById)
        {
            if (TryOpenById(spec.DeviceId, expectedFlow, out device))
            {
                return true;
            }

            // GUID исчез (переустановка/обновление драйвера пересоздаёт все
            // endpoints) — пробуем найти устройство по имени из кэша и
            // перепривязываем узел графа (профиль сохраняется автосейвом).
            if (rebindNode is Guid nodeId &&
                TryRebindByName(spec, expectedFlow, out var newId, out var reopened))
            {
                var newSpec = new DeviceSpec(spec.Loopback, DeviceSpecTarget.ById, newId);
                _graph.SetNodeDevice(nodeId, newSpec.ToString());
                LogMessage(
                    EngineLogLevel.Info,
                    $"Привязка восстановлена по имени ({spec.DeviceId} → {newId}): «{DeviceNameCache.GetName(newId)}»");
                device = reopened;
                return true;
            }

            LogMessage(
                EngineLogLevel.Error,
                $"Устройство {spec} недоступно: не найдено (переустановка драйвера?)");
            return false;
        }

        try
        {
            var flow = spec.Target == DeviceSpecTarget.DefaultCapture ? DataFlow.Capture : DataFlow.Render;
            return _enumerator.TryGetDefaultAudioEndpoint(flow, Role.Multimedia, out device!);
        }
        catch (Exception ex)
        {
            LogMessage(EngineLogLevel.Error, $"Устройство {spec} недоступно: {ex.Message}");
            return false;
        }
    }

    /// <summary>Открытие по id: успех → запоминаем имя в кэше (E3).</summary>
    private bool TryOpenById(string id, DataFlow expectedFlow, out MMDevice device)
    {
        device = null!;
        try
        {
            var found = _enumerator.GetDevice(id);
            if (found.DataFlow != expectedFlow)
            {
                LogMessage(
                    EngineLogLevel.Error,
                    $"Устройство {id} — не тот поток данных ({found.DataFlow}, ожидался {expectedFlow})");
                found.Dispose();
                return false;
            }

            DeviceNameCache.Update(id, found.FriendlyName);
            device = found;
            return true;
        }
        catch (Exception)
        {
            return false; // id не найден — дальше перепривязка или лог вызывающего
        }
    }

    /// <summary>
    /// Поиск замены по имени из кэша: одно ЗАМКНУТОЕ совпадение в активных
    /// устройствах потока → открыто и возвращено;0/несколько → false.
    /// </summary>
    private bool TryRebindByName(
        DeviceSpec spec,
        DataFlow flow,
        out string newId,
        out MMDevice device)
    {
        newId = string.Empty;
        device = null!;

        var name = DeviceNameCache.GetName(spec.DeviceId);
        if (string.IsNullOrEmpty(name))
        {
            return false;
        }

        try
        {
            using var active = _enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active);
            var candidates = new List<(string Id, string Name)>();
            foreach (var item in active)
            {
                try
                {
                    candidates.Add((item.ID, item.FriendlyName));
                }
                finally
                {
                    item.Dispose();
                }
            }

            var match = DeviceNameCache.PickUniqueByName(candidates, name);
            if (match is null)
            {
                return false;
            }

            device = _enumerator.GetDevice(match);
            newId = match;
            return true;
        }
        catch (Exception ex)
        {
            LogMessage(EngineLogLevel.Warning, $"Перепривязка по имени не удалась: {ex.Message}");
            device = null!;
            newId = string.Empty;
            return false;
        }
    }

    private void OnDevicesChanged(object? sender, EventArgs e) => Restart("изменились устройства");

    private void OnGraphChanged(object? sender, GraphChange e)
    {
        if (e.Kind is not (GraphChangeKind.NodeAdded
            or GraphChangeKind.NodeRemoved
            or GraphChangeKind.NodeChanged
            or GraphChangeKind.Reset))
        {
            return;
        }

        lock (_gate)
        {
            if (!_running)
            {
                return;
            }

            var current = CaptureBindings();
            if (!_bindings.SequenceEqual(current, StringComparer.Ordinal))
            {
                Restart("изменилась привязка устройств");
            }
        }
    }

    private void Restart(string reason)
    {
        // Фоном: остановка/старт WASAPI может ждать события от устройства
        // долго (а на исчезающем — виснуть), UI-поток блокировать нельзя.
        _restartPending = true;
        _ = Task.Run(() =>
        {
            var startedAt = Stopwatch.GetTimestamp();
            lock (_gate)
            {
                if (_disposed || !_running)
                {
                    return;
                }

                LogMessage(EngineLogLevel.Info, $"Перезапуск движка ({reason})");
                Cleanup();
                StartCore();
            }

            var elapsedMs = (Stopwatch.GetTimestamp() - startedAt) * 1000 / Stopwatch.Frequency;
            LogMessage(EngineLogLevel.Info, $"Рестарт «{reason}» занял {elapsedMs} мс");
            StatusChanged?.Invoke(this, EventArgs.Empty);
        });
    }

    /// <summary>
    /// Однократный автоповтор: если старт упал после смены привязки
    /// (типично «device disconnected» во время переназначений) — пробуем
    /// ещё раз через2 секунды. Ошибка первичного старта не ретраится.
    /// </summary>
    private void ScheduleStartRetry()
    {
        if (!_restartPending)
        {
            return;
        }

        _restartPending = false; // только один повтор
        _ = Task.Delay(TimeSpan.FromSeconds(2)).ContinueWith(_ =>
        {
            lock (_gate)
            {
                if (_disposed || _running)
                {
                    return;
                }

                LogMessage(EngineLogLevel.Info, "Повторный запуск движка после ошибки");
                StartCore();
            }

            StatusChanged?.Invoke(this, EventArgs.Empty);
        });
    }

    private string[] CaptureBindings() =>
        _graph.Nodes
            .Select(n => n.Id.ToString("N") + "=" + (n.DeviceId ?? "-"))
            .ToArray();

    private EngineStatus BuildStatus()
    {
        if (!_running)
        {
            return EngineStatus.Stopped;
        }

        long underruns = 0;
        long overflows = 0;
        var active = 0;
        foreach (var source in _sources)
        {
            if (source.Failed)
            {
                continue;
            }

            active++;
            underruns += source.Ring.UnderrunSamples;
            overflows += source.Ring.OverflowSamples;
        }

        return new EngineStatus(true, _sampleRate, _channels, _sinkName, active, underruns, overflows)
        {
            MonitorName = _monitorName,
            MonitorActive = _monitorPlayer is not null,
            SinkNames = _sinkNames,
        };
    }

    private async Task ResetStatsLater()
    {
        try
        {
            await Task.Delay(StatsResetDelay).ConfigureAwait(false);
        }
        catch (TaskCanceledException)
        {
            return;
        }

        lock (_gate)
        {
            // Сбрасываем стартовые xrun'ы (первые блоки идут до наполнения колец).
            long underflow = 0;
            long overflow = 0;
            foreach (var source in _sources)
            {
                if (source.Failed)
                {
                    continue;
                }

                underflow += source.Ring.UnderrunSamples;
                overflow += source.Ring.OverflowSamples;
            }

            if (underflow > 0 || overflow > 0)
            {
                LogMessage(
                    EngineLogLevel.Info,
                    $"стартовые xrun сброшены: под={underflow}, переп={overflow}");
            }

            foreach (var source in _sources)
            {
                source.Ring.ResetStatistics();
            }

            _lastLoggedUnderflow = 0;
        }
    }

    /// <summary>Раз в5 секунд пишет дельту xrun — только если под-счётчик растёт.</summary>
    private void LogLatencySummary()
    {
        try
        {
            var report = GetLatencyReport();
            if (report.Stages.Count == 0)
            {
                return;
            }

            var slow = report.Slowest;
            LogMessage(
                EngineLogLevel.Info,
                $"задержка: итого≈{report.TotalMs:0} мс [{report.Level.ToString().ToLowerInvariant()}]" +
                (slow is null ? string.Empty : $" · макс «{slow.Name}» {slow.Ms:0.0} мс") +
                (report.Issues.Count > 0 ? " · " + string.Join("; ", report.Issues) : string.Empty));
        }
        catch (Exception)
        {
            // Диагностика не должна ронять таймер статистики.
        }
    }

    /// <summary>База счётчиков потерь кабеля (стартовый прогрев — не «потеря»); флаг — т.к. ulong.</summary>
    private ulong _cableDropBaseline;
    private bool _cableDropBaselineSet;

    /// <summary>Поэтапный отчёт задержки (M-волна) — см. <see cref="LatencyReport"/>.</summary>
    public LatencyReport GetLatencyReport()
    {
        if (!_running)
        {
            return LatencyReport.Build([], ["движок не запущен"]);
        }

        var stages = new List<LatencyStage>();
        var issues = new List<string>();
        var serious = false;
        double rate;

        lock (_gate)
        {
            rate = Math.Max(1, _sampleRate * (double)_channels);

            foreach (var binding in _sources)
            {
                if (binding.Failed)
                {
                    continue;
                }

                stages.Add(new LatencyStage(
                    $"Захват «{binding.Name}» — клиент",
                    binding.Recorder.LatencyMilliseconds,
                    binding.Recorder.LowLatencyActive ? "low-latency (IAudioClient3)" : "обычный shared"));

                stages.Add(new LatencyStage(
                    $"Кольцо «{binding.Name}»",
                    binding.Ring.Available * 1000.0 / rate,
                    $"свежесть входа · xrun под/переп {binding.Ring.UnderrunSamples}/{binding.Ring.OverflowSamples}"));
            }
        }

        // Кабель — вне _gate: файловый ввод-вывод (read-only хэндл не замирает кабель).
        if (DriverFeed.TryReadStatsShared(out var feed))
        {
            stages.Add(new LatencyStage(
                "Кабель In→Out — уровень",
                feed.LevelBytes / 384.0,
                $"кольцо {feed.LevelBytes} Б · потеряно всего {feed.DroppedBytes + feed.CableDropped} Б"));

            // Серьёзна только НОВАЯ потеря (дельта к базе первого отчёта):
            // стартовый прогрев (окно до захвата Out1) — не «потеря звука».
            var totalLoss = feed.DroppedBytes + feed.CableDropped;
            if (!_cableDropBaselineSet)
            {
                _cableDropBaseline = totalLoss;
                _cableDropBaselineSet = true;
            }
            else if (totalLoss > _cableDropBaseline)
            {
                issues.Add($"кабель: потеряно {totalLoss - _cableDropBaseline} Б с прошлого отчёта");
                serious = true;
            }
        }

        lock (_gate)
        {
            var mix = _processor.GetMixerStats();
            stages.Add(new LatencyStage(
                "Микшер (граф)",
                mix.LastFrames * 1000.0 / Math.Max(1, _sampleRate),
                $"блок {mix.LastFrames} фр · CPU {mix.CpuUsPerBlock:0.0} мкс · {mix.BlocksPerSec:0} бл/с"));

            var namesMatch = _players.Count > 0 && _players.Count == _sinkNames.Length;
            for (var i = 0; i < _players.Count; i++)
            {
                var detail = _players[i].LowLatencyActive ? "low-latency (IAudioClient3)" : "обычный shared";
                stages.Add(new LatencyStage(
                    namesMatch ? $"Вывод «{_sinkNames[i]}» — клиент" : $"Вывод — клиент {i + 1}",
                    _players[i].LatencyMilliseconds,
                    detail));
            }
        }

        return LatencyReport.Build(stages, issues, serious);
    }

    private void LogStatsIfChanged()
    {
        if (!_running)
        {
            return;
        }

        LogLatencySummary();
        long underflow = 0;
        long overflow = 0;
        foreach (var source in _sources)
        {
            if (source.Failed)
            {
                continue;
            }

            underflow += source.Ring.UnderrunSamples;
            overflow += source.Ring.OverflowSamples;
        }

        // Счётчики ядро-фида (виртуальный вывод): дельта за период.
        if (_feed is not null)
        {
            try
            {
                if (_feed.TryGetStats(out var feedStats))
                {
                    var dropDelta = feedStats.DroppedBytes - _lastFeedDropped;
                    var quietDelta = feedStats.UnderrunBytes - _lastFeedUnderrun;
                    _lastFeedDropped = feedStats.DroppedBytes;
                    _lastFeedUnderrun = feedStats.UnderrunBytes;
                    if (dropDelta != 0 || quietDelta != 0)
                    {
                        LogMessage(
                            EngineLogLevel.Info,
                            $"фида дельта: сброс {dropDelta} Б (переполнение), тишина {quietDelta} Б (недостача); " +
                            $"всего сброс {feedStats.DroppedBytes} Б");
                    }
                }
            }
            catch
            {
                // Фид мог закрыться — статистика не критична.
            }
        }

        if (underflow == _lastLoggedUnderflow)
        {
            return;
        }

        var delta = underflow - _lastLoggedUnderflow;
        _lastLoggedUnderflow = underflow;
        LogMessage(
            EngineLogLevel.Info,
            $"xrun-дельта: под +{delta} сэмплов (всего {underflow}), переп {overflow}");
    }

    private void LogMessage(EngineLogLevel level, string message)
    {
        Trace.WriteLine($"[Parrhesia.Audio][{level}] {message}");
        Log?.Invoke(this, new EngineLogEntry(DateTime.Now, level, message));
    }

    private static bool IsSameFormat(WaveFormat actual, WaveFormat expected)
    {
        if (actual.SampleRate != expected.SampleRate || actual.Channels != expected.Channels || actual.BitsPerSample != 32)
        {
            return false;
        }

        return actual.Encoding switch
        {
            WaveFormatEncoding.IeeeFloat => true,
            WaveFormatEncoding.Extensible => actual is WaveFormatExtensible ext && ext.SubFormat == IeeeFloatSubFormat,
            _ => false,
        };
    }

    /// <summary>Формат записывается как 32-бит float — иначе байты не разобрать как сэмплы.</summary>
    private static bool IsFloat32(WaveFormat format)
    {
        if (format.BitsPerSample != 32)
        {
            return false;
        }

        return format.Encoding switch
        {
            WaveFormatEncoding.IeeeFloat => true,
            WaveFormatEncoding.Extensible => format is WaveFormatExtensible ext && ext.SubFormat == IeeeFloatSubFormat,
            _ => false,
        };
    }

    private static int NextPowerOfTwo(int value)
    {
        var result = 1;
        while (result < value)
        {
            result <<= 1;
        }

        return Math.Max(result, 4096);
    }

    /// <summary>План назначения: узел-сник и разобранная привязка устройства.</summary>
    private sealed record SinkPlan(Guid NodeId, string Name, DeviceSpec Spec);

    /// <summary>
    /// Успешно открытое назначение. У виртуального нет устройства и игрока —
    /// там фид (<see cref="_feed"/>) и помпа, создаваемые на шаге сборки.
    /// </summary>
    private sealed record OpenedSink(SinkPlan Plan, MMDevice? Device, WasapiPlayer? Player);

    /// <summary>Привязка источника: поток записи, кольцо в формате движка и адаптер формата.</summary>
    private sealed class SourceBinding(
        Guid nodeId,
        string name,
        WasapiRecorder recorder,
        SampleRing ring,
        MMDevice device,
        SourceFormatAdapter? adapter,
        bool loopback)
    {
        public Guid NodeId { get; } = nodeId;

        public string Name { get; } = name;

        public SampleRing Ring { get; } = ring;

        public MMDevice Device { get; } = device;

        public bool Loopback { get; } = loopback;

        /// <summary>Текущий поток записи (меняется при фолбэке на формат устройства).</summary>
        public WasapiRecorder Recorder { get; set; } = recorder;

        /// <summary>Адаптер формата источника; null — данные уже в формате движка.</summary>
        public SourceFormatAdapter? Adapter { get; set; } = adapter;

        /// <summary>Поток умер после старта: источник неактивен, его статистику не считаем.</summary>
        public volatile bool Failed;

        /// <summary>Монотонные тики старта записи — для замера задержки первых данных.</summary>
        public long StartTimestamp;

        public bool FirstDataLogged;

        /// <summary>Нули в формате источника для тишинных пакетов (адаптерный путь).</summary>
        public float[] SourceScratch = [];

        /// <summary>Буфер вывода адаптера — уже в формате движка.</summary>
        public float[] OutputScratch = [];
    }
}
