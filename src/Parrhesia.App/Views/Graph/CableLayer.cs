using System.Windows;
using System.Windows.Media;
using Parrhesia.Core.Graph;

namespace Parrhesia.App.Views.Graph;

/// <summary>
/// Слой отрисовки кабелей (координаты мира).
/// Мастеринг-режим: жила на каждую пару каналов. Однонодовый: одна
/// линия-бандл на маршрут. Hit-test возвращает жилу (маршрут + пару).
/// </summary>
internal sealed class CableLayer : FrameworkElement
{
    private const int HitTestSamples = 40;

    private static readonly Brush CableBrush = ResolveBrush("Brush.TextDim", "#FF8B93A1");
    private static readonly Brush SelectedBrush = ResolveBrush("Brush.Accent", "#FFFFB020");
    private static readonly Brush DisabledBrush = ResolveBrush("Brush.TextFaint", "#FF5C6472");
    private static readonly Brush TempBrush = ResolveBrush("Brush.Cyan", "#FF35D0C8");
    private static readonly Pen InvalidTempPen = CreateDashPen(ResolveBrush("Brush.Danger", "#FFFF5A52"));

    /// <summary>Порог «кабель живёт» (U2): |сигнал| источника выше — жила подсвечивается.</summary>
    private const float HotThreshold = 0.01f;

    /// <summary>Пик узла-источника маршрута (U2: видно, где реально идёт звук).</summary>
    public Func<Guid, float>? GetPeak { get; set; }

    public CableLayer()
    {
        IsHitTestVisible = false;
    }

    /// <summary>Выделенная жила: маршрут + пара каналов (каналы null — весь кабель/бандл).</summary>
    public sealed record WireHit(Route Route, int? FromChannel, int? ToChannel);

    /// <summary>Найденный прикреплённый конец жилы (для перетаскивания-переподключения).</summary>
    public sealed record ReattachTarget(Route Route, int? FromChannel, int? ToChannel);

    public IReadOnlyList<Route>? Routes { get; set; }

    public Route? SelectedRoute { get; set; }

    /// <summary>Выделенная пара каналов внутри <see cref="SelectedRoute"/> (мастеринг-режим).</summary>
    public (int FromChannel, int ToChannel)? SelectedPair { get; set; }

    /// <summary>
    /// Жила, «снятая» с порта во время перетаскивания-переподключения:
    /// не рисуется, пока тянется.
    /// </summary>
    public ReattachTarget? DetachedStrand { get; set; }

    /// <summary>Мастеринг-режим: жила на каждую пару. false — одна линия на маршрут.</summary>
    public bool Expanded { get; set; }

    /// <summary>Конец временного кабеля у порта-источника, координаты мира.</summary>
    public Point? TempFrom { get; set; }

    /// <summary>Курсор во время перетаскивания, координаты мира.</summary>
    public Point? TempTo { get; set; }

    public bool TempValid { get; set; } = true;

    /// <summary>1/масштаб: радиус hit-test кабеля задаётся в экранных пикселях.</summary>
    public double InverseScale { get; set; } = 1;

    public Func<Guid, int, Point?>? GetOutputPoint { get; set; }

    public Func<Guid, int, Point?>? GetInputPoint { get; set; }

    /// <summary>Жила под точкой (координаты мира) или null.</summary>
    public WireHit? HitTest(Point worldPoint)    {
        if (Routes is null || GetOutputPoint is null || GetInputPoint is null)
        {
            return null;
        }

        var radiusSq = Math.Pow(6 * InverseScale, 2);

        if (!Expanded)
        {
            foreach (var route in Routes)
            {
                var from = GetOutputPoint(route.FromId, NodeElement.BundleChannel);
                var to = GetInputPoint(route.ToId, NodeElement.BundleChannel);
                if (from is { } f && to is { } t && CurveHits(f, t, worldPoint, radiusSq))
                {
                    return new WireHit(route, null, null);
                }
            }

            return null;
        }

        foreach (var route in Routes)
        {
            foreach (var (fromChannel, toChannel) in route.Map.Pairs())
            {
                var from = GetOutputPoint(route.FromId, fromChannel);
                var to = GetInputPoint(route.ToId, toChannel);
                if (from is { } f && to is { } t && CurveHits(f, t, worldPoint, radiusSq))
                {
                    return new WireHit(route, fromChannel, toChannel);
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Ближайший прикреплённый конец жилы к порту узла (для захвата и
    /// переподключения). В бандл-режиме возвращается весь маршрут.
    /// </summary>
    public ReattachTarget? FindAttachedWire(Point worldPoint, NodeElement element, bool atOutput, int channel, double radius)
    {
        if (Routes is null || GetOutputPoint is null || GetInputPoint is null)
        {
            return null;
        }

        var radiusSq = radius * radius;
        ReattachTarget? best = null;
        var bestDistance = radiusSq;

        void Consider(Route route, int? fromChannel, int? toChannel, Point from, Point to)
        {
            var (c1, c2) = ControlPoints(from, to);
            for (var i = 0; i <= HitTestSamples; i++)
            {
                var p = Bezier(from, c1, c2, to, i / (double)HitTestSamples);
                var dx = p.X - worldPoint.X;
                var dy = p.Y - worldPoint.Y;
                var distance = (dx * dx) + (dy * dy);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = new ReattachTarget(route, fromChannel, toChannel);
                }
            }
        }

        foreach (var route in Routes)
        {
            if (!Expanded)
            {
                if (atOutput)
                {
                    if (route.FromId == element.Node.Id)
                    {
                        var from = GetOutputPoint(route.FromId, NodeElement.BundleChannel);
                        var to = GetInputPoint(route.ToId, NodeElement.BundleChannel);
                        if (from is { } f && to is { } t)
                        {
                            Consider(route, null, null, f, t);
                        }
                    }
                }
                else if (route.ToId == element.Node.Id)
                {
                    var from = GetOutputPoint(route.FromId, NodeElement.BundleChannel);
                    var to = GetInputPoint(route.ToId, NodeElement.BundleChannel);
                    if (from is { } f && to is { } t)
                    {
                        Consider(route, null, null, f, t);
                    }
                }

                continue;
            }

            foreach (var (fromChannel, toChannel) in route.Map.Pairs())
            {
                var matchesPort = atOutput
                    ? route.FromId == element.Node.Id && fromChannel == channel
                    : route.ToId == element.Node.Id && toChannel == channel;
                if (!matchesPort)
                {
                    continue;
                }

                var from = GetOutputPoint(route.FromId, fromChannel);
                var to = GetInputPoint(route.ToId, toChannel);
                if (from is { } f && to is { } t)
                {
                    Consider(route, fromChannel, toChannel, f, t);
                }
            }
        }

        return best;
    }

    /// <summary>Точка противоположного конца жилы (не двигается при переподключении).</summary>
    public Point? GetOtherEnd(ReattachTarget target, bool grabbedAtOutput)
    {
        if (GetOutputPoint is null || GetInputPoint is null)
        {
            return null;
        }

        if (!Expanded || target.FromChannel is null || target.ToChannel is null)
        {
            return grabbedAtOutput
                ? GetInputPoint(target.Route.ToId, NodeElement.BundleChannel)
                : GetOutputPoint(target.Route.FromId, NodeElement.BundleChannel);
        }

        return grabbedAtOutput
            ? GetInputPoint(target.Route.ToId, target.ToChannel.Value)
            : GetOutputPoint(target.Route.FromId, target.FromChannel.Value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (Routes is not null && GetOutputPoint is not null && GetInputPoint is not null)
        {
            foreach (var route in Routes)
            {
                // Снятая во время перетаскивания бандл-жила не рисуется.
                if (!Expanded && DetachedStrand is { } detached && ReferenceEquals(detached.Route, route))
                {
                    continue;
                }

                if (ReferenceEquals(route, SelectedRoute))
                {
                    continue;
                }

                if (route.Enabled && (GetPeak?.Invoke(route.FromId) ?? 0f) > HotThreshold)
                {
                    // U2: по кабелю реально идёт звук — подсвечиваем (cyan, жирнее).
                    DrawRoute(dc, route, TempBrush, 2.25);
                }
                else
                {
                    DrawRoute(dc, route, route.Enabled ? CableBrush : DisabledBrush, 1.75);
                }
            }

            if (SelectedRoute is not null)
            {
                if (SelectedPair is { } pair && Expanded)
                {
                    // Выделена одна жила: остальные обычным цветом, она — акцентом.
                    var background = SelectedRoute.Enabled ? CableBrush : DisabledBrush;
                    DrawRoute(dc, SelectedRoute, background, 1.75, skipPair: pair);
                    DrawWire(dc, SelectedRoute, pair.FromChannel, pair.ToChannel, SelectedBrush, 2.5);
                }
                else
                {
                    DrawRoute(dc, SelectedRoute, SelectedBrush, 2.5);
                }
            }
        }

        if (TempFrom is { } from && TempTo is { } to)
        {
            var (c1, c2) = ControlPoints(from, to);
            var geometry = new StreamGeometry();
            using (var context = geometry.Open())
            {
                context.BeginFigure(from, isFilled: false, isClosed: false);
                context.BezierTo(c1, c2, to, isStroked: true, isSmoothJoin: false);
            }

            geometry.Freeze();
            dc.DrawGeometry(null, TempValid ? TempPen() : InvalidTempPen, geometry);
        }
    }

    /// <summary>Жилы маршрута: в бандл-режиме — одна линия, иначе по паре каналов.</summary>
    private IEnumerable<(Point From, Point To, int FromChannel, int ToChannel)> Strands(Route route)
    {
        if (GetOutputPoint is null || GetInputPoint is null)
        {
            yield break;
        }

        if (!Expanded)
        {
            var from = GetOutputPoint(route.FromId, NodeElement.BundleChannel);
            var to = GetInputPoint(route.ToId, NodeElement.BundleChannel);
            if (from is { } f && to is { } t)
            {
                yield return (f, t, 0, 0);
            }

            yield break;
        }

        foreach (var (fromChannel, toChannel) in route.Map.Pairs())
        {
            var from = GetOutputPoint(route.FromId, fromChannel);
            var to = GetInputPoint(route.ToId, toChannel);
            if (from is { } f && to is { } t)
            {
                yield return (f, t, fromChannel, toChannel);
            }
        }
    }

    private static bool CurveHits(Point from, Point to, Point worldPoint, double radiusSq)
    {
        var (c1, c2) = ControlPoints(from, to);
        for (var i = 0; i <= HitTestSamples; i++)
        {
            var p = Bezier(from, c1, c2, to, i / (double)HitTestSamples);
            var dx = p.X - worldPoint.X;
            var dy = p.Y - worldPoint.Y;
            if ((dx * dx) + (dy * dy) < radiusSq)
            {
                return true;
            }
        }

        return false;
    }

    private static Pen CreateStrokePen(Brush brush, double thickness)
    {
        var pen = new Pen(brush, thickness)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
        };
        pen.Freeze();
        return pen;
    }

    private static Pen CreateDashPen(Brush brush)
    {
        var pen = new Pen(brush, 1.75)
        {
            DashStyle = new DashStyle(new[] { 4.0, 3.0 }, 0),
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
        };
        pen.Freeze();
        return pen;
    }

    private static Pen TempPen() => CreateDashPen(TempBrush);

    private void DrawRoute(
        DrawingContext dc,
        Route route,
        Brush brush,
        double thickness,
        (int FromChannel, int ToChannel)? skipPair = null)
    {
        // Пара, снятая с порта во время перетаскивания, не рисуется.
        if (DetachedStrand is { } detached &&
            ReferenceEquals(detached.Route, route) &&
            detached.FromChannel is { } detachedFrom &&
            detached.ToChannel is { } detachedTo)
        {
            skipPair = (detachedFrom, detachedTo);
        }

        var pen = CreateStrokePen(brush, thickness);
        foreach (var (from, to, fromChannel, toChannel) in Strands(route))
        {
            if (skipPair is { } skip && skip.FromChannel == fromChannel && skip.ToChannel == toChannel)
            {
                continue;
            }

            DrawCurve(dc, pen, from, to);
        }
    }

    private void DrawWire(DrawingContext dc, Route route, int fromChannel, int toChannel, Brush brush, double thickness)
    {
        if (GetOutputPoint is null || GetInputPoint is null)
        {
            return;
        }

        var from = GetOutputPoint(route.FromId, fromChannel);
        var to = GetInputPoint(route.ToId, toChannel);
        if (from is { } f && to is { } t)
        {
            DrawCurve(dc, CreateStrokePen(brush, thickness), f, t);
        }
    }

    private static void DrawCurve(DrawingContext dc, Pen pen, Point from, Point to)
    {
        var (c1, c2) = ControlPoints(from, to);
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(from, isFilled: false, isClosed: false);
            context.BezierTo(c1, c2, to, isStroked: true, isSmoothJoin: false);
        }

        geometry.Freeze();
        dc.DrawGeometry(null, pen, geometry);
    }

    private static (Point C1, Point C2) ControlPoints(Point from, Point to)
    {
        var distance = Math.Abs(to.X - from.X);
        var offset = Math.Clamp(distance * 0.5, 40, 160);
        return (new Point(from.X + offset, from.Y), new Point(to.X - offset, to.Y));
    }

    private static Point Bezier(Point p0, Point c1, Point c2, Point p3, double t)
    {
        var u = 1 - t;
        var x = (u * u * u * p0.X) + (3 * u * u * t * c1.X) + (3 * u * t * t * c2.X) + (t * t * t * p3.X);
        var y = (u * u * u * p0.Y) + (3 * u * u * t * c1.Y) + (3 * u * t * t * c2.Y) + (t * t * t * p3.Y);
        return new Point(x, y);
    }

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
