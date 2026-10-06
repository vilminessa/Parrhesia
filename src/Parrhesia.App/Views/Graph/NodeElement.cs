using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using Parrhesia.App.Controls;
using Parrhesia.Core.Graph;

namespace Parrhesia.App.Views.Graph;

internal enum PortHighlight
{
    None,
    Valid,
    Invalid,
}

/// <summary>
/// Визуал одного узла графа: заголовок с цветом типа, входной порт слева,
/// выходной справа. Позиция хранится в координатах мира (Canvas).
/// </summary>
internal sealed class NodeElement : Border
{
    public const double NodeWidth = 168;
    public const double PortVisualRadius = 5;

    /// <summary>Смещение центров каналов от центра узла (порты стоят столбиком).</summary>
    public const double PortChannelOffset = 9.0;

    /// <summary>Маркер «бандл-порт» (один порт на сторону в однонодовом режиме).</summary>
    public const int BundleChannel = -1;

    private static readonly DropShadowEffect SelectedEffect = CreateSelectionEffect();

    private readonly Border _root;
    private readonly TextBlock _title;
    private readonly Border[] _inPorts = new Border[2];
    private readonly Border[] _outPorts = new Border[2];
    private readonly Grid _grid;

    private StackPanel? _inColumn;
    private StackPanel? _outColumn;
    private bool _builtExpanded;
    private int _builtChannelCount;

    /// <summary>Мини-метр уровня узла (заполняется снаружи через render-тикер).</summary>
    public LedMeterControl Meter { get; }

    /// <summary>Клик по бейджу «B» в шапке узла.</summary>
    public event Action<AudioNode>? ToggleBypassRequested;

    /// <summary>Клик по бейджу «M» в шапке узла.</summary>
    public event Action<AudioNode>? ToggleMuteRequested;

    private readonly Border _bypassBadge;
    private readonly Border _muteBadge;

    public NodeElement(AudioNode node)
    {
        Node = node;

        for (var channel = ChannelMap.Left; channel <= ChannelMap.Right; channel++)
        {
            _inPorts[channel] = BuildPort(isInput: true, channel);
            _outPorts[channel] = BuildPort(isInput: false, channel);
        }

        _title = new TextBlock
        {
            Foreground = ResolveBrush("Brush.Text", "#FFE6E9EF"),
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            Text = node.Name,
        };

        var kindDot = new Ellipse
        {
            Width = 7,
            Height = 7,
            Fill = KindBrush(node.Kind),
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };

        _bypassBadge = BuildBadge("B", () => ToggleBypassRequested?.Invoke(Node));
        _muteBadge = BuildBadge("M", () => ToggleMuteRequested?.Invoke(Node));

        var kindLabel = new TextBlock
        {
            Foreground = ResolveBrush("Brush.TextFaint", "#FF5C6472"),
            FontSize = 10,
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Text = KindLabel(node.Kind),
        };

        var header = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(kindDot, Dock.Left);
        DockPanel.SetDock(kindLabel, Dock.Right);
        DockPanel.SetDock(_bypassBadge, Dock.Right);
        DockPanel.SetDock(_muteBadge, Dock.Right);
        header.Children.Add(kindDot);
        header.Children.Add(kindLabel);
        header.Children.Add(_bypassBadge);
        header.Children.Add(_muteBadge);
        header.Children.Add(_title);

        Meter = new LedMeterControl
        {
            Orientation = Orientation.Horizontal,
            Height = 5,
            Margin = new Thickness(0, 6, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };

        var bodyContent = new Grid();
        bodyContent.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        bodyContent.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(header, 0);
        Grid.SetRow(Meter, 1);
        bodyContent.Children.Add(header);
        bodyContent.Children.Add(Meter);

        var body = new Border
        {
            Background = ResolveBrush("Brush.Elevated", "#FF1B1F26"),
            BorderBrush = ResolveBrush("Brush.Stroke", "#FF262B33"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(11, 8, 11, 8),
            MinHeight = 38,
            Child = bodyContent,
        };

        var grid = new Grid();
        grid.Children.Add(body);
        _grid = grid;
        RebuildPortColumns();

        _root = new Border
        {
            Width = NodeWidth,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            BorderBrush = ResolveBrush("Brush.Accent", "#FFFFB020"),
            CornerRadius = new CornerRadius(9),
            Child = grid,
        };

        Child = _root;
        Background = Brushes.Transparent;
        SnapsToDevicePixels = true;
    }

    public AudioNode Node { get; }

    /// <summary>Мастеринг-режим: порт на каждый канал с подписью. false — один бандл-порт на сторону.</summary>
    public bool Expanded { get; private set; }

    public double Left { get; private set; }

    public double Top { get; private set; }

    public Point InputPortCenter(int channel) => new(Left, ChannelCenterY(channel));

    public Point OutputPortCenter(int channel) => new(Left + ActualWidth, ChannelCenterY(channel));

    private double ChannelCenterY(int channel)
    {
        var centerY = Top + (ActualHeight / 2);
        if (channel == BundleChannel || Node.ChannelCount <= 1)
        {
            return centerY;
        }

        var clamped = Math.Clamp(channel, 0, Node.ChannelCount - 1);
        var offset = (clamped - ((Node.ChannelCount - 1) / 2.0)) * (PortChannelOffset * 2);
        return centerY + offset;
    }

    /// <summary>Переключение режима отображения портов (однонодовый/мастеринг).</summary>
    public void SetViewMode(bool expanded)
    {
        if (Expanded == expanded)
        {
            return;
        }

        Expanded = expanded;
        RebuildPortColumns();
    }

    /// <summary>Пересобирает порт-колонки под режим и число каналов узла.</summary>
    private void RebuildPortColumns()
    {
        if (_inColumn is not null)
        {
            _grid.Children.Remove(_inColumn);
            _inColumn = null;
        }

        if (_outColumn is not null)
        {
            _grid.Children.Remove(_outColumn);
            _outColumn = null;
        }

        if (Node.HasInput)
        {
            _inColumn = BuildPortColumn(_inPorts, isInput: true);
            _grid.Children.Add(_inColumn);
        }

        if (Node.HasOutput)
        {
            _outColumn = BuildPortColumn(_outPorts, isInput: false);
            _grid.Children.Add(_outColumn);
        }

        _builtExpanded = Expanded;
        _builtChannelCount = Node.ChannelCount;
    }

    public void SetPosition(double x, double y)
    {
        Left = x;
        Top = y;
        Canvas.SetLeft(this, x);
        Canvas.SetTop(this, y);
    }

    public void SetSelected(bool selected)
    {
        // Обводка через glow: не двигает вёрстку узла при выделении.
        _root.Effect = selected ? SelectedEffect : null;
    }

    public void SetPortHighlight(bool outputPort, int channel, PortHighlight highlight)
    {
        var ports = outputPort ? _outPorts : _inPorts;
        var column = outputPort ? _outColumn : _inColumn;
        if (column is null)
        {
            return; // у этого типа узла нет такой стороны
        }

        // В бандл-режиме (и для моно) любой канал указывает на единственный порт.
        var index = !Expanded || channel < 0 ? 0 : Math.Clamp(channel, 0, ports.Length - 1);
        var port = ports[index];
        switch (highlight)
        {
            case PortHighlight.None:
                port.BorderThickness = new Thickness(1);
                port.BorderBrush = ResolveBrush("Brush.StrokeStrong", "#FF39404B");
                break;
            case PortHighlight.Valid:
                port.BorderThickness = new Thickness(2);
                port.BorderBrush = ResolveBrush("Brush.Cyan", "#FF35D0C8");
                break;
            case PortHighlight.Invalid:
                port.BorderThickness = new Thickness(2);
                port.BorderBrush = ResolveBrush("Brush.Danger", "#FFFF5A52");
                break;
        }
    }

    public void ClearPortHighlights()
    {
        for (var channel = ChannelMap.Left; channel <= ChannelMap.Right; channel++)
        {
            SetPortHighlight(outputPort: true, channel, PortHighlight.None);
            SetPortHighlight(outputPort: false, channel, PortHighlight.None);
        }
    }

    public void RefreshFromNode()
    {
        _title.Text = Node.Name;
        _title.Opacity = Node.Mute ? 0.45 : 1.0;
        UpdateBadges();

        if (Node.ChannelCount != _builtChannelCount || Expanded != _builtExpanded)
        {
            RebuildPortColumns();
        }
    }

    /// <summary>Состояния бейджей: B — обход (циан), M — мьют (красный).</summary>
    private void UpdateBadges()
    {
        ApplyBadgeState(_bypassBadge, Node.Bypassed, "Brush.Cyan", "#FF35D0C8");
        ApplyBadgeState(_muteBadge, Node.Mute, "Brush.Danger", "#FFFF5A52");
    }

    private static void ApplyBadgeState(Border badge, bool active, string activeBrushKey, string fallback)
    {
        badge.Background = active ? ResolveBrush(activeBrushKey, fallback) : Brushes.Transparent;
        badge.BorderBrush = active
            ? ResolveBrush(activeBrushKey, fallback)
            : ResolveBrush("Brush.Stroke", "#FF262B33");
        if (badge.Child is TextBlock label)
        {
            label.Foreground = active
                ? ResolveBrush("Brush.Deep", "#FF0B0D10")
                : ResolveBrush("Brush.TextFaint", "#FF5C6472");
        }
    }

    private Border BuildBadge(string label, Action onClick)
    {
        var badge = new Border
        {
            Width = 16,
            Height = 15,
            CornerRadius = new CornerRadius(3),
            BorderThickness = new Thickness(1),
            BorderBrush = ResolveBrush("Brush.Stroke", "#FF262B33"),
            Background = Brushes.Transparent,
            Margin = new Thickness(4, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = Cursors.Hand,
            Child = new TextBlock
            {
                Text = label,
                FontSize = 9,
                FontWeight = FontWeights.SemiBold,
                Foreground = ResolveBrush("Brush.TextFaint", "#FF5C6472"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };

        ToolTipService.SetToolTip(badge, label == "B" ? "Обход (Bypass)" : "Mute");
        badge.MouseLeftButtonDown += (_, args) =>
        {
            args.Handled = true;
            onClick();
        };
        return badge;
    }

    private static DropShadowEffect CreateSelectionEffect()
    {
        var effect = new DropShadowEffect
        {
            Color = Color.FromRgb(0xFF, 0xB0, 0x20),
            BlurRadius = 12,
            ShadowDepth = 0,
            Opacity = 0.95,
        };
        effect.Freeze();
        return effect;
    }

    /// <summary>Колонка портов под текущий режим: бандл (один порт) или по порту на канал.</summary>
    private StackPanel BuildPortColumn(Border[] ports, bool isInput)
    {
        var channelCount = Expanded ? Math.Max(1, Node.ChannelCount) : 1;
        var sideName = isInput ? "Вход" : "Выход";

        var column = new StackPanel
        {
            Orientation = Orientation.Vertical,
            HorizontalAlignment = isInput ? HorizontalAlignment.Left : HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = isInput
                ? new Thickness(-PortVisualRadius, 0, 0, 0)
                : new Thickness(0, 0, -PortVisualRadius, 0),
        };

        for (var channel = 0; channel < channelCount; channel++)
        {
            var port = ports[channel];
            port.ToolTip = Expanded
                ? $"{sideName} {Node.ChannelNames[channel]} (канал {channel + 1})"
                : $"{sideName}: все каналы ({Node.ChannelCount})";
            port.Margin = channel < channelCount - 1
                ? new Thickness(0, 0, 0, 8)
                : new Thickness(0);
            column.Children.Add(port);
        }

        return column;
    }

    private static Border BuildPort(bool isInput, int channel)
    {
        return new Border
        {
            Width = PortVisualRadius * 2,
            Height = PortVisualRadius * 2,
            CornerRadius = new CornerRadius(PortVisualRadius),
            Background = ResolveBrush("Brush.Panel", "#FF14171C"),
            BorderThickness = new Thickness(1),
            BorderBrush = ResolveBrush("Brush.StrokeStrong", "#FF39404B"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0),
        };
    }

    private static Brush KindBrush(NodeKind kind) => kind switch
    {
        NodeKind.Source => ResolveBrush("Brush.Cyan", "#FF35D0C8"),
        NodeKind.Bus => ResolveBrush("Brush.Accent", "#FFFFB020"),
        _ => ResolveBrush("Brush.Success", "#FF3DDC84"),
    };

    private static string KindLabel(NodeKind kind) => kind switch
    {
        NodeKind.Source => "вход",
        NodeKind.Bus => "шина",
        _ => "выход",
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
