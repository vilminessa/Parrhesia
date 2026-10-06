using System.Windows;
using System.Windows.Controls;
using Parrhesia.App.Controls;
using Parrhesia.App.Rendering;
using Parrhesia.App.Views.Mixer;
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

    public MixerView()
    {
        InitializeComponent();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        AppServices.Graph.Changed += OnGraphChanged;
        RenderTicker.Subscribe(OnTick);
        RebuildStrips();
        RefreshStatus();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        AppServices.Graph.Changed -= OnGraphChanged;
        RenderTicker.Unsubscribe(OnTick);
    }

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
        StatusText.Text = status.IsRunning
            ? $"Движок: работает · {status.SampleRate} Гц · {status.Channels} к · " +
              $"выход «{status.SinkName}» · xrun под/переп. {status.UnderrunSamples}/{status.OverflowSamples}"
            : "Движок: остановлен";
    }
}
