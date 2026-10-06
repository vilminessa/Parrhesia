using System.Diagnostics;
using System.Windows.Threading;
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

    public static void Initialize()
    {
        Devices = new WasapiDeviceService();
        Graph = new AudioGraph();
        Profiles = new ProfileService(Graph);

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
            Profiles.SaveActive();
        };
        Graph.Changed += OnGraphChanged;
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
        _autosaveTimer?.Stop();
        Profiles?.SaveActive();
        Engine?.Dispose();
        Devices?.Dispose();
    }

    private static void OnGraphChanged(object? sender, GraphChange e)
    {
        _autosaveTimer?.Stop();
        _autosaveTimer?.Start();
    }

    /// <summary>
    /// Стартовая схема первого запуска: микрофон → основная шина → наушники.
    /// Остальные источники без привязок — виртуальные endpoint'ы появятся в Ф4.
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

        graph.AddRoute(mic.Id, main.Id, out _);
        graph.AddRoute(capture.Id, main.Id, out _);
        graph.AddRoute(browser.Id, main.Id, out _);
        graph.AddRoute(browser.Id, stream.Id, out _);
        graph.AddRoute(main.Id, headphones.Id, out _);
        graph.AddRoute(main.Id, stream.Id, out _);
        graph.AddRoute(stream.Id, discord.Id, out _);
    }
}
