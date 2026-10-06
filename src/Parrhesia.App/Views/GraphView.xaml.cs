using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Parrhesia.App.Views.Graph;
using Parrhesia.Core.Graph;

namespace Parrhesia.App.Views;

/// <summary>
/// Редактор схемы маршрутизации: ноды, кабели, pan/zoom.
/// Мир (координаты Canvas) масштабируется Transform'ом на вьюпорте.
/// Модель — <see cref="AudioGraph"/>; позиции узлов — UI-состояние вне модели.
/// </summary>
public partial class GraphView : UserControl
{
    private const double MinScale = 0.4;
    private const double MaxScale = 2.0;
    private const double ZoomFactor = 1.1;
    private const double PortHitRadius = 12.0;

    private readonly AudioGraph _graph = new();
    private readonly Dictionary<Guid, NodeElement> _elements = [];
    private readonly Dictionary<Guid, Point> _positions = [];
    private readonly CableLayer _cableLayer;

    private readonly ScaleTransform _scaleTransform = new(1, 1);
    private readonly TranslateTransform _translate = new(0, 0);

    private double _scale = 1.0;

    private bool _spaceDown;
    private bool _panning;
    private Point _panStart;
    private Point _panOrigin;

    private NodeElement? _draggedNode;
    private Point _dragOffset;

    private CableDrag? _cableDrag;
    private NodeElement? _selectedNode;
    private Route? _selectedRoute;

    private sealed record PortHit(NodeElement Element, bool Output);

    private sealed record CableDrag(Guid NodeId, bool FromOutput);

    public GraphView()
    {
        InitializeComponent();

        var transforms = new TransformGroup();
        transforms.Children.Add(_scaleTransform);
        transforms.Children.Add(_translate);
        World.RenderTransform = transforms;

        _cableLayer = new CableLayer
        {
            Routes = _graph.Routes,
            GetOutputPoint = id => _elements.TryGetValue(id, out var element) ? element.OutputPortCenter : null,
            GetInputPoint = id => _elements.TryGetValue(id, out var element) ? element.InputPortCenter : null,
        };
        World.Children.Add(_cableLayer);

        BuildDemoGraph();
        Rebuild();
        _graph.Changed += OnGraphChanged;
    }

    // ===== Мышь =====

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        Focus();

        if (e.ChangedButton == MouseButton.Middle || (e.ChangedButton == MouseButton.Left && _spaceDown))
        {
            _panning = true;
            _panStart = e.GetPosition(Viewport);
            _panOrigin = new Point(_translate.X, _translate.Y);
            CaptureMouse();
            Cursor = Cursors.SizeAll;
            e.Handled = true;
            return;
        }

        if (e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        var world = e.GetPosition(World);

        if (FindPort(world) is { } port)
        {
            StartCableDrag(port, world);
            e.Handled = true;
            return;
        }

        if (FindNodeAt(world) is { } node)
        {
            SelectNode(node);
            _draggedNode = node;
            _dragOffset = new Point(world.X - node.Left, world.Y - node.Top);
            Canvas.SetZIndex(node, 20);
            CaptureMouse();
            e.Handled = true;
            return;
        }

        if (_cableLayer.HitTest(world) is { } route)
        {
            SelectRoute(route);
            e.Handled = true;
            return;
        }

        ClearSelection();
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (_panning)
        {
            var screen = e.GetPosition(Viewport);
            _translate.X = _panOrigin.X + (screen.X - _panStart.X);
            _translate.Y = _panOrigin.Y + (screen.Y - _panStart.Y);
            return;
        }

        if (_draggedNode is { } node)
        {
            var world = e.GetPosition(World);
            var x = world.X - _dragOffset.X;
            var y = world.Y - _dragOffset.Y;
            node.SetPosition(x, y);
            _positions[node.Node.Id] = new Point(x, y);
            _cableLayer.InvalidateVisual();
            return;
        }

        if (_cableDrag is { } drag)
        {
            var world = e.GetPosition(World);
            _cableLayer.TempTo = world;
            UpdatePortHighlights(world, drag);
            _cableLayer.InvalidateVisual();
        }
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_panning && e.ChangedButton is MouseButton.Middle or MouseButton.Left)
        {
            _panning = false;
            ReleaseMouseCapture();
            Cursor = Cursors.Arrow;
            e.Handled = true;
        }

        if (_draggedNode is not null)
        {
            _draggedNode = null;
            ReleaseMouseCapture();
        }

        if (_cableDrag is { } drag && e.ChangedButton == MouseButton.Left)
        {
            FinishCableDrag(drag, e.GetPosition(World));
        }
    }

    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        var screen = e.GetPosition(Viewport);
        var world = new Point(
            (screen.X - _translate.X) / _scale,
            (screen.Y - _translate.Y) / _scale);

        var factor = e.Delta > 0 ? ZoomFactor : 1 / ZoomFactor;
        _scale = Math.Clamp(_scale * factor, MinScale, MaxScale);
        _scaleTransform.ScaleX = _scale;
        _scaleTransform.ScaleY = _scale;
        _translate.X = screen.X - world.X * _scale;
        _translate.Y = screen.Y - world.Y * _scale;

        _cableLayer.InverseScale = 1 / _scale;
        _cableLayer.InvalidateVisual();
        e.Handled = true;
    }

    // ===== Клавиатура =====

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space && !e.IsRepeat)
        {
            _spaceDown = true;
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape)
        {
            if (_cableDrag is not null)
            {
                EndCableDrag();
            }
            else
            {
                ClearSelection();
            }

            e.Handled = true;
            return;
        }

        if (e.Key is Key.Delete or Key.Back)
        {
            if (_selectedRoute is { } route)
            {
                _graph.RemoveRoute(route.FromId, route.ToId);
            }
            else if (_selectedNode is { } node)
            {
                _graph.RemoveNode(node.Node.Id);
            }

            e.Handled = true;
        }
    }

    private void OnKeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space)
        {
            _spaceDown = false;
        }
    }

    // ===== Перетаскивание кабеля =====

    private void StartCableDrag(PortHit origin, Point world)
    {
        _cableDrag = new CableDrag(origin.Element.Node.Id, origin.Output);
        _cableLayer.TempFrom = origin.Output
            ? origin.Element.OutputPortCenter
            : origin.Element.InputPortCenter;
        _cableLayer.TempTo = world;
        _cableLayer.TempValid = false;
        CaptureMouse();
        Cursor = Cursors.Cross;
        UpdatePortHighlights(world, _cableDrag);
        _cableLayer.InvalidateVisual();
    }

    private void FinishCableDrag(CableDrag drag, Point world)
    {
        var target = FindPort(world);
        EndCableDrag();

        if (target is null || target.Output == drag.FromOutput)
        {
            return;
        }

        var (from, to) = drag.FromOutput
            ? (drag.NodeId, target.Element.Node.Id)
            : (target.Element.Node.Id, drag.NodeId);
        _graph.AddRoute(from, to, out _);
    }

    private void EndCableDrag()
    {
        _cableDrag = null;
        _cableLayer.TempFrom = null;
        _cableLayer.TempTo = null;
        _cableLayer.TempValid = true;
        foreach (var element in _elements.Values)
        {
            element.ClearPortHighlights();
        }

        ReleaseMouseCapture();
        Cursor = Cursors.Arrow;
        _cableLayer.InvalidateVisual();
    }

    /// <summary>Во время перетаскивания подсвечивает все валидные порты и порт под курсором.</summary>
    private void UpdatePortHighlights(Point world, CableDrag drag)
    {
        foreach (var element in _elements.Values)
        {
            element.ClearPortHighlights();
            if (Validate(drag, element.Node.Id) == RouteError.None)
            {
                element.SetPortHighlight(!drag.FromOutput, PortHighlight.Valid);
            }
        }

        if (FindPort(world) is { } hovered)
        {
            var correctSide = hovered.Output != drag.FromOutput;
            var error = correctSide ? Validate(drag, hovered.Element.Node.Id) : RouteError.SelfLoop;
            var hoverValid = error == RouteError.None;
            hovered.Element.SetPortHighlight(
                hovered.Output,
                hoverValid ? PortHighlight.Valid : PortHighlight.Invalid);
            _cableLayer.TempValid = hoverValid;
        }
        else
        {
            _cableLayer.TempValid = true;
        }
    }

    private RouteError Validate(CableDrag drag, Guid otherId) =>
        drag.FromOutput
            ? _graph.ValidateRoute(drag.NodeId, otherId)
            : _graph.ValidateRoute(otherId, drag.NodeId);

    // ===== Выделение =====

    private void SelectNode(NodeElement? node)
    {
        _selectedNode?.SetSelected(false);
        _selectedNode = node;
        node?.SetSelected(true);

        if (_selectedRoute is not null)
        {
            _selectedRoute = null;
            _cableLayer.SelectedRoute = null;
            _cableLayer.InvalidateVisual();
        }
    }

    private void SelectRoute(Route? route)
    {
        _selectedNode?.SetSelected(false);
        _selectedNode = null;
        _selectedRoute = route;
        _cableLayer.SelectedRoute = route;
        _cableLayer.InvalidateVisual();
    }

    private void ClearSelection() => SelectNode(null);

    // ===== Хит-тесты =====

    private PortHit? FindPort(Point world)
    {
        var radius = PortHitRadius * _cableLayer.InverseScale;
        foreach (var element in _elements.Values)
        {
            if (Distance(element.InputPortCenter, world) <= radius)
            {
                return new PortHit(element, Output: false);
            }

            if (Distance(element.OutputPortCenter, world) <= radius)
            {
                return new PortHit(element, Output: true);
            }
        }

        return null;
    }

    private NodeElement? FindNodeAt(Point world)
    {
        foreach (var element in _elements.Values)
        {
            if (world.X >= element.Left &&
                world.X <= element.Left + element.ActualWidth &&
                world.Y >= element.Top &&
                world.Y <= element.Top + element.ActualHeight)
            {
                return element;
            }
        }

        return null;
    }

    private static double Distance(Point a, Point b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    // ===== Синхронизация с моделью =====

    private void OnGraphChanged(object? sender, GraphChange change)
    {
        Rebuild();

        if (_selectedRoute is { } route && !_graph.Routes.Contains(route))
        {
            _selectedRoute = null;
            _cableLayer.SelectedRoute = null;
        }

        if (_selectedNode is { } node && _graph.FindNode(node.Node.Id) is null)
        {
            node.SetSelected(false);
            _selectedNode = null;
        }

        _cableLayer.InvalidateVisual();
    }

    private void Rebuild()
    {
        foreach (var id in _elements.Keys.Where(id => _graph.FindNode(id) is null).ToList())
        {
            World.Children.Remove(_elements[id]);
            _elements.Remove(id);
        }

        foreach (var node in _graph.Nodes)
        {
            if (_elements.TryGetValue(node.Id, out var existing))
            {
                existing.RefreshFromNode();
                continue;
            }

            var element = new NodeElement(node);
            element.SizeChanged += (_, _) => _cableLayer.InvalidateVisual();

            if (!_positions.TryGetValue(node.Id, out var position))
            {
                position = DefaultPosition(node.Kind);
                _positions[node.Id] = position;
            }

            element.SetPosition(position.X, position.Y);
            _elements[node.Id] = element;
            World.Children.Add(element);
        }
    }

    private Point DefaultPosition(NodeKind kind)
    {
        var column = kind switch
        {
            NodeKind.Source => 0,
            NodeKind.Bus => 1,
            _ => 2,
        };

        // Считаем уже размещённые элементы: при пакетном построении (демо-граф)
        // модель содержит все узлы сразу, и счёт по модели сложил бы колонку в стопку.
        var row = _elements.Values.Count(e => e.Node.Kind == kind);
        return new Point(60 + column * 300, 60 + row * 96);
    }

    private void BuildDemoGraph()
    {
        var mic = _graph.AddNode("Микрофон", NodeKind.Source);
        var capture = _graph.AddNode("Захват устройств", NodeKind.Source);
        var browser = _graph.AddNode("Браузер", NodeKind.Source);
        var main = _graph.AddNode("Основная шина", NodeKind.Bus);
        var stream = _graph.AddNode("Шина стрима", NodeKind.Bus);
        var headphones = _graph.AddNode("Наушники", NodeKind.Sink);
        var discord = _graph.AddNode("Discord (вирт.)", NodeKind.Sink);

        _graph.AddRoute(mic.Id, main.Id, out _);
        _graph.AddRoute(capture.Id, main.Id, out _);
        _graph.AddRoute(browser.Id, main.Id, out _);
        _graph.AddRoute(browser.Id, stream.Id, out _);
        _graph.AddRoute(main.Id, headphones.Id, out _);
        _graph.AddRoute(main.Id, stream.Id, out _);
        _graph.AddRoute(stream.Id, discord.Id, out _);
    }
}
