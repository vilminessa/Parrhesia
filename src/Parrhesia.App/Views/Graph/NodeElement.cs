using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
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

    /// <summary>Смещение центров каналов L/R от центра узла (порты стоят столбиком).</summary>
    public const double PortChannelOffset = 9.0;

    private static readonly DropShadowEffect SelectedEffect = CreateSelectionEffect();

    private readonly Border _root;
    private readonly TextBlock _title;
    private readonly Border[] _inPorts = new Border[2];
    private readonly Border[] _outPorts = new Border[2];

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
        header.Children.Add(kindDot);
        header.Children.Add(kindLabel);
        header.Children.Add(_title);

        var body = new Border
        {
            Background = ResolveBrush("Brush.Elevated", "#FF1B1F26"),
            BorderBrush = ResolveBrush("Brush.Stroke", "#FF262B33"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(11, 8, 11, 8),
            MinHeight = 38,
            Child = header,
        };

        var inPanel = BuildPortColumn(_inPorts, isInput: true);
        var outPanel = BuildPortColumn(_outPorts, isInput: false);

        var grid = new Grid();
        grid.Children.Add(body);
        grid.Children.Add(inPanel);
        grid.Children.Add(outPanel);

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

    public double Left { get; private set; }

    public double Top { get; private set; }

    public Point InputPortCenter(int channel) => new(Left, ChannelCenterY(channel));

    public Point OutputPortCenter(int channel) => new(Left + ActualWidth, ChannelCenterY(channel));

    private double ChannelCenterY(int channel) =>
        Top + (ActualHeight / 2) + (channel == ChannelMap.Left ? -PortChannelOffset : PortChannelOffset);

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
        var port = outputPort ? _outPorts[channel] : _inPorts[channel];
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

    private static StackPanel BuildPortColumn(Border[] ports, bool isInput)
    {
        var channelName = isInput ? "Вход" : "Выход";
        for (var channel = ChannelMap.Left; channel <= ChannelMap.Right; channel++)
        {
            var port = ports[channel];
            port.ToolTip = $"{channelName} {(channel == ChannelMap.Left ? "L" : "R")}";
        }

        var column = new StackPanel
        {
            Orientation = Orientation.Vertical,
            HorizontalAlignment = isInput ? HorizontalAlignment.Left : HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = isInput
                ? new Thickness(-PortVisualRadius, 0, 0, 0)
                : new Thickness(0, 0, -PortVisualRadius, 0),
        };
        column.Children.Add(ports[ChannelMap.Left]);
        column.Children.Add(ports[ChannelMap.Right]);
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
            // Верхний столбика — L, у него зазор до нижнего (R).
            Margin = channel == ChannelMap.Left
                ? new Thickness(0, 0, 0, 8)
                : new Thickness(0),
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
