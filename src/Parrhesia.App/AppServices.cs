using System.Diagnostics;
using System.Windows.Threading;
using Parrhesia.App.Settings;
using Parrhesia.Audio.Devices;
using Parrhesia.Audio.Engine;
using Parrhesia.Core.Graph;
using Parrhesia.Core.Profiles;

namespace Parrhesia.App;

/// <summary>
/// Корневые сервисы приложения: создаются один раз в OnStartup,
/// живут до выхода. Граф маршрутизации общий для движка и интерфейса —
/// это состояние активного профиля.
/// </summary>
public static class AppServices
{
    private static DispatcherTimer? _autosaveTimer;

    public static AudioGraph Graph { get; private set; } = null!;

    public static IDeviceService Devices { get; private set; } = null!;

    public static IAudioEngine Engine { get; private set; } = null!;

    public static ProfileService Profiles { get; private set; } = null!;

    public static AppSettings Settings { get; private set; } = null!;

    public static void Initialize()
    {
        Devices = new WasapiDeviceService();
        Graph = new AudioGraph();
        Profiles = new ProfileService(Graph);
        Settings = AppSettings.Load();

        // Ф4: восстановление имён кабелей подписывается ДО движка — порядок
        // обработчиков DevicesChanged = порядок подписки: сначала имена
        // возвращаются, потом движок стартует/рестартует и резолвит привязки.
        Devices.DevicesChanged += OnDevicesChanged;

        if (Profiles.Count == 0)
        {
            // Первый запуск: стартовая схема становится профилем «Основной».
            BuildSessionGraph(Graph);
            Profiles.CreateFromLive("Основной");
        }

        if (!Profiles.ActivateInitial(out var error))
        {
            Trace.WriteLine("[Parrhesia] Профили: " + error);
        }

        Engine = new WasapiAudioEngine(Graph, Devices);

        // Дебаунс-автосейв: правки активного профиля пишутся на диск
        // через 1.5 с покоя (позиции при перетаскивании не бомбят записью).
        _autosaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
        _autosaveTimer.Tick += (_, _) =>
        {
            _autosaveTimer.Stop();
            SaveActiveProfile();
        };
        Graph.Changed += OnGraphChanged;

        // Ф4: стартовые узлы без привязки → кабели (однократно, пока драйвер
        // виден; изменения уйдут в автосейв через тик выше).
        AutoBindStartNodes();
    }

    /// <summary>Снимок state плагинов в модель + запись активного профиля.</summary>
    private static void SaveActiveProfile()
    {
        Engine?.CollectPluginStates();
        Profiles.SaveActive();
    }

    public static void StartEngine()
    {
        try
        {
            Engine.Start();
        }
        catch (Exception ex)
        {
            Trace.WriteLine("[Parrhesia] Не удалось запустить движок: " + ex);
        }
    }

    public static void Shutdown()
    {
        Graph.Changed -= OnGraphChanged;
        Devices.DevicesChanged -= OnDevicesChanged;
        _autosaveTimer?.Stop();
        SaveActiveProfile();
        Engine?.Dispose();
        Devices?.Dispose();
    }

    private static void OnGraphChanged(object? sender, GraphChange e)
    {
        _autosaveTimer?.Stop();
        _autosaveTimer?.Start();
    }

    /// <summary>Ф4: устройства сменились (в т.ч. rebind драйвера) — вернуть имена кабелей.</summary>
    private static void OnDevicesChanged(object? sender, EventArgs e)
    {
        try
        {
            CableNameRestorer.Restore(Settings);
        }
        catch (Exception ex)
        {
            Trace.WriteLine("[Parrhesia] Восстановление имён кабелей: " + ex.Message);
        }
    }

    /// <summary>Свои endpoint'ы (кабели) для авто-биндинга; пусто — драйвера нет.</summary>
    private static List<LaneEndpoint> GetLanes()
    {
        try
        {
            return LaneCatalog.Get(
                Devices.GetDevices(DeviceFlow.Capture),
                Devices.GetDevices(DeviceFlow.Render));
        }
        catch
        {
            return [];
        }
    }

    /// <summary>
    /// Ф4: существующие стартовые узлы БЕЗ привязки → кабели (однократно при
    /// старте); контракт и защита от повтора — в LaneAutoBind.BindUnbound.
    /// </summary>
    private static void AutoBindStartNodes()
    {
        var bound = LaneAutoBind.BindUnbound(Graph, GetLanes());
        if (bound > 0)
        {
            Trace.WriteLine($"[Parrhesia] Ф4: стартовые узлы привязаны к кабелям ({bound})");
        }
    }

    /// <summary>
    /// Стартовая схема первого запуска: микрофон → основная шина → наушники.
    /// Ф4: «Браузер» и «Discord (вирт.)» сразу привязываются к кабелям,
    /// если драйвер установлен (без драйвера остаются без привязки).
    /// </summary>
    private static void BuildSessionGraph(AudioGraph graph)
    {
        var mic = graph.AddNode("Микрофон", NodeKind.Source);
        var capture = graph.AddNode("Захват устройств", NodeKind.Source);
        var browser = graph.AddNode("Браузер", NodeKind.Source);
        var main = graph.AddNode("Основная шина", NodeKind.Bus);
        var stream = graph.AddNode("Шина стрима", NodeKind.Bus);
        var headphones = graph.AddNode("Наушники", NodeKind.Sink);
        var discord = graph.AddNode("Discord (вирт.)", NodeKind.Sink);

        graph.SetNodeDevice(mic.Id, DeviceSpec.DefaultCapture);
        graph.SetNodeDevice(headphones.Id, DeviceSpec.DefaultRender);

        // Ф4: стартовые узлы сразу на кабелях (драйвер есть → «вход приложений»
        // для браузера и виртуальный микрофон для Discord).
        var lanes = GetLanes();
        var browserDevice = LaneAutoBind.SourceDeviceId(lanes);
        if (browserDevice is not null)
        {
            graph.SetNodeDevice(browser.Id, browserDevice);
        }

        var discordDevice = LaneAutoBind.SinkDeviceId(lanes);
        if (discordDevice is not null)
        {
            graph.SetNodeDevice(discord.Id, discordDevice);
        }

        graph.AddRoute(mic.Id, main.Id, out _);
        graph.AddRoute(capture.Id, main.Id, out _);
        graph.AddRoute(browser.Id, main.Id, out _);
        graph.AddRoute(browser.Id, stream.Id, out _);
        graph.AddRoute(main.Id, headphones.Id, out _);
        graph.AddRoute(main.Id, stream.Id, out _);
        graph.AddRoute(stream.Id, discord.Id, out _);
    }
}
