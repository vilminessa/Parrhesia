namespace Parrhesia.App.Views.Mixer;

/// <summary>
/// Жадная masonry-упаковка блоков-зон в колонки: каждый следующий блок
/// кладётся в самую низкую колонку (при равенстве — левее), порядок зон
/// сохраняется внутри колонок. Чистая функция — тестируется без UI.
/// </summary>
internal static class MasonryLayout
{
    /// <summary>
    /// Раскладка блоков.
    /// </summary>
    /// <param name="heights">Высоты блоков в порядке следования зон.</param>
    /// <param name="columnCount">Число колонок (≥ 1).</param>
    /// <returns>Индекс колонки и вертикальная позиция (top) каждого блока.</returns>
    public static (int[] Column, double[] Top) Assign(IReadOnlyList<double> heights, int columnCount)
    {
        ArgumentNullException.ThrowIfNull(heights);
        if (columnCount < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(columnCount), "Число колонок должно быть ≥ 1.");
        }

        var columns = new int[heights.Count];
        var tops = new double[heights.Count];
        var columnHeight = new double[columnCount];

        for (var i = 0; i < heights.Count; i++)
        {
            var best = 0;
            for (var c = 1; c < columnCount; c++)
            {
                // Строго меньше — при равенстве выигрывает левая колонка.
                if (columnHeight[c] < columnHeight[best])
                {
                    best = c;
                }
            }

            columns[i] = best;
            tops[i] = columnHeight[best];
            columnHeight[best] += heights[i];
        }

        return (columns, tops);
    }
}
