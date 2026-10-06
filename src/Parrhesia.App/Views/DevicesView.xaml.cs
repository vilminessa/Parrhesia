using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Parrhesia.Audio.Devices;
using Parrhesia.Audio.Engine;

namespace Parrhesia.App.Views;

/// <summary>
/// Вкладка «Устройства»: списки endpoint'ов, статус движка (опрос раз в секунду)
/// и лента его лога.
/// </summary>
public partial class DevicesView : UserControl
{
    private const int MaxLogLines = 8;

    private readonly List<string> _log = [];
    private readonly DispatcherTimer _statusTimer;

    public DevicesView()
    {
        InitializeComponent();

        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _statusTimer.Tick += (_, _) => RefreshStatus();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        AppServices.Devices.DevicesChanged += OnDevicesChanged;
        AppServices.Engine.Log += OnEngineLog;
        AppServices.Engine.StatusChanged += OnStatusChanged;
        _statusTimer.Start();

        RefreshDevices();
        RefreshStatus();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        AppServices.Devices.DevicesChanged -= OnDevicesChanged;
        AppServices.Engine.Log -= OnEngineLog;
        AppServices.Engine.StatusChanged -= OnStatusChanged;
        _statusTimer.Stop();
    }

    private void OnRestartClick(object sender, RoutedEventArgs e)
    {
        AppServices.Engine.Stop();
        AppServices.Engine.Start();
        RefreshStatus();
    }

    private void OnDevicesChanged(object? sender, EventArgs e) =>
        Dispatcher.InvokeAsync(RefreshDevices);

    private void OnStatusChanged(object? sender, EventArgs e) =>
        Dispatcher.InvokeAsync(RefreshStatus);

    private void OnEngineLog(object? sender, EngineLogEntry entry) =>
        Dispatcher.InvokeAsync(() => AppendLog(entry));

    private void RefreshDevices()
    {
        RenderList.ItemsSource = AppServices.Devices.GetDevices(DeviceFlow.Render);
        CaptureList.ItemsSource = AppServices.Devices.GetDevices(DeviceFlow.Capture);
    }

    private void RefreshStatus()
    {
        var status = AppServices.Engine.Status;
        StatusText.Text = status.IsRunning
            ? $"Движок: работает · {status.SampleRate} Гц · {status.Channels} к · " +
              $"выход «{status.SinkName}» · источников {status.ActiveSources} · " +
              $"xrun под/переп. {status.UnderrunSamples}/{status.OverflowSamples}"
            : "Движок: остановлен";
    }

    private void AppendLog(EngineLogEntry entry)
    {
        _log.Add($"[{entry.Timestamp:HH:mm:ss}] {entry.Level}: {entry.Message}");
        while (_log.Count > MaxLogLines)
        {
            _log.RemoveAt(0);
        }

        LogText.Text = _log.Count == 0
            ? "Лог пуст"
            : string.Join(Environment.NewLine, _log);
    }
}
