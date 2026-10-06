using System.Windows.Media;

namespace Parrhesia.App.Controls;

/// <summary>
/// Цвета сегментных метров. Единственный источник правды для метров:
/// кисти замораживаются сразу и переиспользуются без аллокаций в кадре.
/// </summary>
internal static class MeterPalette
{
    public static readonly Brush Off = Frozen("#FF232830");
    public static readonly Brush Low = Frozen("#FF3DDC84");
    public static readonly Brush Mid = Frozen("#FFFFC53D");
    public static readonly Brush High = Frozen("#FFFF4D4D");
    public static readonly Brush Peak = Frozen("#FFF6D8");

    private static Brush Frozen(string argb)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(argb)!);
        brush.Freeze();
        return brush;
    }
}
