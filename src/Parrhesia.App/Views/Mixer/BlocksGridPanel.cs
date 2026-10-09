using System.Windows;
using System.Windows.Controls;

namespace Parrhesia.App.Views.Mixer;

/// <summary>
/// Сетка блоков-зон микшера: блоки текут слева направо, вниз переносятся
/// только при нехватке ширины (см. <see cref="BlocksGridLayout"/>).
/// Ширина блока не константа: каждый измеряется с ограничением «остаток
/// строки» — зона занимает ровно столько горизонтали, сколько нужно её
/// пультам, а их внутренний wrap уводит вниз только при реальной нехватке.
/// </summary>
internal sealed class BlocksGridPanel : Panel
{
    /// <summary>Зазор между блоками (совпадает с Margin контейнера блока).</summary>
    public const double Gap = 10;

    private double[] _x = [];
    private double[] _y = [];
    private Size[] _desired = [];

    protected override Size MeasureOverride(Size availableSize)
    {
        var unlimited = double.IsInfinity(availableSize.Width) || availableSize.Width <= 0;
        var width = unlimited ? double.PositiveInfinity : availableSize.Width;
        var height = double.IsInfinity(availableSize.Height) || availableSize.Height <= 0
            ? double.PositiveInfinity
            : availableSize.Height;

        // Блок ограничиваем полной шириной строки: его внутренний WrapPanel
        // сам переносит пульты, если в зоне не хватает даже всей ширины.
        _desired = new Size[Children.Count];
        for (var i = 0; i < Children.Count; i++)
        {
            Children[i].Measure(new Size(width, height));
            _desired[i] = Children[i].DesiredSize;
        }

        if (Children.Count == 0)
        {
            _x = [];
            _y = [];
            return new Size(0, 0);
        }

        (_x, _y, var totalWidth, var totalHeight) =
            BlocksGridLayout.Pack(_desired, unlimited ? double.PositiveInfinity : availableSize.Width);

        return new Size(totalWidth, totalHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (_desired.Length != Children.Count)
        {
            return finalSize;
        }

        for (var i = 0; i < Children.Count; i++)
        {
            Children[i].Arrange(new Rect(_x[i], _y[i], _desired[i].Width, _desired[i].Height));
        }

        return finalSize;
    }
}
