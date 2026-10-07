using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Parrhesia.App.Controls;
using Parrhesia.App.Rendering;
using Parrhesia.App.Views.Mixer;
using Parrhesia.Audio.Devices;
using Parrhesia.Audio.Engine;
using Parrhesia.Core.Graph;

namespace Parrhesia.App.Views;

/// <summary>
/// Вкладка «Микшер»: полосы каналов (источники → шины → назначения)
/// с фейдерами, метрами и кнопками M/S/B. Полосы пересоздаются только
/// при структурных изменениях графа; правки параметров обновляются на месте.
/// </summary>
public partial class MixerView : UserControl
{
    private readonly Dictionary<Guid, MixerStrip> _strips = [];
    private double _statusAccum;

    /// <summary>Блокировка обработчиков на время программного наполнения комбобоксов.</summary>
    private bool _syncingUi;

    public MixerView()
    {
        InitializeComponent();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        AppServices.Graph.Changed += OnGraphChanged;
        AppServices.Devices.DevicesChanged += OnDevicesChanged;
        RenderTicker.Subscribe(OnTick);
        RefreshRateList();
        RefreshMonitorList();
        PushSettingsToEngine();
        RebuildStrips();
        RefreshStatus();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        AppServices.Graph.Changed -= OnGraphChanged;
        AppServices.Devices.DevicesChanged -= OnDevicesChanged;
        RenderTicker.Unsubscribe(OnTick);
    }

    /// <summary>Настройки из AppSettings → движок (идемпотентно: движок не трогает то, что уже так настроено).</summary>
    private static void PushSettingsToEngine()
    {
        AppServices.Engine.SetSampleRate(EngineFormat.ParseSetting(AppServices.Settings.EngineSampleRate));
        AppServices.Engine.SetMonitorDevice(AppServices.Settings.MonitorDeviceId);
    }

    private void RefreshRateList()
    {
        _syncingUi = true;
        try
        {
            RateCombo.Items.Clear();
            RateCombo.Items.Add(new ComboBoxItem { Content = "Авто", Tag = null });
            foreach (var rate in EngineFormat.Rates)
            {
                RateCombo.Items.Add(new ComboBoxItem { Content = FormatRate(rate), Tag = rate });
            }

            var configured = EngineFormat.ParseSetting(AppServices.Settings.EngineSampleRate);
            SelectByTag(RateCombo, configured);
        }
        finally
        {
            _syncingUi = false;
        }
    }

    private void RefreshMonitorList()
    {
        _syncingUi = true;
        try
        {
            var selected = AppServices.Settings.MonitorDeviceId;
            MonitorCombo.Items.Clear();
            MonitorCombo.Items.Add(new ComboBoxItem { Content = "Выкл.", Tag = null });
            foreach (var device in AppServices.Devices.GetDevices(DeviceFlow.Render))
            {
                MonitorCombo.Items.Add(new ComboBoxItem { Content = device.Name, Tag = device.Id });
            }

            if (SelectByTag(MonitorCombo, selected) < 0)
            {
                // Устройство исчезло — оставляем «Выкл.», настройку перезапишет пользователь.
                MonitorCombo.SelectedIndex = 0;
            }
        }
        finally
        {
            _syncingUi = false;
        }
    }

    /// <returns>Индекс найденного элемента или −1.</returns>
    private static int SelectByTag(ComboBox combo, object? tag)
    {
        for (var i = 0; i < combo.Items.Count; i++)
        {
            if (combo.Items[i] is ComboBoxItem item && Equals(item.Tag, tag))
            {
                combo.SelectedIndex = i;
                return i;
            }
        }

        return -1;
    }

    private static string FormatRate(int rate) =>
        rate % 1000 == 0 ? $"{rate / 1000} кГц" : $"{rate / 1000.0:0.#} кГц";

    private void OnRateChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingUi)
        {
            return;
        }

        var tag = (RateCombo.SelectedItem as ComboBoxItem)?.Tag;
        var rate = tag is int value ? (int?)value : null;
        AppServices.Settings.EngineSampleRate = rate?.ToString(CultureInfo.InvariantCulture) ?? "auto";
        AppServices.Settings.Save();
        AppServices.Engine.SetSampleRate(rate);
    }

    private void OnMonitorChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingUi)
        {
            return;
        }

        var deviceId = (MonitorCombo.SelectedItem as ComboBoxItem)?.Tag as string;
        AppServices.Settings.MonitorDeviceId = deviceId ?? string.Empty;
        AppServices.Settings.Save();
        AppServices.Engine.SetMonitorDevice(deviceId);
    }

    private void OnDevicesChanged(object? sender, EventArgs e) =>
        Dispatcher.InvokeAsync(() => RefreshMonitorList());

    private void OnGraphChanged(object? sender, GraphChange e)
    {
        switch (e.Kind)
        {
            case GraphChangeKind.NodeAdded:
            case GraphChangeKind.NodeRemoved:
            case GraphChangeKind.Reset:
                RebuildStrips();
                break;

            case GraphChangeKind.NodeChanged:
                foreach (var strip in _strips.Values)
                {
                    strip.RefreshFromNode();
                }

                break;
        }
    }

    private void RebuildStrips()
    {
        StripsHost.Children.Clear();
        _strips.Clear();

        foreach (var node in OrderedNodes())
        {
            var strip = new MixerStrip(node);
            strip.MuteToggled += s => AppServices.Graph.SetNodeMute(s.Node.Id, !s.Node.Mute);
            strip.SoloToggled += s => AppServices.Graph.SetNodeSolo(s.Node.Id, !s.Node.Solo);
            strip.BypassToggled += s => AppServices.Graph.SetNodeBypass(s.Node.Id, !s.Node.Bypassed);
            strip.GainChanged += (s, gain) => AppServices.Graph.SetNodeGain(s.Node.Id, gain);
            strip.RenameRequested += OnRenameRequested;
            strip.DeleteRequested += OnDeleteRequested;

            _strips[node.Id] = strip;
            StripsHost.Children.Add(strip);
        }
    }

    private static IEnumerable<AudioNode> OrderedNodes() =>
        AppServices.Graph.Nodes.OrderBy(n => n.Kind switch
        {
            NodeKind.Source => 0,
            NodeKind.Bus => 1,
            _ => 2,
        });

    private void OnRenameRequested(MixerStrip strip)
    {
        var owner = Window.GetWindow(this);
        if (PromptDialog.Show(owner, "Переименовать", strip.Node.Name, out var name))
        {
            if (!string.Equals(name, strip.Node.Name, StringComparison.Ordinal))
            {
                AppServices.Graph.RenameNode(strip.Node.Id, name);
            }
        }
    }

    private void OnDeleteRequested(MixerStrip strip)
    {
        var answer = MessageBox.Show(
            $"Удалить «{strip.Node.Name}» со всеми связями?",
            "Микшер",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (answer == MessageBoxResult.Yes)
        {
            AppServices.Graph.RemoveNode(strip.Node.Id);
        }
    }

    private void OnTick(double now, double dt)
    {
        foreach (var strip in _strips.Values)
        {
            var peak = LedMeterControl.NormalizePeak(AppServices.Engine.GetPeak(strip.Node.Id));
            strip.UpdateMeter(peak, now, dt);
        }

        _statusAccum += dt;
        if (_statusAccum >= 1.0)
        {
            _statusAccum = 0;
            RefreshStatus();
        }
    }

    private void RefreshStatus()
    {
        var status = AppServices.Engine.Status;
        if (!status.IsRunning)
        {
            StatusText.Text = "Движок: остановлен";
            return;
        }

        var outputs = status.SinkNames.Count > 0
            ? string.Join(" + ", status.SinkNames)
            : status.SinkName;

        var monitor = status.MonitorActive
            ? $" · монитор «{status.MonitorName}»"
            : string.IsNullOrEmpty(AppServices.Settings.MonitorDeviceId)
                ? string.Empty
                : " · монитор выкл.";

        StatusText.Text = $"Движок: работает · {status.SampleRate} Гц · {status.Channels} к · " +
                          $"выходы «{outputs}» · xrun под/переп. {status.UnderrunSamples}/{status.OverflowSamples}" +
                          monitor;
    }
}
