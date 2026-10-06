using System.Windows;
using System.Windows.Media;

namespace Parrhesia.App.Controls;

/// <summary>
/// Сегментный LED-метр (снизу вверх): фон, зелёный/жёлтый/красный диапазоны
/// и отдельный яркий сегмент пик-холда.
/// Тупой вид: значения <see cref="Value"/> и <see cref="Peak"/> задаются снаружи
/// (0..1, где 1 — верх шкалы); декей пика — забота владельца на своём тике.
/// Рисование через OnRender, ноль аллокаций в кадре.
/// </summary>
public sealed class LedMeterControl : FrameworkElement
{
    public const int SegmentCount = 24;

    private const double SegmentGap = 2.0;

    private float _value;
    private float _peak;

    public LedMeterControl()
    {
        SnapsToDevicePixels = true;
        Focusable = false;
    }

    /// <summary>Текущий уровень, 0..1.</summary>
    public float Value
    {
        get => _value;
        set
        {
            var v = Math.Clamp(value, 0f, 1f);
            if (v.Equals(_value))
            {
                return;
            }

            _value = v;
            InvalidateVisual();
        }
    }

    /// <summary>Уровень пик-холда, 0..1. Меньше <see cref="Value"/> — игнорируется.</summary>
    public float Peak
    {
        get => _peak;
        set
        {
            var v = Math.Clamp(value, 0f, 1f);
            if (v.Equals(_peak))
            {
                return;
            }

            _peak = v;
            InvalidateVisual();
        }
    }

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        dc.DrawRoundedRectangle(MeterPalette.Off, null, new Rect(0, 0, width, height), 3, 3);

        var totalGap = SegmentGap * (SegmentCount - 1);
        var segmentHeight = (height - totalGap) / SegmentCount;
        if (segmentHeight <= 0)
        {
            return;
        }

        var lit = (int)MathF.Round(_value * SegmentCount);
        var peakIndex = _peak <= 0f
            ? -1
            : Math.Clamp((int)MathF.Round(_peak * SegmentCount), 1, SegmentCount);

        var y = height;
        for (var i = 1; i <= SegmentCount; i++)
        {
            y -= segmentHeight;
            var rect = new Rect(0, y, width, segmentHeight);

            if (i == peakIndex)
            {
                dc.DrawRectangle(MeterPalette.Peak, null, rect);
            }
            else if (i <= lit)
            {
                dc.DrawRectangle(ColorForSegment(i), null, rect);
            }

            y -= SegmentGap;
        }
    }

    private static Brush ColorForSegment(int segment)
    {
        var ratio = (float)segment / SegmentCount;
        if (ratio <= 0.6f)
        {
            return MeterPalette.Low;
        }

        return ratio <= 0.85f ? MeterPalette.Mid : MeterPalette.High;
    }
}
