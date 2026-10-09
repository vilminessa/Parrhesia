using System.Windows;
using System.Windows.Controls;

namespace Parrhesia.App.Views.Mixer;

/// <summary>
/// Masonry-панель блоков-зон микшера: блоки идут в фиксированном порядке зон,
/// но кладутся в самую низкую колонку — высота ленты ≈ самая глубокая колонка,
/// а не сумма всех секций (M-волна «модульность, место по надобности»).
/// Блок измеряется при фиксированной ширине (<see cref="BlockWidth"/> + Gap).
/// </summary>
internal sealed class MasonryPanel : Panel
{
    /// <summary>Ширина контента блока-зоны (px); полный шаг блока = BlockWidth + Gap.</summary>
    public const double BlockWidth = 250;

    /// <summary>Зазор между блоками (совпадает с Margin контейнера блока).</summary>
    public const double Gap = 10;

    private int[] _columns = [];
    private double[] _tops = [];
    private Size[] _desired = [];

    protected override Size MeasureOverride(Size availableSize)
    {
        var availableWidth = availableSize.Width;
        var infinite = double.IsInfinity(availableWidth) || availableWidth <= 0;

        var columns = Children.Count == 0 || infinite
            ? 1
            : Math.Clamp((int)((availableWidth + Gap) / (BlockWidth + Gap)), 1, Children.Count);

        var childConstraint = new Size(BlockWidth + Gap, double.PositiveInfinity);
        _desired = new Size[Children.Count];
        for (var i = 0; i < Children.Count; i++)
        {
            Children[i].Measure(childConstraint);
            _desired[i] = Children[i].DesiredSize;
        }

        var heights = new List<double>(_desired.Length);
        foreach (var size in _desired)
        {
            heights.Add(size.Height);
        }

        (_columns, _tops) = MasonryLayout.Assign(heights, columns);

        if (Children.Count == 0)
        {
            return new Size(0, 0);
        }

        var neededWidth = columns * (BlockWidth + Gap) - Gap;
        var width = infinite ? neededWidth : Math.Min(availableWidth, neededWidth);

        var height = 0.0;
        for (var i = 0; i < _desired.Length; i++)
        {
            height = Math.Max(height, _tops[i] + _desired[i].Height);
        }

        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (_desired.Length != Children.Count)
        {
            // Дети изменились между measure и arrange (крайний случай) — доверяем системе лайаута.
            return finalSize;
        }

        var pitch = BlockWidth + Gap;
        for (var i = 0; i < Children.Count; i++)
        {
            Children[i].Arrange(new Rect(_columns[i] * pitch, _tops[i], _desired[i].Width, _desired[i].Height));
        }

        return finalSize;
    }
}
