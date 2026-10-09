using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Parrhesia.App.Views.Mixer;
using Parrhesia.Core.Graph;

namespace Parrhesia.App.Views.Effects;

/// <summary>
/// Окно настроек эффектора пульта (ПКМ по чипу в продвинутом режиме).
/// K-волна: оболочка — заголовок, зеркало включения, подсказка параметров.
/// Содержимое редактора (ползунки/кривые) заполняет волна эффектов.
/// Одно окно на (пульт, эффектор); повторный ПКМ активирует уже открытое.
/// </summary>
internal sealed class EffectSettingsWindow : Window
{
    private static readonly Dictionary<(Guid Node, string Fx), EffectSettingsWindow> OpenWindows = [];

    private readonly Guid _nodeId;
    private readonly string _fxId;
    private readonly CheckBox _enabled;
    private bool _syncing;

    /// <summary>Открыть (или активировать) окно настроек эффектора пульта.</summary>
    public static void Show(Window? owner, AudioNode node, string fxId)
    {
        var key = (node.Id, fxId);
        if (OpenWindows.TryGetValue(key, out var existing))
        {
            existing.SyncFromGraph();
            existing.Show();
            existing.Activate();
            return;
        }

        var window = new EffectSettingsWindow(node, fxId);
        if (owner is { IsLoaded: true })
        {
            window.Owner = owner;
        }

        OpenWindows[key] = window;
        window.Closed += (_, _) => OpenWindows.Remove(key);
        window.Show();
        window.Activate();
    }

    private EffectSettingsWindow(AudioNode node, string fxId)
    {
        _nodeId = node.Id;
        _fxId = fxId;

        Title = $"{FxInfo.Title(fxId)} — {node.Name}";
        Width = 460;
        Height = 300;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ResizeMode = ResizeMode.CanResizeWithGrip;
        Background = BrushOf("Brush.Elevated", "#FF1B1F26");
        Foreground = BrushOf("Brush.Text", "#FFE6E9EF");

        _enabled = new CheckBox
        {
            Content = "Включено",
            IsChecked = IsFxOn(),
            Margin = new Thickness(0, 0, 0, 12),
        };
        _enabled.Checked += (_, _) => ApplyEnabled(true);
        _enabled.Unchecked += (_, _) => ApplyEnabled(false);

        var title = new TextBlock
        {
            Text = FxInfo.Title(fxId),
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Foreground = BrushOf("Brush.Text", "#FFE6E9EF"),
            Margin = new Thickness(0, 0, 0, 4),
        };

        var strip = new TextBlock
        {
            Text = $"Пульт «{node.Name}»",
            FontSize = 11,
            Foreground = BrushOf("Brush.TextFaint", "#FF5C6472"),
            Margin = new Thickness(0, 0, 0, 14),
        };

        var placeholder = new Border
        {
            Background = BrushOf("Brush.Panel", "#FF14171C"),
            BorderBrush = BrushOf("Brush.Stroke", "#FF262B33"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14, 12, 14, 12),
            Child = new StackPanel
            {
                Children =
                {
                    new TextBlock
                    {
                        Text = "Редактор эффектора",
                        FontSize = 12,
                        FontWeight = FontWeights.SemiBold,
                        Foreground = BrushOf("Brush.Text", "#FFE6E9EF"),
                        Margin = new Thickness(0, 0, 0, 6),
                    },
                    new TextBlock
                    {
                        Text = FxInfo.ParamsHint(fxId),
                        FontSize = 12,
                        Foreground = BrushOf("Brush.TextDim", "#FF9AA3B2"),
                        TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(0, 0, 0, 10),
                    },
                    new TextBlock
                    {
                        Text = "Появится в волне эффектов — этот фреймворк уже держит состояние и событие включения.",
                        FontSize = 11,
                        Foreground = BrushOf("Brush.TextFaint", "#FF5C6472"),
                        TextWrapping = TextWrapping.Wrap,
                    },
                },
            },
        };

        Content = new StackPanel
        {
            Margin = new Thickness(18),
            Children = { title, strip, _enabled, placeholder },
        };

        SyncFromGraph();
    }

    private bool IsFxOn()
    {
        var node = AppServices.Graph.FindNode(_nodeId);
        return node is not null
            && node.FxEnabled.TryGetValue(_fxId, out var on)
            && on;
    }

    private void ApplyEnabled(bool enabled)
    {
        if (_syncing)
        {
            return;
        }

        // Источник истины — граф: NodeChanged обновит чип на пульте.
        AppServices.Graph.SetNodeFx(_nodeId, _fxId, enabled);
    }

    /// <summary>Синхронизация зеркала «Включено» (например, при повторном ПКМ).</summary>
    private void SyncFromGraph()
    {
        _syncing = true;
        try
        {
            _enabled.IsChecked = IsFxOn();
        }
        finally
        {
            _syncing = false;
        }
    }

    private static Brush BrushOf(string key, string fallback) =>
        Application.Current?.TryFindResource(key) as Brush ?? new SolidColorBrush(Color.FromRgb(
            Convert.ToByte(fallback.Substring(1, 2), 16),
            Convert.ToByte(fallback.Substring(3, 2), 16),
            Convert.ToByte(fallback.Substring(5, 2), 16)));
}
