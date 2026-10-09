using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Parrhesia.App.Controls;
using Parrhesia.Audio.Devices;
using Parrhesia.Core.Graph;

namespace Parrhesia.App.Views.Mixer;

/// <summary>
/// Полоса микшера для одного узла: цвет-акцент типа, имя (двойной клик —
/// переименование), крупный LED-метр с пик-холдом, вертикальный фейдер
/// в дБ, кнопки M/S/B и подпись устройства.
/// Визуал и события — стрип сам граф не трогает, решает MixerView.
/// </summary>
internal sealed class MixerStrip : Border
{
    private const double PeakHoldSeconds = 0.8;
    private const float PeakFallPerSecond = 1.2f;

    private readonly TextBlock _nameText;
    private readonly TextBlock _dbText;
    private readonly TextBlock _deviceText;
    private readonly Button _muteButton;
    private readonly Button _soloButton;
    private readonly Button _bypassButton;

    private float _peak;
    private double _holdUntil;
    private bool _syncingFader;
    private DateTime _lastNameClick;

    public MixerStrip(AudioNode node)
    {
        Node = node;

        Width = 112;
        Padding = new Thickness(10, 0, 10, 10);
        Background = ResolveBrush("Brush.Elevated", "#FF1B1F26");
        BorderBrush = ResolveBrush("Brush.Stroke", "#FF262B33");
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(8);
        VerticalAlignment = VerticalAlignment.Top;
        Margin = new Thickness(0, 0, 10, 0);

        // Акцентная полоса по типу узла.
        var accent = new Border
        {
            Height = 3,
            CornerRadius = new CornerRadius(7, 7, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Background = KindBrush(node.Kind),
        };

        _nameText = new TextBlock
        {
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = ResolveBrush("Brush.Text", "#FFE6E9EF"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            Text = node.Name,
        };
        _nameText.MouseLeftButtonDown += OnNameClick;

        var header = new Border
        {
            Background = Brushes.Transparent,
            Padding = new Thickness(0, 7, 0, 0),
            Child = _nameText,
        };

        _dbText = new TextBlock
        {
            FontSize = 11,
            Foreground = ResolveBrush("Brush.Cyan", "#FF35D0C8"),
            Margin = new Thickness(0, 3, 0, 0),
            Text = FormatDb(node.Gain),
        };

        Meter = new LedMeterControl
        {
            Orientation = Orientation.Vertical,
            Width = 16,
            Height = 210,
            Margin = new Thickness(0, 8, 8, 0),
        };

        Fader = new Slider
        {
            Style = (Style)Application.Current!.FindResource("FaderSlider")!,
            Orientation = Orientation.Vertical,
            Width = 30,
            Height = 210,
            Margin = new Thickness(0, 8, 0, 0),
            Minimum = Decibels.MinDb,
            Maximum = 6,
            Value = SliderValue(node.Gain),
        };
        Fader.ValueChanged += OnFaderChanged;

        var meters = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
        };
        meters.Children.Add(Meter);
        meters.Children.Add(Fader);

        _muteButton = BuildChoiceButton("M", OnMuteClick);
        _soloButton = BuildChoiceButton("S", OnSoloClick);
        _bypassButton = BuildChoiceButton("B", OnBypassClick);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 10, 0, 6),
        };
        buttons.Children.Add(_muteButton);
        buttons.Children.Add(_soloButton);
        buttons.Children.Add(_bypassButton);

        _deviceText = new TextBlock
        {
            FontSize = 10,
            Foreground = ResolveBrush("Brush.TextFaint", "#FF5C6472"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            Text = DescribeDevice(node),
            ToolTip = DescribeDevice(node),
        };

        var root = new DockPanel();
        DockPanel.SetDock(accent, Dock.Top);
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(_dbText, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Bottom);
        DockPanel.SetDock(_deviceText, Dock.Bottom);
        root.Children.Add(accent);
        root.Children.Add(buttons);
        root.Children.Add(_deviceText);
        root.Children.Add(header);
        root.Children.Add(_dbText);
        root.Children.Add(meters);
        Child = root;

        BuildContextMenu();
        ApplyStates();
    }

    public AudioNode Node { get; }

    public LedMeterControl Meter { get; }

    public Slider Fader { get; }

    /// <summary>Двойной клик по имени.</summary>
    public event Action<MixerStrip>? RenameRequested;

    /// <summary>ПКМ → удалить.</summary>
    public event Action<MixerStrip>? DeleteRequested;

    /// <summary>«В группу ▸» — наполняется MixerView при каждом открытии меню
    /// (список ручных групп динамический).</summary>
    public MenuItem GroupMenuItem { get; private set; } = null!;

    /// <summary>«Из группы убрать» — включается, когда узел состоит в группе.</summary>
    public MenuItem UngroupMenuItem { get; private set; } = null!;

    /// <summary>Перенос узла в группу по id либо null — «из группы убрать».</summary>
    public event Action<MixerStrip, string?>? AssignGroupRequested;

    public event Action<MixerStrip>? MuteToggled;

    public event Action<MixerStrip>? SoloToggled;

    public event Action<MixerStrip>? BypassToggled;

    /// <summary>Фейдер двигается (линейный гейн).</summary>
    public event Action<MixerStrip, float>? GainChanged;

    /// <summary>Обновление из модели: имя, дБ, состояния кнопок, устройство.</summary>
    public void RefreshFromNode()
    {
        _nameText.Text = Node.Name;
        _dbText.Text = FormatDb(Node.Gain);
        _deviceText.Text = DescribeDevice(Node);
        _deviceText.ToolTip = _deviceText.Text;

        if (!_syncingFader && Math.Abs(Fader.Value - SliderValue(Node.Gain)) > 0.01)
        {
            _syncingFader = true;
            try
            {
                Fader.Value = SliderValue(Node.Gain);
            }
            finally
            {
                _syncingFader = false;
            }
        }

        ApplyStates();
    }

    /// <summary>Метр со значением (0..1) и пик-холдом.</summary>
    public void UpdateMeter(float value01, double now, double dt)
    {
        if (value01 >= _peak)
        {
            _peak = value01;
            _holdUntil = now + PeakHoldSeconds;
        }
        else if (now >= _holdUntil)
        {
            _peak = MathF.Max(value01, _peak - (PeakFallPerSecond * (float)dt));
        }

        Meter.Value = value01;
        Meter.Peak = _peak;
    }

    private void ApplyStates()
    {
        ApplyChoice(_muteButton, Node.Mute, "Brush.Danger", "#FFFF5A52");
        ApplyChoice(_soloButton, Node.Solo, "Brush.Accent", "#FFFFB020");
        ApplyChoice(_bypassButton, Node.Bypassed, "Brush.Cyan", "#FF35D0C8");
    }

    private static void ApplyChoice(Button button, bool active, string key, string fallback)
    {
        button.Background = active ? ResolveBrush(key, fallback) : ResolveBrush("Brush.Elevated", "#FF1B1F26");
        button.BorderBrush = active ? ResolveBrush(key, fallback) : ResolveBrush("Brush.StrokeStrong", "#FF39404B");
        button.Foreground = active
            ? ResolveBrush("Brush.Deep", "#FF0B0D10")
            : ResolveBrush("Brush.TextDim", "#FF8B93A1");
    }

    private Button BuildChoiceButton(string label, RoutedEventHandler onClick)
    {
        var button = new Button
        {
            Content = label,
            Width = 26,
            Height = 22,
            Padding = new Thickness(0),
            Margin = new Thickness(2, 0, 0, 0),
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            ToolTip = label switch
            {
                "M" => "Mute — полное перекрытие",
                "S" => "Solo — слышно только этот канал",
                _ => "Bypass — обход: сигнал сквозь без гейна",
            },
        };
        button.Click += onClick;
        return button;
    }

    private void BuildContextMenu()
    {
        var menu = new ContextMenu { Placement = PlacementMode.MousePoint, MinWidth = 160 };

        var rename = new MenuItem { Header = "Переименовать" };
        rename.Click += (_, _) => RenameRequested?.Invoke(this);

        GroupMenuItem = new MenuItem { Header = "В группу", IsEnabled = false };

        UngroupMenuItem = new MenuItem { Header = "Из группы убрать", IsEnabled = false };
        UngroupMenuItem.Click += (_, _) => AssignGroupRequested?.Invoke(this, null);

        var delete = new MenuItem { Header = "Удалить" };
        delete.Click += (_, _) => DeleteRequested?.Invoke(this);

        menu.Items.Add(rename);
        menu.Items.Add(GroupMenuItem);
        menu.Items.Add(UngroupMenuItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(delete);

        ContextMenu = menu;
    }

    private void OnNameClick(object sender, MouseButtonEventArgs e)
    {
        var now = DateTime.UtcNow;
        if (now - _lastNameClick < TimeSpan.FromMilliseconds(400))
        {
            _lastNameClick = default;
            RenameRequested?.Invoke(this);
            e.Handled = true;
            return;
        }

        _lastNameClick = now;
    }

    private void OnFaderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncingFader)
        {
            return;
        }

        _dbText.Text = FormatDb((float)e.NewValue);
        GainChanged?.Invoke(this, Decibels.FromDb((float)e.NewValue));
    }

    private void OnMuteClick(object sender, RoutedEventArgs e) => MuteToggled?.Invoke(this);

    private void OnSoloClick(object sender, RoutedEventArgs e) => SoloToggled?.Invoke(this);

    private void OnBypassClick(object sender, RoutedEventArgs e) => BypassToggled?.Invoke(this);

    private static double SliderValue(float gain)
    {
        var db = Decibels.ToDb(gain);
        return float.IsNegativeInfinity(db) ? Decibels.MinDb : Math.Max(db, Decibels.MinDb);
    }

    private static string FormatDb(float gain)
    {
        var db = Decibels.ToDb(gain);
        return float.IsNegativeInfinity(db) ? "−∞ дБ" : db.ToString("0.0") + " дБ";
    }

    private static string DescribeDevice(AudioNode node)
    {
        if (node.DeviceId is null)
        {
            return "устройство не привязано";
        }

        if (node.DeviceId == "virtual:parrhesia")
        {
            return "виртуальный: Parrhesia Out";
        }

        if (node.DeviceId == "default:capture" || node.DeviceId == "default:render")
        {
            return "по умолчанию";
        }

        if (node.DeviceId == "loopback:default")
        {
            return "loopback: по умолчанию";
        }

        var id = node.DeviceId.StartsWith("loopback:", StringComparison.Ordinal)
            ? node.DeviceId["loopback:".Length..]
            : node.DeviceId;

        foreach (var flow in new[] { DeviceFlow.Capture, DeviceFlow.Render })
        {
            foreach (var device in AppServices.Devices.GetDevices(flow))
            {
                if (device.Id == id)
                {
                    return device.Name;
                }
            }
        }

        return node.DeviceId;
    }

    private static Brush KindBrush(NodeKind kind) => kind switch
    {
        NodeKind.Source => ResolveBrush("Brush.Cyan", "#FF35D0C8"),
        NodeKind.Bus => ResolveBrush("Brush.Accent", "#FFFFB020"),
        _ => ResolveBrush("Brush.Success", "#FF3DDC84"),
    };

    private static Brush ResolveBrush(string key, string fallbackHex)
    {
        if (Application.Current?.TryFindResource(key) is Brush brush)
        {
            return brush;
        }

        var fallback = new SolidColorBrush((Color)ColorConverter.ConvertFromString(fallbackHex)!);
        fallback.Freeze();
        return fallback;
    }
}
