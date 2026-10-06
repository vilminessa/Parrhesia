using System.Windows;
using System.Windows.Media;
using Parrhesia.Core.Graph;

namespace Parrhesia.App.Views.Graph;

/// <summary>
/// Слой отрисовки кабелей (координаты мира): маршрут рисуется одной жилой
/// на каждую соединённую пару каналов (стерео = две параллельные жилы,
/// кросс = пересечение к конкретным портам). Hit-test — по расстоянию
/// до любой жилы маршрута. Один проход OnRender.
/// </summary>
internal sealed class CableLayer : FrameworkElement
{
    private const int HitTestSamples = 40;

    private static readonly Brush CableBrush = ResolveBrush("Brush.TextDim", "#FF8B93A1");
    private static readonly Brush SelectedBrush = ResolveBrush("Brush.Accent", "#FFFFB020");
    private static readonly Brush DisabledBrush = ResolveBrush("Brush.TextFaint", "#FF5C6472");
    private static readonly Brush TempBrush = ResolveBrush("Brush.Cyan", "#FF35D0C8");
    private static readonly Pen InvalidTempPen = CreatePen(ResolveBrush("Brush.Danger", "#FFFF5A52"));

    public CableLayer()
    {
        IsHitTestVisible = false;
    }

    public IReadOnlyList<Route>? Routes { get; set; }

    public Route? SelectedRoute { get; set; }

    /// <summary>Конец временного кабеля у порта-источника, координаты мира.</summary>
    public Point? TempFrom { get; set; }

    /// <summary>Курсор во время перетаскивания, координаты мира.</summary>
    public Point? TempTo { get; set; }

    public bool TempValid { get; set; } = true;

    /// <summary>1/масштаб: радиус hit-test кабеля задаётся в экранных пикселях.</summary>
    public double InverseScale { get; set; } = 1;

    public Func<Guid, int, Point?>? GetOutputPoint { get; set; }

    public Func<Guid, int, Point?>? GetInputPoint { get; set; }

    /// <summary>Кабель под точкой (координаты мира) или null.</summary>
    public Route? HitTest(Point worldPoint)
    {
        if (Routes is null || GetOutputPoint is null || GetInputPoint is null)
        {
            return null;
        }

        var radius = 6 * InverseScale;
        var radiusSq = radius * radius;
        Route? best = null;
        var bestDistance = radiusSq;

        foreach (var route in Routes)
        {
            foreach (var (from, to) in Strands(route))
            {
                var (c1, c2) = ControlPoints(from, to);
                for (var i = 0; i <= HitTestSamples; i++)
                {
                    var p = Bezier(from, c1, c2, to, i / (double)HitTestSamples);
                    var dx = p.X - worldPoint.X;
                    var dy = p.Y - worldPoint.Y;
                    var distance = dx * dx + dy * dy;
                    if (distance < bestDistance)
                    {
                        bestDistance = distance;
                        best = route;
                    }
                }
            }
        }

        return best;
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (Routes is not null && GetOutputPoint is not null && GetInputPoint is not null)
        {
            foreach (var route in Routes)
            {
                if (ReferenceEquals(route, SelectedRoute))
                {
                    continue;
                }

                DrawRoute(dc, route, route.Enabled ? CableBrush : DisabledBrush, 1.75);
            }

            if (SelectedRoute is not null)
            {
                DrawRoute(dc, SelectedRoute, SelectedBrush, 2.5);
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

    /// <summary>Жилы маршрута: одна на соединённую пару каналов.</summary>
    private IEnumerable<(Point From, Point To)> Strands(Route route)
    {
        if (GetOutputPoint is null || GetInputPoint is null)
        {
            yield break;
        }

        foreach (var (fromChannel, toChannel) in route.Map.Pairs())
        {
            var from = GetOutputPoint(route.FromId, fromChannel);
            var to = GetInputPoint(route.ToId, toChannel);
            if (from is { } f && to is { } t)
            {
                yield return (f, t);
            }
        }
    }

    private static Pen CreatePen(Brush brush)
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

    private static Pen TempPen()
    {
        var pen = new Pen(TempBrush, 1.75)
        {
            DashStyle = new DashStyle(new[] { 4.0, 3.0 }, 0),
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
        };
        pen.Freeze();
        return pen;
    }

    private void DrawRoute(DrawingContext dc, Route route, Brush brush, double thickness)
    {
        var pen = new Pen(brush, thickness)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
        };
        pen.Freeze();

        foreach (var (from, to) in Strands(route))
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
