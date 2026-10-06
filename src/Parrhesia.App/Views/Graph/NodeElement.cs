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

    private static readonly DropShadowEffect SelectedEffect = CreateSelectionEffect();

    private readonly Border _root;
    private readonly TextBlock _title;
    private readonly Border _inPort;
    private readonly Border _outPort;

    public NodeElement(AudioNode node)
    {
        Node = node;

        _inPort = BuildPort(isInput: true);
        _outPort = BuildPort(isInput: false);

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

        var grid = new Grid();
        grid.Children.Add(body);
        grid.Children.Add(_inPort);
        grid.Children.Add(_outPort);

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

    public Point InputPortCenter => new(Left, Top + ActualHeight / 2);

    public Point OutputPortCenter => new(Left + ActualWidth, Top + ActualHeight / 2);

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

    public void SetPortHighlight(bool outputPort, PortHighlight highlight)
    {
        var port = outputPort ? _outPort : _inPort;
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
        SetPortHighlight(true, PortHighlight.None);
        SetPortHighlight(false, PortHighlight.None);
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

    private static Border BuildPort(bool isInput)
    {
        return new Border
        {
            Width = PortVisualRadius * 2,
            Height = PortVisualRadius * 2,
            CornerRadius = new CornerRadius(PortVisualRadius),
            Background = ResolveBrush("Brush.Panel", "#FF14171C"),
            BorderThickness = new Thickness(1),
            BorderBrush = ResolveBrush("Brush.StrokeStrong", "#FF39404B"),
            HorizontalAlignment = isInput ? HorizontalAlignment.Left : HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = isInput
                ? new Thickness(-PortVisualRadius, 0, 0, 0)
                : new Thickness(0, 0, -PortVisualRadius, 0),
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
