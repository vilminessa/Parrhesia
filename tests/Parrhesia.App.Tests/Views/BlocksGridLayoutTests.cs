using System.Windows;
using Parrhesia.App.Views.Mixer;

namespace Parrhesia.App.Tests.Views;

/// <summary>
/// L-волна «горизонтальная сетка»: блоки идут слева направо, вниз — только
/// при нехватке ширины; высота ряда = самый высокий блок ряда.
/// </summary>
public class BlocksGridLayoutTests
{
    private static Size S(double w, double h) => new(w, h);

    [Fact]
    public void AllFitInOneRow_PositionsAreSequential()
    {
        var (xs, ys, width, height) = BlocksGridLayout.Pack(
            [S(100, 50), S(120, 80), S(100, 40)], availableWidth: 500);

        Assert.Equal(new double[] { 0, 100, 220 }, xs);
        Assert.All(ys, y => Assert.Equal(0, y));
        Assert.Equal(320, width);   // сумма ширин
        Assert.Equal(80, height);   // высота самого высокого
    }

    [Fact]
    public void Overflow_WrapsToNextRow()
    {
        // Ряд1: [100][200] =300; остаток200 < 250 → третий блок уходит вниз.
        var (xs, ys, width, height) = BlocksGridLayout.Pack(
            [S(100, 60), S(200, 90), S(250, 70)], availableWidth: 500);

        Assert.Equal(new double[] { 0, 100, 0 }, xs);      // третий — в начало нового ряда
        Assert.Equal(new double[] { 0, 0, 90 }, ys);       // ряд2 после самого высокого блока ряда1
        Assert.Equal(300, width);                          // ширина = самый широкий ряд (300 vs 250)
        Assert.Equal(160, height);                         //90 + 70
    }

    [Fact]
    public void RowHeight_IsTallestOfRow_NextRowStartsAfterIt()
    {
        var (_, ys, _, height) = BlocksGridLayout.Pack(
            [S(100, 300), S(100, 50), S(100, 60)], availableWidth: 200);

        Assert.Equal(new double[] { 0, 0, 300 }, ys); // второй в ряд1, третий — ряд2 после высоты300
        Assert.Equal(360, height);
    }

    [Fact]
    public void ExactFit_DoesNotWrap()
    {
        var (xs, ys, _, _) = BlocksGridLayout.Pack(
            [S(250, 40), S(250, 40)], availableWidth: 500);

        Assert.Equal(new double[] { 0, 250 }, xs);
        Assert.All(ys, y => Assert.Equal(0, y));
    }

    [Fact]
    public void SingleOversizedBlock_ClampedToAvailableWidth()
    {
        var (xs, _, width, height) = BlocksGridLayout.Pack([S(800, 120)], availableWidth: 500);

        Assert.Equal(0, xs[0]);
        Assert.Equal(500, width);   // ширина панели, не блока (прокрутка — фолбэк ленты)
        Assert.Equal(120, height);
    }

    [Fact]
    public void UnlimitedWidth_AllInOneRow()
    {
        var (_, ys, width, _) = BlocksGridLayout.Pack(
            [S(300, 10), S(300, 20), S(300, 30)], double.PositiveInfinity);

        Assert.All(ys, y => Assert.Equal(0, y));
        Assert.Equal(900, width);
    }

    [Fact]
    public void Empty_NoBlocks()
    {
        var (xs, ys, width, height) = BlocksGridLayout.Pack([], availableWidth: 500);

        Assert.Empty(xs);
        Assert.Empty(ys);
        Assert.Equal(0, width);
        Assert.Equal(0, height);
    }
}
