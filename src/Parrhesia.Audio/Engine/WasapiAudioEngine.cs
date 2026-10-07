using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Parrhesia.Audio.Buffers;
using Parrhesia.Audio.Devices;
using Parrhesia.Audio.Processing;
using Parrhesia.Core.Graph;

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
    private const int OutputLatencyMs = 50;
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

    private WasapiPlayer? _player;
    private DriverFeed? _feed;
    private VirtualSinkPump? _pump;
    private MMDevice? _sinkDevice;
    private Guid _sinkId;
    private string? _sinkName;
    private int _sampleRate;
    private int _channels;
    private bool _running;
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
        try
        {
            var sink = _graph.Nodes.FirstOrDefault(n => n.Kind == NodeKind.Sink && n.DeviceId is not null);
            if (sink is null)
            {
                LogMessage(EngineLogLevel.Warning, "Нет назначения с привязанным устройством — движок не запущен");
                return;
            }

            if (!DeviceSpec.TryParse(sink.DeviceId, out var sinkSpec) || sinkSpec.Loopback)
            {
                LogMessage(EngineLogLevel.Warning, $"Недопустимая привязка назначения «{sink.DeviceId}»");
                return;
            }

            WaveFormat engineFormat;
            Action startStreaming;
            MMDevice? sinkDevice = null;

            if (sinkSpec.Target == DeviceSpecTarget.Virtual)
            {
                // Виртуальный вывод: граф гонит блоки в драйвер \\.\ParrhesiaFeed.
                engineFormat = WaveFormat.CreateIeeeFloatWaveFormat(DriverFeed.Rate, DriverFeed.Channels);
                var feed = new DriverFeed();
                feed.Open(); // бросает исключение, если драйвер не установлен
                var provider = new GraphWaveProvider(_processor, sink.Id, engineFormat);
                var pump = new VirtualSinkPump(provider, feed);
                _feed = feed;
                _pump = pump;
                startStreaming = pump.Start;
                _sinkName = "Parrhesia Out (виртуальный)";
            }
            else
            {
                if (!TryResolveDevice(sinkSpec, DataFlow.Render, out sinkDevice))
                {
                    return;
                }

                var player = new WasapiPlayerBuilder()
                    .WithDevice(sinkDevice)
                    .WithSharedMode()
                    .WithEventSync()
                    .WithLatency(OutputLatencyMs)
                    .Build();

                var mix = player.DeviceMixFormat;
                engineFormat = WaveFormat.CreateIeeeFloatWaveFormat(mix.SampleRate, mix.Channels);
                var provider = new GraphWaveProvider(_processor, sink.Id, engineFormat);
                player.Init(provider);

                _player = player;
                _sinkDevice = sinkDevice;
                _sinkName = sinkDevice.FriendlyName;
                startStreaming = player.Play;
            }

            _sinkId = sink.Id;
            _sampleRate = engineFormat.SampleRate;
            _channels = engineFormat.Channels;

            // Цепочки слотов: загрузка/подготовка плагинов под формат движка
            // (max-блок100 мс — больше обоих путей вывода: player50 мс, помпа10 мс).
            _slotChains.Prepare(engineFormat.SampleRate, engineFormat.SampleRate / 10, engineFormat.Channels);

            OpenSources(engineFormat, sinkDevice);

            startStreaming();
            foreach (var source in _sources)
            {
                source.StartTimestamp = Stopwatch.GetTimestamp();
                source.FirstDataLogged = false;
                source.Recorder.StartRecording();
            }

            _running = true;
            _bindings = CaptureBindings();
            _lastLoggedUnderflow = 0;
            _lastFeedDropped = 0;
            _lastFeedUnderrun = 0;
            _statsTimer = new Timer(_ => LogStatsIfChanged(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
            LogMessage(
                EngineLogLevel.Info,
                $"Движок запущен: {_sampleRate} Гц, выход «{_sinkName}», источников {_sources.Count}");
            StatusChanged?.Invoke(this, EventArgs.Empty);
            _ = ResetStatsLater();
        }
        catch (Exception ex)
        {
            LogMessage(EngineLogLevel.Error, "Ошибка запуска движка: " + ex.Message);
            Cleanup();
        }
    }

    private void OpenSources(WaveFormat engineFormat, MMDevice? sinkDevice)
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

            if (!TryResolveDevice(spec, spec.Loopback ? DataFlow.Render : DataFlow.Capture, out var device))
            {
                continue;
            }

            if (sinkDevice is not null && spec.Loopback && device.ID == sinkDevice.ID)
            {
                LogMessage(
                    EngineLogLevel.Warning,
                    $"Источник «{node.Name}» захватывает то же устройство, куда идёт вывод, — это петля; источник пропущен");
                device.Dispose();
                continue;
            }

            var builder = new WasapiRecorderBuilder()
                .WithDevice(device)
                .WithEventSync()
                .WithFormat(WaveFormat.CreateIeeeFloatWaveFormat(engineFormat.SampleRate, engineFormat.Channels));
            if (spec.Loopback)
            {
                builder = builder.WithLoopbackCapture();
            }

            var recorder = builder.Build();
            if (!IsSameFormat(recorder.WaveFormat, engineFormat))
            {
                LogMessage(
                    EngineLogLevel.Error,
                    $"Источник «{node.Name}»: формат {recorder.WaveFormat} ≠ {engineFormat}; источник пропущен");
                recorder.Dispose();
                device.Dispose();
                continue;
            }

            var capacity = NextPowerOfTwo((int)(engineFormat.SampleRate * engineFormat.Channels * RingSeconds));
            var binding = new SourceBinding(node.Id, node.Name, recorder, new SampleRing(capacity), device);
            recorder.DataAvailable += (data, flags, _, _) => OnCaptureData(binding, data, flags);
            _processor.SetInput(node.Id, binding.Ring);
            _sources.Add(binding);

            LogMessage(
                EngineLogLevel.Info,
                $"Источник «{node.Name}»: {recorder.WaveFormat.SampleRate} Гц, {recorder.WaveFormat.Channels} к " +
                $"({(spec.Loopback ? "loopback" : "захват")}, {node.DeviceId})");
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
                $"«{binding.Name}»: первые данные через {delayMs:0} мс");
        }

        if (flags.HasFlag(AudioClientBufferFlags.Silent))
        {
            var samples = data.Length / sizeof(float);
            Span<float> zeros = samples <= 16384 ? stackalloc float[samples] : new float[samples];
            binding.Ring.Write(zeros);
            return;
        }

        binding.Ring.Write(MemoryMarshal.Cast<byte, float>(data));
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

        try
        {
            _player?.Stop();
        }
        catch
        {
            // Устройство могло исчезнуть.
        }

        try
        {
            _pump?.Stop();
        }
        catch
        {
            // Фид мог закрыться — не мешаем остальной очистке.
        }

        _processor.ClearInputs();

        foreach (var source in _sources)
        {
            source.Recorder.Dispose();
            source.Device.Dispose();
        }

        _sources.Clear();
        _statsTimer?.Dispose();
        _statsTimer = null;
        _player?.Dispose();
        _player = null;
        _pump?.Dispose();
        _pump = null;
        _feed?.Dispose();
        _feed = null;
        _sinkDevice?.Dispose();
        _sinkDevice = null;
        _sinkId = Guid.Empty;
        _sinkName = null;
        _sampleRate = 0;
        _channels = 0;
        _running = false;
        _bindings = [];
    }

    private bool TryResolveDevice(DeviceSpec spec, DataFlow expectedFlow, out MMDevice device)
    {
        device = null!;
        try
        {
            if (spec.Target == DeviceSpecTarget.ById)
            {
                var found = _enumerator.GetDevice(spec.DeviceId);
                if (found.DataFlow != expectedFlow)
                {
                    LogMessage(
                        EngineLogLevel.Error,
                        $"Устройство {spec} — не тот поток данных ({found.DataFlow}, ожидался {expectedFlow})");
                    found.Dispose();
                    return false;
                }

                device = found;
                return true;
            }

            var flow = spec.Target == DeviceSpecTarget.DefaultCapture ? DataFlow.Capture : DataFlow.Render;
            return _enumerator.TryGetDefaultAudioEndpoint(flow, Role.Multimedia, out device!);
        }
        catch (Exception ex)
        {
            LogMessage(EngineLogLevel.Error, $"Устройство {spec} недоступно: {ex.Message}");
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
        lock (_gate)
        {
            if (!_running)
            {
                return;
            }

            LogMessage(EngineLogLevel.Info, $"Перезапуск движка ({reason})");
            Cleanup();
            StartCore();
        }
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
        foreach (var source in _sources)
        {
            underruns += source.Ring.UnderrunSamples;
            overflows += source.Ring.OverflowSamples;
        }

        return new EngineStatus(true, _sampleRate, _channels, _sinkName, _sources.Count, underruns, overflows);
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
    private void LogStatsIfChanged()
    {
        if (!_running)
        {
            return;
        }

        long underflow = 0;
        long overflow = 0;
        foreach (var source in _sources)
        {
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

    private static int NextPowerOfTwo(int value)
    {
        var result = 1;
        while (result < value)
        {
            result <<= 1;
        }

        return Math.Max(result, 4096);
    }

    private sealed record SourceBinding(
        Guid NodeId,
        string Name,
        WasapiRecorder Recorder,
        SampleRing Ring,
        MMDevice Device)
    {
        /// <summary>Монотонные тики старта записи — для замера задержки первых данных.</summary>
        public long StartTimestamp;

        public bool FirstDataLogged;
    }
}
