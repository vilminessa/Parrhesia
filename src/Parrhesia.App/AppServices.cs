using System.Diagnostics;
using Parrhesia.Audio.Devices;
using Parrhesia.Audio.Engine;
using Parrhesia.Core.Graph;

namespace Parrhesia.App;

/// <summary>
/// Корневые сервисы приложения: создаются один раз в OnStartup,
/// живут до выхода. Граф маршрутизации общий для движка и интерфейса.
/// </summary>
public static class AppServices
{
    public static AudioGraph Graph { get; private set; } = null!;

    public static IDeviceService Devices { get; private set; } = null!;

    public static IAudioEngine Engine { get; private set; } = null!;

    public static void Initialize()
    {
        Devices = new WasapiDeviceService();
        Graph = CreateSessionGraph();
        Engine = new WasapiAudioEngine(Graph, Devices);
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
        Engine?.Dispose();
        Devices?.Dispose();
    }

    /// <summary>
    /// Стартовая схема: микрофон → основная шина → наушники (устройство по умолчанию).
    /// Остальные источники без привязок — их можно подключить, когда появятся
    /// устройства/виртуальные endpoint'ы (Ф4).
    /// </summary>
    private static AudioGraph CreateSessionGraph()
    {
        var graph = new AudioGraph();

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

        return graph;
    }
}
