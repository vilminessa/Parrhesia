using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Parrhesia.App.Rendering;
using Parrhesia.App.Views.Graph;
using Parrhesia.Audio.Devices;
using Parrhesia.Audio.Engine;
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

    private readonly AudioGraph _graph;
    private readonly Dictionary<Guid, NodeElement> _elements = [];
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
    private ReattachDrag? _reattachDrag;
    private NodeElement? _selectedNode;
    private Route? _selectedRoute;

    /// <summary>Глушит реакцию на события графа во время внутренних операций Rebuild.</summary>
    private bool _suppressChanges;

    /// <summary>Глушит обработчики инспектора при программном обновлении полей.</summary>
    private bool _syncing;

    /// <summary>Глушит переключатели режима при программной установке.</summary>
    private bool _syncingMode;

    private Guid _deviceNodeId = Guid.Empty;

    /// <summary>Мастеринг-режим отображения портов (иначе бандл).</summary>
    private bool _expandedView;

    /// <summary>Выделенная пара каналов выделенного кабеля (мастеринг-режим).</summary>
    private (int FromChannel, int ToChannel)? _selectedPair;

    private sealed record PortHit(NodeElement Element, bool Output, int Channel);

    private sealed record CableDrag(Guid NodeId, bool FromOutput, int Channel);

    /// <summary>Перетаскивание уже прикреплённого конца жилы (переподключение).</summary>
    private sealed record ReattachDrag(
        CableLayer.ReattachTarget Target,
        bool GrabbedAtOutput,
        Guid GrabbedNodeId,
        Guid OtherNodeId,
        Point OtherEnd);

    public GraphView()
    {
        InitializeComponent();

        _graph = AppServices.Graph;

        var transforms = new TransformGroup();
        transforms.Children.Add(_scaleTransform);
        transforms.Children.Add(_translate);
        World.RenderTransform = transforms;

        _cableLayer = new CableLayer
        {
            Routes = _graph.Routes,
            GetOutputPoint = (id, channel) =>
                _elements.TryGetValue(id, out var element) ? element.OutputPortCenter(channel) : null,
            GetInputPoint = (id, channel) =>
                _elements.TryGetValue(id, out var element) ? element.InputPortCenter(channel) : null,
        };
        World.Children.Add(_cableLayer);

        Rebuild();
        _graph.Changed += OnGraphChanged;
    }

    // ===== Мышь =====

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        Focus();

        if (e.ChangedButton == MouseButton.Right)
        {
            HandleContextMenu(e);
            return;
        }

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
            // Зажатие на занятом порту — хватаем прикреплённый конец жилы
            // (переподключение); на свободном — новое соединение.
            var attached = _cableLayer.FindAttachedWire(
                world,
                port.Element,
                port.Output,
                port.Channel,
                PortHitRadius * _cableLayer.InverseScale);

            if (attached is not null)
            {
                StartReattach(port, attached, world);
            }
            else
            {
                StartCableDrag(port, world);
            }

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

        if (_cableLayer.HitTest(world) is { } hit)
        {
            SelectRoute(hit.Route, PairOf(hit));
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
            _graph.SetNodePosition(node.Node.Id, x, y);
            _cableLayer.InvalidateVisual();
            return;
        }

        if (_reattachDrag is { } reattach)
        {
            var world = e.GetPosition(World);
            _cableLayer.TempTo = world;
            UpdateReattachHighlights(world, reattach);
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

        if (_reattachDrag is { } reattach && e.ChangedButton == MouseButton.Left)
        {
            FinishReattach(reattach, e.GetPosition(World));
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
                if (_selectedPair is { } pair && _cableLayer.Expanded)
                {
                    // Мастеринг: удаляем только выделенную пару каналов.
                    _graph.SetRouteMap(
                        route.FromId,
                        route.ToId,
                        route.Map.With(pair.FromChannel, pair.ToChannel, enabled: false));
                }
                else
                {
                    _graph.RemoveRoute(route.FromId, route.ToId);
                }
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
        _cableDrag = new CableDrag(origin.Element.Node.Id, origin.Output, origin.Channel);
        _cableLayer.TempFrom = origin.Output
            ? origin.Element.OutputPortCenter(origin.Channel)
            : origin.Element.InputPortCenter(origin.Channel);
        _cableLayer.TempTo = world;
        _cableLayer.TempValid = false;
        CaptureMouse();
        Cursor = Cursors.Cross;
        UpdatePortHighlights(world, _cableDrag);
        _cableLayer.InvalidateVisual();
    }

    private void StartReattach(PortHit origin, CableLayer.ReattachTarget target, Point world)
    {
        var otherEnd = _cableLayer.GetOtherEnd(target, origin.Output);
        if (otherEnd is null)
        {
            StartCableDrag(origin, world);
            return;
        }

        var otherNodeId = origin.Output ? target.Route.ToId : target.Route.FromId;
        _reattachDrag = new ReattachDrag(
            target,
            origin.Output,
            origin.Element.Node.Id,
            otherNodeId,
            otherEnd.Value);

        // Жила снята с порта: не рисуем её, тянем «свободный» конец от другого края.
        _cableLayer.DetachedStrand = target;
        _cableLayer.TempFrom = otherEnd;
        _cableLayer.TempTo = world;
        _cableLayer.TempValid = false;
        CaptureMouse();
        Cursor = Cursors.Cross;
        UpdateReattachHighlights(world, _reattachDrag);
        _cableLayer.InvalidateVisual();
    }

    private void FinishReattach(ReattachDrag drag, Point world)
    {
        var target = FindPort(world);
        EndCableDrag();

        if (target is null)
        {
            // Сброс в пустоту — отцепляем конец.
            DetachWire(drag.Target);
            return;
        }

        if (target.Output != drag.GrabbedAtOutput)
        {
            // Другая сторона — бессмыслица: просто возвращаем жилу на место.
            return;
        }

        Guid newFrom;
        Guid newTo;
        int? newFromChannel;
        int? newToChannel;
        if (drag.GrabbedAtOutput)
        {
            newFrom = target.Element.Node.Id;
            newFromChannel = _expandedView ? target.Channel : null;
            newTo = drag.OtherNodeId;
            newToChannel = drag.Target.ToChannel;
        }
        else
        {
            newFrom = drag.OtherNodeId;
            newFromChannel = drag.Target.FromChannel;
            newTo = target.Element.Node.Id;
            newToChannel = _expandedView ? target.Channel : null;
        }

        if (ValidateReattachEnds(newFrom, newTo) != RouteError.None)
        {
            return; // цикл/порты — отмена, жила остаётся где была
        }

        if (_expandedView &&
            _graph.ValidateChannelPair(
                newFrom, newFromChannel!.Value, newTo, newToChannel!.Value) != RouteError.None)
        {
            return;
        }

        ApplyReattach(drag.Target, newFrom, newFromChannel, newTo, newToChannel);
    }

    /// <summary>Вырезает старую связь и подключает конец заново (с сохранением гейна нового кабеля).</summary>
    private void ApplyReattach(
        CableLayer.ReattachTarget oldTarget,
        Guid newFrom,
        int? newFromChannel,
        Guid newTo,
        int? newToChannel)
    {
        var oldRoute = oldTarget.Route;
        var savedGain = oldRoute.Gain;
        var savedEnabled = oldRoute.Enabled;
        var savedMap = oldRoute.Map;

        // 1. Вырезаем старый конец.
        if (_expandedView && oldTarget.FromChannel is { } fromChannel && oldTarget.ToChannel is { } toChannel)
        {
            _graph.SetRouteMap(
                oldRoute.FromId,
                oldRoute.ToId,
                oldRoute.Map.With(fromChannel, toChannel, enabled: false));
        }
        else
        {
            _graph.RemoveRoute(oldRoute.FromId, oldRoute.ToId);
        }

        var oldRouteGone = _graph.FindRoute(oldRoute.FromId, oldRoute.ToId) is null;

        // 2. Подключаем к новому порту.
        Route? created;
        if (_expandedView)
        {
            _graph.AddRoute(newFrom, newFromChannel!.Value, newTo, newToChannel!.Value, out created);
        }
        else
        {
            _graph.AddRoute(newFrom, newTo, out created);
        }

        // Переехавший в одиночку кабель сохраняет настройки старого.
        if (created is not null && oldRouteGone)
        {
            _graph.SetRouteGain(created.FromId, created.ToId, savedGain);
            _graph.SetRouteEnabled(created.FromId, created.ToId, savedEnabled);

            // В бандл-режиме сохраняем карту каналов кабеля (с обрезкой под новые счётчики).
            if (!_expandedView)
            {
                var fromCount = _graph.FindNode(newFrom)?.ChannelCount ?? ChannelMap.MaxChannels;
                var toCount = _graph.FindNode(newTo)?.ChannelCount ?? ChannelMap.MaxChannels;
                var restricted = savedMap.Restrict(fromCount, toCount);
                if (!restricted.IsEmpty && restricted != created.Map)
                {
                    _graph.SetRouteMap(created.FromId, created.ToId, restricted);
                }
            }
        }

        if (created is not null)
        {
            SelectRoute(created, _expandedView && newFromChannel is { } fc && newToChannel is { } tc
                ? (fc, tc)
                : null);
        }
    }

    private void DetachWire(CableLayer.ReattachTarget target)
    {
        if (_expandedView && target.FromChannel is { } fromChannel && target.ToChannel is { } toChannel)
        {
            _graph.SetRouteMap(
                target.Route.FromId,
                target.Route.ToId,
                target.Route.Map.With(fromChannel, toChannel, enabled: false));
        }
        else
        {
            _graph.RemoveRoute(target.Route.FromId, target.Route.ToId);
        }
    }

    /// <summary>ValidateRoute, где дубль не ошибка — существующий маршрут просто расширится парой.</summary>
    private RouteError ValidateReattachEnds(Guid fromId, Guid toId)
    {
        var error = _graph.ValidateRoute(fromId, toId);
        return error == RouteError.Duplicate ? RouteError.None : error;
    }

    /// <summary>Подсветка валидных портов во время переподключения: цель — та же сторона, что и хват.</summary>
    private void UpdateReattachHighlights(Point world, ReattachDrag drag)
    {
        foreach (var element in _elements.Values)
        {
            element.ClearPortHighlights();
            var error = drag.GrabbedAtOutput
                ? ValidateReattachEnds(element.Node.Id, drag.OtherNodeId)
                : ValidateReattachEnds(drag.OtherNodeId, element.Node.Id);
            if (error == RouteError.None)
            {
                for (var channel = ChannelMap.Left; channel <= ChannelMap.Right; channel++)
                {
                    element.SetPortHighlight(drag.GrabbedAtOutput, channel, PortHighlight.Valid);
                }
            }
        }

        if (FindPort(world) is { } hovered && hovered.Output == drag.GrabbedAtOutput)
        {
            var error = drag.GrabbedAtOutput
                ? ValidateReattachEnds(hovered.Element.Node.Id, drag.OtherNodeId)
                : ValidateReattachEnds(drag.OtherNodeId, hovered.Element.Node.Id);
            var valid = error == RouteError.None;

            if (valid && _expandedView)
            {
                var fromChannel = drag.GrabbedAtOutput ? hovered.Channel : drag.Target.FromChannel;
                var toChannel = drag.GrabbedAtOutput ? drag.Target.ToChannel : hovered.Channel;
                valid = fromChannel is { } fc && toChannel is { } tc &&
                    _graph.ValidateChannelPair(
                        drag.GrabbedAtOutput ? hovered.Element.Node.Id : drag.OtherNodeId,
                        fc,
                        drag.GrabbedAtOutput ? drag.OtherNodeId : hovered.Element.Node.Id,
                        tc) == RouteError.None;
            }

            hovered.Element.SetPortHighlight(
                hovered.Output,
                hovered.Channel,
                valid ? PortHighlight.Valid : PortHighlight.Invalid);
            _cableLayer.TempValid = valid;
        }
        else
        {
            _cableLayer.TempValid = true;
        }
    }

    private void FinishCableDrag(CableDrag drag, Point world)
    {
        var target = FindPort(world);
        EndCableDrag();

        if (target is null || target.Output == drag.FromOutput)
        {
            return;
        }

        // Тянем из выхода → подключаем к конкретному входу, и наоборот.
        if (drag.Channel == NodeElement.BundleChannel)
        {
            // Бандл-порт: соединяем все каналы по диагонали 1:1.
            if (drag.FromOutput)
            {
                _graph.AddRoute(drag.NodeId, target.Element.Node.Id, out _);
            }
            else
            {
                _graph.AddRoute(target.Element.Node.Id, drag.NodeId, out _);
            }
        }
        else if (drag.FromOutput)
        {
            _graph.AddRoute(drag.NodeId, drag.Channel, target.Element.Node.Id, target.Channel, out _);
        }
        else
        {
            _graph.AddRoute(target.Element.Node.Id, target.Channel, drag.NodeId, drag.Channel, out _);
        }
    }

    private void EndCableDrag()
    {
        _cableDrag = null;
        _reattachDrag = null;
        _cableLayer.DetachedStrand = null;
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
                // Цель — противоположная сторона узла; любой канал принимается.
                for (var channel = ChannelMap.Left; channel <= ChannelMap.Right; channel++)
                {
                    element.SetPortHighlight(!drag.FromOutput, channel, PortHighlight.Valid);
                }
            }
        }

        if (FindPort(world) is { } hovered)
        {
            var correctSide = hovered.Output != drag.FromOutput;
            var error = correctSide ? Validate(drag, hovered.Element.Node.Id) : RouteError.SelfLoop;
            var hoverValid = error == RouteError.None;
            hovered.Element.SetPortHighlight(
                hovered.Output,
                hovered.Channel,
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
            _selectedPair = null;
            _cableLayer.SelectedRoute = null;
            _cableLayer.SelectedPair = null;
            _cableLayer.InvalidateVisual();
        }

        RefreshInspector();
    }

    private void SelectRoute(Route? route, (int FromChannel, int ToChannel)? pair = null)
    {
        _selectedNode?.SetSelected(false);
        _selectedNode = null;
        _selectedRoute = route;
        _selectedPair = route is not null && _expandedView ? pair : null;
        _cableLayer.SelectedRoute = route;
        _cableLayer.SelectedPair = _selectedPair;
        _cableLayer.InvalidateVisual();
        RefreshInspector();
    }

    private void ClearSelection() => SelectNode(null);

    // ===== Контекстные меню =====

    private void HandleContextMenu(MouseButtonEventArgs e)
    {
        var world = e.GetPosition(World);
        if (FindNodeAt(world) is { } node)
        {
            SelectNode(node);
            ShowNodeContextMenu();
        }
        else if (_cableLayer.HitTest(world) is { } hit)
        {
            SelectRoute(hit.Route, PairOf(hit));
            ShowRouteContextMenu(hit.Route);
        }
        else
        {
            ClearSelection();
            return;
        }

        e.Handled = true;
    }

    private static (int FromChannel, int ToChannel)? PairOf(CableLayer.WireHit hit) =>
        hit.FromChannel is { } from && hit.ToChannel is { } to ? (from, to) : null;

    private void ShowNodeContextMenu()
    {
        var menu = CreateContextMenu();

        var rename = new MenuItem { Header = "Переименовать" };
        rename.Click += (_, _) =>
        {
            NodeNameBox.Focus();
            NodeNameBox.SelectAll();
        };

        var delete = new MenuItem { Header = "Удалить" };
        delete.Click += (_, _) =>
        {
            if (_selectedNode is { } element)
            {
                _graph.RemoveNode(element.Node.Id);
            }
        };

        menu.Items.Add(rename);
        menu.Items.Add(new Separator());
        menu.Items.Add(delete);
        OpenContextMenu(menu);
    }

    private void ShowRouteContextMenu(Route route)
    {
        var menu = CreateContextMenu();

        var toggle = new MenuItem { Header = route.Enabled ? "Выключить кабель" : "Включить кабель" };
        toggle.Click += (_, _) =>
            _graph.SetRouteEnabled(route.FromId, route.ToId, !route.Enabled);

        var delete = new MenuItem { Header = "Удалить кабель" };
        delete.Click += (_, _) => _graph.RemoveRoute(route.FromId, route.ToId);

        menu.Items.Add(toggle);
        menu.Items.Add(new Separator());
        menu.Items.Add(delete);
        OpenContextMenu(menu);
    }

    private static ContextMenu CreateContextMenu() => new()
    {
        Placement = PlacementMode.MousePoint,
        MinWidth = 160,
    };

    private static void OpenContextMenu(ContextMenu menu)
    {
        menu.IsOpen = true;
    }

    // ===== Инструментарий =====

    private void OnPaletteAdd(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not string kind)
        {
            return;
        }

        // Центр видимой области холста в координатах мира.
        var center = ViewportToWorld(new Point(Viewport.ActualWidth / 2, Viewport.ActualHeight / 2));
        AddNodeAt(kind, center);
    }

    private void AddNodeAt(string kind, Point worldPosition)
    {
        AudioNode node = kind switch
        {
            "Capture" => _graph.AddNode("Захват", NodeKind.Source),
            "Loopback" => _graph.AddNode("Звук системный", NodeKind.Source),
            "Bus" => _graph.AddNode("Шина", NodeKind.Bus),
            "Sink" => _graph.AddNode("Вывод", NodeKind.Sink),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

        var deviceId = kind switch
        {
            "Capture" => DeviceSpec.DefaultCapture,
            "Loopback" => DeviceSpec.DefaultLoopback,
            "Sink" => DeviceSpec.DefaultRender,
            _ => null,
        };

        if (deviceId is not null)
        {
            _graph.SetNodeDevice(node.Id, deviceId);
        }

        _graph.SetNodePosition(node.Id, worldPosition.X, worldPosition.Y);

        if (_elements.TryGetValue(node.Id, out var element))
        {
            SelectNode(element);
        }
    }

    private Point ViewportToWorld(Point viewport) => new(
        (viewport.X - _translate.X) / _scale,
        (viewport.Y - _translate.Y) / _scale);

    // ===== Жизненный цикл вкладки =====

    private void OnGraphViewLoaded(object sender, RoutedEventArgs e)
    {
        _expandedView = AppServices.Settings.IsExpandedView;
        _syncingMode = true;
        try
        {
            SingleModeButton.IsChecked = !_expandedView;
            MasteringModeButton.IsChecked = _expandedView;
        }
        finally
        {
            _syncingMode = false;
        }

        ApplyViewMode();
        RenderTicker.Subscribe(OnRenderTick);
    }

    private void OnGraphViewUnloaded(object sender, RoutedEventArgs e)
    {
        RenderTicker.Unsubscribe(OnRenderTick);
    }

    // ===== Режимы отображения =====

    private void OnViewModeChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _syncingMode)
        {
            return;
        }

        _syncingMode = true;
        try
        {
            var mastering = MasteringModeButton.IsChecked == true;
            // Режим ровно один: снимаем галку с противоположного (без рекурсии).
            if (mastering)
            {
                SingleModeButton.IsChecked = false;
            }
            else
            {
                MasteringModeButton.IsChecked = false;
            }

            _expandedView = mastering;
            AppServices.Settings.GraphViewMode = mastering ? "mastering" : "single";
            AppServices.Settings.Save();
            ApplyViewMode();
        }
        finally
        {
            _syncingMode = false;
        }
    }

    private void OnViewModeUnchecked(object sender, RoutedEventArgs e)
    {
        if (_syncingMode || !IsLoaded)
        {
            return;
        }

        // Нельзя выключить оба режима — возвращаем галку.
        if (sender is ToggleButton button)
        {
            button.IsChecked = true;
        }
    }

    private void ApplyViewMode()
    {
        _cableLayer.Expanded = _expandedView;
        foreach (var element in _elements.Values)
        {
            element.SetViewMode(_expandedView);
        }

        if (!_expandedView)
        {
            _selectedPair = null;
            _cableLayer.SelectedPair = null;
        }

        _cableLayer.InvalidateVisual();
        RefreshInspector();
    }

    // ===== Мини-метры на узлах =====

    private void OnRenderTick(double now, double dt)
    {
        foreach (var element in _elements.Values)
        {
            element.Meter.Value = NormalizePeak(AppServices.Engine.GetPeak(element.Node.Id));
        }
    }

    private static float NormalizePeak(float peak)
    {
        var db = Decibels.ToDb(peak);
        if (float.IsNegativeInfinity(db) || db <= Decibels.MinDb)
        {
            return 0f;
        }

        // −60..0 дБ → 0..1
        return Math.Clamp((db - Decibels.MinDb) / -Decibels.MinDb, 0f, 1f);
    }

    // ===== Инспектор =====

    private sealed record DeviceChoice(string Name, string? Value);

    private void RefreshInspector()
    {
        if (_selectedNode is { } element && _graph.FindNode(element.Node.Id) is { } node)
        {
            ShowNodeInspector(node);
            return;
        }

        if (_selectedRoute is { } route && _graph.Routes.Contains(route))
        {
            ShowRouteInspector(route);
            return;
        }

        HideInspector();
    }

    private void HideInspector()
    {
        InspectorPanel.Visibility = Visibility.Collapsed;
        NodeSection.Visibility = Visibility.Collapsed;
        RouteSection.Visibility = Visibility.Collapsed;
        _deviceNodeId = Guid.Empty;
    }

    private void ShowNodeInspector(AudioNode node)
    {
        _syncing = true;
        try
        {
            InspectorTitle.Text = node.Kind switch
            {
                NodeKind.Source => "Источник",
                NodeKind.Bus => "Шина",
                _ => "Назначение",
            };
            NodeSection.Visibility = Visibility.Visible;
            RouteSection.Visibility = Visibility.Collapsed;
            InspectorPanel.Visibility = Visibility.Visible;

            if (!NodeNameBox.IsKeyboardFocused && NodeNameBox.Text != node.Name)
            {
                NodeNameBox.Text = node.Name;
            }

            SyncDeviceChoices(node);

            // Каналы: количество (моно/стерео) + имена.
            ChannelMonoButton.IsChecked = node.ChannelCount == 1;
            ChannelStereoButton.IsChecked = node.ChannelCount == 2;
            RebuildChannelNameBoxes(node);

            var db = Decibels.ToDb(node.Gain);
            NodeGainSlider.Value = float.IsNegativeInfinity(db) || db < Decibels.MinDb
                ? Decibels.MinDb
                : db;
            UpdateGainText(NodeGainText, db);
            NodeMuteBox.IsChecked = node.Mute;
            NodeSoloBox.IsChecked = node.Solo;

            // Слоты эффектов — только у шин.
            SlotsSection.Visibility = node.Kind == NodeKind.Bus ? Visibility.Visible : Visibility.Collapsed;
            if (node.Kind == NodeKind.Bus)
            {
                RebuildSlotRows(node);
            }
        }
        finally
        {
            _syncing = false;
        }
    }

    // ===== Слоты-вставки эффектов =====

    private void RebuildSlotRows(AudioNode node)
    {
        SlotsHost.Children.Clear();
        for (var i = 0; i < node.Slots.Count; i++)
        {
            var index = i;
            var slot = node.Slots[i];

            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 2, 0, 0),
            };

            var enable = new CheckBox
            {
                IsChecked = slot.Enabled,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0),
                ToolTip = "Слот включён (без него — проход)",
            };
            enable.Checked += (_, _) => SetSlotEnabled(node, index, true);
            enable.Unchecked += (_, _) => SetSlotEnabled(node, index, false);
            row.Children.Add(enable);

            var title = new TextBlock
            {
                Text = string.IsNullOrEmpty(slot.Name)
                    ? System.IO.Path.GetFileNameWithoutExtension(slot.Path)
                    : slot.Name,
                ToolTip = slot.Path + " · " + slot.PluginId,
                VerticalAlignment = VerticalAlignment.Center,
                MaxWidth = 120,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 0, 6, 0),
                Opacity = slot.Enabled ? 1.0 : 0.5,
            };
            row.Children.Add(title);

            row.Children.Add(BuildSlotButton("↑", "Выше", index > 0, (_, _) => MoveSlot(node, index, -1)));
            row.Children.Add(BuildSlotButton("↓", "Ниже", index < node.Slots.Count - 1, (_, _) => MoveSlot(node, index, +1)));
            row.Children.Add(BuildSlotButton("✕", "Убрать", true, (_, _) => RemoveSlot(node, index)));
            row.Children.Add(BuildSlotButton("✎", "Редактор плагина", true, (_, _) => OpenSlotEditor(node, index)));

            SlotsHost.Children.Add(row);
        }
    }

    private static Button BuildSlotButton(string content, string tooltip, bool enabled, RoutedEventHandler onClick)
    {
        var button = new Button
        {
            Content = content,
            ToolTip = tooltip,
            IsEnabled = enabled,
            Width = 22,
            Height = 20,
            Padding = new Thickness(0),
            Margin = new Thickness(2, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        button.Click += onClick;
        return button;
    }

    private void SetSlotEnabled(AudioNode node, int index, bool enabled)
    {
        if (_syncing)
        {
            return;
        }

        _graph.SetSlotEnabled(node.Id, index, enabled);
    }

    private void MoveSlot(AudioNode node, int index, int delta)
    {
        if (_syncing)
        {
            return;
        }

        var target = index + delta;
        if (target < 0 || target >= node.Slots.Count)
        {
            return;
        }

        _graph.MoveSlot(node.Id, index, target);
    }

    private void RemoveSlot(AudioNode node, int index)
    {
        if (_syncing)
        {
            return;
        }

        _graph.RemoveSlot(node.Id, index);
    }

    private void OpenSlotEditor(AudioNode node, int index)
    {
        if (_syncing || index < 0 || index >= node.Slots.Count)
        {
            return;
        }

        var slot = node.Slots[index];
        var instance = AppServices.Engine.GetSlotInstance(node.Id, index);
        if (instance is not Parrhesia.Plugins.IPluginEditor editor)
        {
            MessageBox.Show(
                Window.GetWindow(this),
                instance is null
                    ? "Слот не загружен — проверьте путь к плагину."
                    : "Этот формат пока не поддерживает редактор.",
                "Редактор",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        if (!editor.SupportsEditor)
        {
            MessageBox.Show(
                Window.GetWindow(this),
                "У плагина нет редактора.",
                "Редактор",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        if (editor.IsOpen)
        {
            return; // редактор этого слота уже открыт
        }

        var window = new PluginEditorWindow(editor, slot.Name)
        {
            Owner = Window.GetWindow(this),
        };
        window.Show();
    }

    private void OnAddSlotClick(object sender, RoutedEventArgs e)
    {
        if (_syncing)
        {
            return;
        }

        if (_selectedNode is not { } element ||
            _graph.FindNode(element.Node.Id) is not { } node ||
            node.Kind != NodeKind.Bus)
        {
            return;
        }

        var picker = new PluginPickerWindow
        {
            Owner = Window.GetWindow(this),
        };
        if (picker.ShowDialog() == true && picker.Selected is { } descriptor)
        {
            _graph.AddSlot(node.Id, new PluginSlot
            {
                Format = descriptor.Format,
                Path = descriptor.Path,
                PluginId = descriptor.PluginId,
                Name = descriptor.Name,
            });
        }
    }

    private void ShowRouteInspector(Route route)
    {
        _syncing = true;
        try
        {
            InspectorTitle.Text = "Кабель";
            NodeSection.Visibility = Visibility.Collapsed;
            RouteSection.Visibility = Visibility.Visible;
            InspectorPanel.Visibility = Visibility.Visible;

            var from = _graph.FindNode(route.FromId)?.Name ?? "?";
            var to = _graph.FindNode(route.ToId)?.Name ?? "?";
            RouteEndpoints.Text = $"«{from}» → «{to}»";

            // Чекбоксы — только для пар, влезающих в счётчики каналов узлов.
            var fromCount = _graph.FindNode(route.FromId)?.ChannelCount ?? 2;
            var toCount = _graph.FindNode(route.ToId)?.ChannelCount ?? 2;
            ChLeftLeft.Visibility = 0 < fromCount && 0 < toCount ? Visibility.Visible : Visibility.Collapsed;
            ChLeftRight.Visibility = 0 < fromCount && 1 < toCount ? Visibility.Visible : Visibility.Collapsed;
            ChRightLeft.Visibility = 1 < fromCount && 0 < toCount ? Visibility.Visible : Visibility.Collapsed;
            ChRightRight.Visibility = 1 < fromCount && 1 < toCount ? Visibility.Visible : Visibility.Collapsed;

            ChLeftLeft.IsChecked = route.Map.Has(ChannelMap.Left, ChannelMap.Left);
            ChLeftRight.IsChecked = route.Map.Has(ChannelMap.Left, ChannelMap.Right);
            ChRightLeft.IsChecked = route.Map.Has(ChannelMap.Right, ChannelMap.Left);
            ChRightRight.IsChecked = route.Map.Has(ChannelMap.Right, ChannelMap.Right);

            var db = Decibels.ToDb(route.Gain);
            RouteGainSlider.Value = float.IsNegativeInfinity(db) || db < Decibels.MinDb
                ? Decibels.MinDb
                : db;
            UpdateGainText(RouteGainText, db);
            RouteEnabledBox.IsChecked = route.Enabled;
        }
        finally
        {
            _syncing = false;
        }
    }

    /// <summary>
    /// Список устройств для узла. Перестраивается только при смене узла:
    /// перечисление устройств — COM-вызов, его нельзя делать на каждой тяге гейна.
    /// </summary>
    private void SyncDeviceChoices(AudioNode node)
    {
        if (node.Kind == NodeKind.Bus)
        {
            NodeDeviceBox.IsEnabled = false;
            if (NodeDeviceBox.ItemsSource is not null)
            {
                NodeDeviceBox.ItemsSource = null;
            }

            _deviceNodeId = node.Id;
            return;
        }

        if (_deviceNodeId != node.Id || NodeDeviceBox.ItemsSource is not List<DeviceChoice>)
        {
            var choices = BuildDeviceChoices(node);
            NodeDeviceBox.ItemsSource = choices;
            _deviceNodeId = node.Id;
        }

        if (NodeDeviceBox.ItemsSource is List<DeviceChoice> items)
        {
            var target = items.FirstOrDefault(c => c.Value == node.DeviceId) ?? items[0];
            if (!ReferenceEquals(NodeDeviceBox.SelectedItem, target))
            {
                NodeDeviceBox.SelectedItem = target;
            }
        }

        NodeDeviceBox.IsEnabled = true;
    }

    private List<DeviceChoice> BuildDeviceChoices(AudioNode node)
    {
        var choices = new List<DeviceChoice> { new("(не привязан)", null) };

        if (node.Kind == NodeKind.Source)
        {
            choices.Add(new("По умолчанию (захват)", DeviceSpec.DefaultCapture));
            choices.Add(new("Loopback: по умолчанию", DeviceSpec.DefaultLoopback));
            foreach (var device in AppServices.Devices.GetDevices(DeviceFlow.Capture))
            {
                choices.Add(new(device.Name, device.Id));
            }

            foreach (var device in AppServices.Devices.GetDevices(DeviceFlow.Render))
            {
                choices.Add(new("Loopback: " + device.Name, "loopback:" + device.Id));
            }
        }
        else
        {
            choices.Add(new("По умолчанию (вывод)", DeviceSpec.DefaultRender));
            choices.Add(new("Parrhesia Out (виртуальный)", DeviceSpec.VirtualParrhesia));
            foreach (var device in AppServices.Devices.GetDevices(DeviceFlow.Render))
            {
                choices.Add(new(device.Name, device.Id));
            }
        }

        return choices;
    }

    private static void UpdateGainText(TextBlock target, float db) =>
        target.Text = float.IsNegativeInfinity(db) ? "−∞ дБ" : db.ToString("0.0") + " дБ";

    private void CommitNodeName()
    {
        if (_syncing || _selectedNode is not { } element)
        {
            return;
        }

        var name = NodeNameBox.Text.Trim();
        if (name.Length > 0 && name != element.Node.Name)
        {
            _graph.RenameNode(element.Node.Id, name);
        }
    }

    private void OnNodeNameCommit(object sender, RoutedEventArgs e) => CommitNodeName();

    // ===== Каналы узла =====

    private void OnChannelCountChanged(object sender, RoutedEventArgs e)
    {
        if (_syncing || _selectedNode is not { } element)
        {
            return;
        }

        _syncing = true;
        try
        {
            // Ровно одно состояние: снимаем галку с противоположного.
            if (ReferenceEquals(sender, ChannelStereoButton))
            {
                ChannelMonoButton.IsChecked = false;
            }
            else
            {
                ChannelStereoButton.IsChecked = false;
            }
        }
        finally
        {
            _syncing = false;
        }

        var count = ChannelStereoButton.IsChecked == true ? 2 : 1;
        if (_graph.FindNode(element.Node.Id) is { } node && node.ChannelCount != count)
        {
            _graph.SetNodeChannels(node.Id, count);
        }
    }

    private void OnChannelCountUnchecked(object sender, RoutedEventArgs e)
    {
        if (_syncing || !IsLoaded)
        {
            return;
        }

        // Нельзя выключить оба варианта — возвращаем галку.
        if (sender is ToggleButton button)
        {
            button.IsChecked = true;
        }
    }

    private void RebuildChannelNameBoxes(AudioNode node)
    {
        ChannelNamesHost.Children.Clear();
        for (var channel = 0; channel < node.ChannelCount; channel++)
        {
            var index = channel;
            var box = new TextBox
            {
                Text = node.ChannelNames[channel],
                Margin = new Thickness(0, 0, 0, 6),
            };
            box.LostFocus += (_, _) => CommitChannelName(node.Id, index, box);
            box.KeyDown += (_, args) =>
            {
                if (args.Key != Key.Enter)
                {
                    return;
                }

                CommitChannelName(node.Id, index, box);
                Keyboard.ClearFocus();
                args.Handled = true;
            };

            ChannelNamesHost.Children.Add(new TextBlock
            {
                Text = $"Канал {channel + 1}",
                FontSize = 11,
                Foreground = (System.Windows.Media.Brush)FindResource("Brush.TextFaint"),
                Margin = new Thickness(0, 0, 0, 2),
            });
            ChannelNamesHost.Children.Add(box);
        }
    }

    private void CommitChannelName(Guid nodeId, int channel, TextBox box)
    {
        if (_syncing)
        {
            return;
        }

        var name = box.Text.Trim();
        if (name.Length == 0)
        {
            box.Text = _graph.FindNode(nodeId)?.ChannelNames[channel] ?? (channel + 1).ToString();
            return;
        }

        if (_graph.FindNode(nodeId) is { } node && node.ChannelNames[channel] != name)
        {
            _graph.SetNodeChannelName(nodeId, channel, name);
        }
    }

    private void OnNodeNameKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        CommitNodeName();
        Keyboard.ClearFocus();
        e.Handled = true;
    }

    private void OnNodeDeviceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || _selectedNode is not { } element)
        {
            return;
        }

        if (NodeDeviceBox.SelectedItem is DeviceChoice choice &&
            _graph.FindNode(element.Node.Id) is { } node &&
            node.DeviceId != choice.Value)
        {
            _graph.SetNodeDevice(node.Id, choice.Value);
        }
    }

    private void OnNodeGainChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncing)
        {
            return;
        }

        UpdateGainText(NodeGainText, (float)e.NewValue);
        if (_selectedNode is { } element)
        {
            _graph.SetNodeGain(element.Node.Id, Decibels.FromDb((float)e.NewValue));
        }
    }

    private void OnNodeMuteChanged(object sender, RoutedEventArgs e)
    {
        if (_syncing || _selectedNode is not { } element)
        {
            return;
        }

        _graph.SetNodeMute(element.Node.Id, NodeMuteBox.IsChecked == true);
    }

    private void OnNodeSoloChanged(object sender, RoutedEventArgs e)
    {
        if (_syncing || _selectedNode is not { } element)
        {
            return;
        }

        _graph.SetNodeSolo(element.Node.Id, NodeSoloBox.IsChecked == true);
    }

    private void OnDeleteNodeClick(object sender, RoutedEventArgs e)
    {
        if (_selectedNode is { } element)
        {
            _graph.RemoveNode(element.Node.Id);
        }
    }

    private void OnChannelChanged(object sender, RoutedEventArgs e)
    {
        if (_syncing || _selectedRoute is not { } route)
        {
            return;
        }

        byte bits = 0;
        if (ChLeftLeft.IsChecked == true)
        {
            bits |= 1 << 0;
        }

        if (ChLeftRight.IsChecked == true)
        {
            bits |= 1 << 1;
        }

        if (ChRightLeft.IsChecked == true)
        {
            bits |= 1 << 2;
        }

        if (ChRightRight.IsChecked == true)
        {
            bits |= 1 << 3;
        }

        _graph.SetRouteMap(route.FromId, route.ToId, new ChannelMap(bits));
    }

    private void OnRouteGainChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncing)
        {
            return;
        }

        UpdateGainText(RouteGainText, (float)e.NewValue);
        if (_selectedRoute is { } route)
        {
            _graph.SetRouteGain(route.FromId, route.ToId, Decibels.FromDb((float)e.NewValue));
        }
    }

    private void OnRouteEnabledChanged(object sender, RoutedEventArgs e)
    {
        if (_syncing || _selectedRoute is not { } route)
        {
            return;
        }

        _graph.SetRouteEnabled(route.FromId, route.ToId, RouteEnabledBox.IsChecked == true);
    }

    private void OnDeleteRouteClick(object sender, RoutedEventArgs e)
    {
        if (_selectedRoute is { } route)
        {
            _graph.RemoveRoute(route.FromId, route.ToId);
        }
    }

    // ===== Хит-тесты =====

    /// <summary>Ближайший порт в радиусе. В бандл-режиме — по одному порту на сторону.</summary>
    private PortHit? FindPort(Point world)
    {
        var radius = PortHitRadius * _cableLayer.InverseScale;
        PortHit? best = null;
        var bestDistance = radius;

        void Consider(Point portCenter, NodeElement element, bool output, int channel)
        {
            var distance = Distance(portCenter, world);
            if (distance <= bestDistance)
            {
                bestDistance = distance;
                best = new PortHit(element, output, channel);
            }
        }

        foreach (var element in _elements.Values)
        {
            if (!_expandedView)
            {
                if (element.Node.HasInput)
                {
                    Consider(
                        element.InputPortCenter(NodeElement.BundleChannel),
                        element,
                        output: false,
                        NodeElement.BundleChannel);
                }

                if (element.Node.HasOutput)
                {
                    Consider(
                        element.OutputPortCenter(NodeElement.BundleChannel),
                        element,
                        output: true,
                        NodeElement.BundleChannel);
                }

                continue;
            }

            for (var channel = 0; channel < element.Node.ChannelCount; channel++)
            {
                if (element.Node.HasInput)
                {
                    Consider(element.InputPortCenter(channel), element, output: false, channel);
                }

                if (element.Node.HasOutput)
                {
                    Consider(element.OutputPortCenter(channel), element, output: true, channel);
                }
            }
        }

        return best;
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
        if (_suppressChanges)
        {
            return;
        }

        // Перетаскивание: позиция уже применена к элементу — достаточно кабелей.
        if (change.Kind == GraphChangeKind.NodeChanged &&
            _draggedNode is not null &&
            change.Node?.Id == _draggedNode.Node.Id)
        {
            _cableLayer.InvalidateVisual();
            return;
        }

        Rebuild();

        if (_selectedRoute is { } route && !_graph.Routes.Contains(route))
        {
            _selectedRoute = null;
            _selectedPair = null;
            _cableLayer.SelectedRoute = null;
            _cableLayer.SelectedPair = null;
        }

        if (_selectedNode is { } node && _graph.FindNode(node.Node.Id) is null)
        {
            node.SetSelected(false);
            _selectedNode = null;
        }

        _cableLayer.InvalidateVisual();
        RefreshInspector();
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
                if (node.X is { } placedX && node.Y is { } placedY)
                {
                    // Позиция могла измениться извне (пресет, инструментарий).
                    existing.SetPosition(placedX, placedY);
                }

                continue;
            }

            var element = new NodeElement(node);
            element.SetViewMode(_expandedView);
            element.ToggleBypassRequested += n => _graph.SetNodeBypass(n.Id, !n.Bypassed);
            element.ToggleMuteRequested += n => _graph.SetNodeMute(n.Id, !n.Mute);
            element.SizeChanged += (_, _) => _cableLayer.InvalidateVisual();

            double x;
            double y;
            if (node.X is { } modelX && node.Y is { } modelY)
            {
                x = modelX;
                y = modelY;
            }
            else
            {
                // Ещё не расставлен — назначаем позицию по колонкам; она сразу
                // живёт в модели и попадёт в пресеты.
                var fallback = DefaultPosition(node.Kind);
                x = fallback.X;
                y = fallback.Y;

                _suppressChanges = true;
                try
                {
                    _graph.SetNodePosition(node.Id, x, y);
                }
                finally
                {
                    _suppressChanges = false;
                }
            }

            element.SetPosition(x, y);
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

        // Считаем уже размещённые элементы: при пакетном построении
        // модель содержит все узлы сразу, и счёт по модели сложил бы колонку в стопку.
        var row = _elements.Values.Count(e => e.Node.Kind == kind);
        return new Point(60 + column * 300, 60 + row * 96);
    }
}
