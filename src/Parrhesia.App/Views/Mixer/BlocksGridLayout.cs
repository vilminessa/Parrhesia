using System.Windows;

namespace Parrhesia.App.Views.Mixer;

/// <summary>
/// Wrap-упаковка блоков сетки микшера: блоки идут слева направо; вниз —
/// только когда следующий блок не помещается в остаток строки (L-волна:
/// «по горизонтали, перенос при нехватке места»). Размеры включают Margin
/// блоков ( gap между блоками = их Margin справа/снизу ).
/// </summary>
internal static class BlocksGridLayout
{
    /// <summary>
    /// Раскладка блоков в ряд(ах).
    /// </summary>
    /// <param name="sizes">Размеры блоков в порядке следования зон (с Margin).</param>
    /// <param name="availableWidth">Доступная ширина; Infinity/≤0 — без ограничения.</param>
    /// <returns>Позиции (x/y), итоговые ширина и высота сетки.</returns>
    public static (double[] X, double[] Y, double TotalWidth, double TotalHeight) Pack(
        IReadOnlyList<Size> sizes,
        double availableWidth)
    {
        ArgumentNullException.ThrowIfNull(sizes);

        var unlimited = double.IsInfinity(availableWidth) || availableWidth <= 0;
        var xs = new double[sizes.Count];
        var ys = new double[sizes.Count];

        var x = 0.0;
        var rowY = 0.0;
        var rowHeight = 0.0;
        var widest = 0.0;

        for (var i = 0; i < sizes.Count; i++)
        {
            var (width, height) = (sizes[i].Width, sizes[i].Height);

            // Перенос: в новый ряд — только при нехватке горизонтали.
            if (!unlimited && x > 0 && x + width > availableWidth)
            {
                rowY += rowHeight;
                x = 0;
                rowHeight = 0;
            }

            xs[i] = x;
            ys[i] = rowY;
            x += width;
            rowHeight = Math.Max(rowHeight, height);
            widest = Math.Max(widest, x);
        }

        var totalHeight = rowY + rowHeight;
        var totalWidth = unlimited ? widest : Math.Min(availableWidth, widest);
        return (xs, ys, totalWidth, totalHeight);
    }
}
