using System.Diagnostics;
using System.Windows.Media;

namespace Parrhesia.App.Rendering;

/// <summary>
/// Единый тактовый сигнал для всех анимаций интерфейса поверх CompositionTarget.Rendering.
/// Один кадр — одно событие для всех подписчиков. Подписки считаются:
/// первый подписчик включает тик, последний выключает.
/// </summary>
public static class RenderTicker
{
    private static readonly object Sync = new();

    private static Action<double, double>? _frame;
    private static int _subscribers;
    private static long _lastTimestamp;
    private static double _now;

    /// <summary>Подписчик получает (время_сек, дельта_сек) по монотонным часам Stopwatch.</summary>
    public static void Subscribe(Action<double, double> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);

        var start = false;
        lock (Sync)
        {
            _frame += handler;
            if (++_subscribers == 1)
            {
                _lastTimestamp = Stopwatch.GetTimestamp();
                _now = 0;
                start = true;
            }
        }

        if (start)
        {
            CompositionTarget.Rendering += OnRendering;
        }
    }

    public static void Unsubscribe(Action<double, double> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);

        var stop = false;
        lock (Sync)
        {
            _frame -= handler;
            if (--_subscribers == 0)
            {
                stop = true;
            }
        }

        if (stop)
        {
            CompositionTarget.Rendering -= OnRendering;
        }
    }

    private static void OnRendering(object? sender, EventArgs e)
    {
        double now;
        double delta;
        lock (Sync)
        {
            var timestamp = Stopwatch.GetTimestamp();
            delta = (timestamp - _lastTimestamp) / (double)Stopwatch.Frequency;
            _lastTimestamp = timestamp;
            _now += delta;
            now = _now;
        }

        _frame?.Invoke(now, delta);
    }
}
