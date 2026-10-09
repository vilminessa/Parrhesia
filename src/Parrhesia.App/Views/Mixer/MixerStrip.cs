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

    /// <summary>Дефолтная высота пульта: компакт, чтобы вся лента влезала в окно.</summary>
    public const double DefaultCardHeight = 200;

    /// <summary>Границы ресайза за нижний край (px); кратны шагу — снап всегда попадает в диапазон.</summary>
    public const double MinCardHeight = 160;
    public const double MaxCardHeight = 424;

    /// <summary>Шаг привязки высоты при перетаскивании.</summary>
    public const double CardHeightSnap = 8;

    /// <summary>Ниже высоты — компакт-режим: подпись устройства уходит в тултип.</summary>
    private const double CompactBelow = 230;

    /// <summary>Минимальная высота зоны метра/фейдера (не даём им схлопнуться).</summary>
    private const double MeterAreaMin = 40;

    /// <summary>Ширина карточки в продвинутом режиме (вся ширина блока masonry).</summary>
    public const double AdvancedCardWidth = 240;

    private readonly TextBlock _nameText;
    private readonly TextBlock _dbText;
    private readonly TextBlock _deviceText;
    private readonly Button _muteButton;
    private readonly Button _soloButton;
    private readonly Button _bypassButton;
    private readonly Border _handle;
    private readonly bool _advanced;

    /// <summary>Чипы эффекторов (id → кнопка) для синхронизации из модели.</summary>
    private readonly Dictionary<string, ToggleButton> _fxChips = [];

    private float _peak;
    private double _holdUntil;
    private bool _syncingFader;
    private DateTime _lastNameClick;

    private bool _dragging;
    private double _dragStartY;
    private double _dragStartHeight;

    public MixerStrip(AudioNode node, bool advanced)
    {
        Node = node;
        _advanced = advanced;

        Width = advanced ? AdvancedCardWidth : 112;
        Padding = new Thickness(10, 0, 10, 10);
        Background = ResolveBrush("Brush.Elevated", "#FF1B1F26");
        BorderBrush = ResolveBrush("Brush.Stroke", "#FF262B33");
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(8);
        VerticalAlignment = VerticalAlignment.Top;
        Margin = new Thickness(0, 0, 10, 0);
        Height = ClampCardHeight(node.StripHeight ?? DefaultCardHeight);

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
            VerticalAlignment = VerticalAlignment.Stretch,
        };
        meters.Children.Add(Meter);
        meters.Children.Add(Fader);
        meters.SizeChanged += (_, e) => OnMetersResized(e);

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

        // Хэндл-грип: перетаскивание за нижний край меняет высоту пульта.
        var gripBrush = ResolveBrush("Brush.Stroke", "#FF262B33");
        _handle = new Border
        {
            Height = 8,
            Background = Brushes.Transparent,
            Cursor = Cursors.SizeNS,
            ToolTip = "Потяните нижний край — высота пульта (двойной клик — дефолт)",
        };
        _handle.MouseEnter += (_, _) =>
        {
            if (!_dragging)
            {
                _handle.Background = gripBrush;
            }
        };
        _handle.MouseLeave += (_, _) =>
        {
            if (!_dragging)
            {
                _handle.Background = Brushes.Transparent;
            }
        };
        _handle.MouseLeftButtonDown += OnResizeStart;
        _handle.MouseMove += OnResizeMove;
        _handle.MouseLeftButtonUp += OnResizeEnd;

        var root = new DockPanel();
        DockPanel.SetDock(accent, Dock.Top);
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(_dbText, Dock.Top);
        DockPanel.SetDock(_handle, Dock.Bottom);
        DockPanel.SetDock(buttons, Dock.Bottom);
        DockPanel.SetDock(_deviceText, Dock.Bottom);
        root.Children.Add(accent);
        root.Children.Add(_handle);
        root.Children.Add(buttons);
        root.Children.Add(_deviceText);
        root.Children.Add(header);
        root.Children.Add(_dbText);
        root.Children.Add(meters);

        if (_advanced)
        {
            // Две колонки: классический пульт (90 = сегодняшняя внутренняя ширина)
            // + FX-поле на всё остальное (блок masonry не меняется — 240+10=250).
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(root, 0);
            var fxColumn = BuildFxColumn();
            Grid.SetColumn(fxColumn, 1);
            grid.Children.Add(root);
            grid.Children.Add(fxColumn);
            Child = grid;
        }
        else
        {
            Child = root;
        }

        SizeChanged += (_, _) => ApplyCompactMode();

        BuildContextMenu();
        ApplyStates();
    }

    /// <summary>Колонка эффектов (продвинутый режим): чипы-заглушки.
    /// ЛКМ — вкл/выкл, ПКМ — окно настроек эффектора.</summary>
    private Border BuildFxColumn()
    {
        var panel = new StackPanel { Margin = new Thickness(10, 2, 0, 0) };
        panel.Children.Add(new TextBlock
        {
            Text = "ЭФФЕКТЫ",
            FontSize = 9,
            FontWeight = FontWeights.SemiBold,
            Foreground = ResolveBrush("Brush.TextFaint", "#FF5C6472"),
            Margin = new Thickness(0, 0, 0, 6),
        });

        var chipStyle = (Style)Application.Current!.FindResource("Chip")!;
        foreach (var (fxId, label) in FxInfo.All)
        {
            var chip = new ToggleButton
            {
                Style = chipStyle,
                Content = label,
                Height = 24,
                FontSize = 11,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Margin = new Thickness(0, 0, 0, 4),
                IsChecked = IsFxOn(fxId),
                ToolTip = "ЛКМ — включить/выключить · ПКМ — настройки эффектора",
            };

            // ПКМ не должен открывать контекстное меню пульта — только окно эффектора.
            chip.PreviewMouseRightButtonDown += (_, e) => e.Handled = true;
            chip.MouseRightButtonUp += (_, e) =>
            {
                e.Handled = true;
                FxSettingsRequested?.Invoke(this, fxId);
            };
            chip.Click += (_, _) => FxToggleRequested?.Invoke(this, fxId, chip.IsChecked == true);

            _fxChips[fxId] = chip;
            panel.Children.Add(chip);
        }

        return new Border
        {
            BorderBrush = ResolveBrush("Brush.Stroke", "#FF262B33"),
            BorderThickness = new Thickness(1, 0, 0, 0),
            Padding = new Thickness(10, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Stretch,
            Child = panel,
        };
    }

    private bool IsFxOn(string fxId) =>
        Node.FxEnabled.TryGetValue(fxId, out var on) && on;

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

    /// <summary>Ресайз завершён (отпустили мышь / сброс) — MixerView сохраняет высоту в узел.</summary>
    public event Action<MixerStrip>? HeightCommitted;

    /// <summary>Чип эффектора переключён ЛКМ (id, вкл) — MixerView применит к графу.</summary>
    public event Action<MixerStrip, string, bool>? FxToggleRequested;

    /// <summary>ПКМ по чипу эффектора — открыть окно настроек.</summary>
    public event Action<MixerStrip, string>? FxSettingsRequested;

    /// <summary>Обновление из модели: имя, дБ, состояния кнопок, устройство, чипы FX.</summary>
    public void RefreshFromNode()
    {
        _nameText.Text = Node.Name;
        _dbText.Text = FormatDb(Node.Gain);
        _deviceText.Text = DescribeDevice(Node);
        _deviceText.ToolTip = _deviceText.Text;

        foreach (var (fxId, chip) in _fxChips)
        {
            var on = IsFxOn(fxId);
            if (chip.IsChecked != on)
            {
                chip.IsChecked = on;
            }
        }

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

    // ── Ресайз пульта за нижний край ──────────────────────────────────────

    /// <summary>Ограничение высоты допустимым диапазоном (пурия-функция, для UI и тестов).</summary>
    public static double ClampCardHeight(double height) =>
        Math.Min(MaxCardHeight, Math.Max(MinCardHeight, height));

    /// <summary>Привязка высоты к шагу (пурия-функция, для UI и тестов).</summary>
    internal static double SnapCardHeight(double height) =>
        Math.Round(height / CardHeightSnap) * CardHeightSnap;

    /// <summary>Применяет высоту из модели (null → дефолт) с клампом.</summary>
    public void ApplyHeight(int? stripHeight)
    {
        var target = ClampCardHeight(stripHeight ?? DefaultCardHeight);
        if (Math.Abs(Height - target) > 0.5)
        {
            Height = target;
        }
    }

    private void OnResizeStart(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            // Двойной клик по хэндлу — сброс высоты в дефолт.
            Height = DefaultCardHeight;
            if (_dragging)
            {
                _dragging = false;
                _handle.ReleaseMouseCapture();
                _handle.Background = Brushes.Transparent;
            }

            HeightCommitted?.Invoke(this);
            e.Handled = true;
            return;
        }

        _dragging = true;
        _dragStartY = e.GetPosition(this).Y;
        _dragStartHeight = double.IsNaN(Height) ? DefaultCardHeight : Height;
        _handle.CaptureMouse();
        e.Handled = true;
    }

    private void OnResizeMove(object sender, MouseEventArgs e)
    {
        if (!_dragging || !_handle.IsMouseCaptured)
        {
            return;
        }

        var delta = e.GetPosition(this).Y - _dragStartY;
        Height = ClampCardHeight(SnapCardHeight(_dragStartHeight + delta));
    }

    private void OnResizeEnd(object sender, MouseButtonEventArgs e)
    {
        if (_dragging)
        {
            _dragging = false;
            _handle.ReleaseMouseCapture();
            _handle.Background = Brushes.Transparent;
            HeightCommitted?.Invoke(this);
        }

        e.Handled = true;
    }

    /// <summary>Метр и фейдер тянутся под доступную высоту зоны (ресайз живой).</summary>
    private void OnMetersResized(SizeChangedEventArgs e)
    {
        var area = Math.Max(MeterAreaMin, e.NewSize.Height - 10);
        if (Math.Abs(Meter.Height - area) > 0.5)
        {
            Meter.Height = area;
            Fader.Height = area;
        }
    }

    /// <summary>Компакт-режим: при малой высоте подпись устройства уходит в тултип пульта.</summary>
    private void ApplyCompactMode()
    {
        var compact = ActualHeight > 0 && ActualHeight < CompactBelow;
        var visible = compact ? Visibility.Collapsed : Visibility.Visible;
        if (_deviceText.Visibility != visible)
        {
            _deviceText.Visibility = visible;
        }

        ToolTip = compact ? DescribeDevice(Node) : null;
    }

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
