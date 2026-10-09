using Parrhesia.App.Views.Mixer;

namespace Parrhesia.App.Tests.Views;

/// <summary>
/// M-волна «модульность»: жадная masonry-упаковка блоков-зон в колонки
/// (порядок зон сохраняется, высота ≈ самая глубокая колонка).
/// </summary>
public class MasonryLayoutTests
{
    [Fact]
    public void SingleColumn_StacksInOrder()
    {
        var (columns, tops) = MasonryLayout.Assign([100, 50, 30], columnCount: 1);

        Assert.All(columns, c => Assert.Equal(0, c));
        Assert.Equal(new double[] { 0, 100, 150 }, tops);
    }

    [Fact]
    public void TwoColumns_GreedyPlacesIntoShortest()
    {
        // i0 → колонка0 (обе0, левее); i1 → колонка1 (0 < 100); i2 → колонка0 (100 < 200); i3 → колонка0 (150 < 200).
        var (columns, tops) = MasonryLayout.Assign([100, 200, 50, 50], columnCount: 2);

        Assert.Equal(new[] { 0, 1, 0, 0 }, columns);
        Assert.Equal(new double[] { 0, 0, 100, 150 }, tops);
    }

    [Fact]
    public void Tie_GoesToLeftmostColumn()
    {
        var (columns, _) = MasonryLayout.Assign([100, 100], columnCount: 3);

        Assert.Equal(0, columns[0]);
        Assert.Equal(1, columns[1]); // высоты0==0 → левая свободная (колонка1)
    }

    [Fact]
    public void MoreColumnsThanBlocks_AllFit()
    {
        var (columns, tops) = MasonryLayout.Assign([80, 40], columnCount: 5);

        Assert.Equal(new[] { 0, 1 }, columns);
        Assert.Equal(new double[] { 0, 0 }, tops);
    }

    [Fact]
    public void Empty_NoBlocks_NoPositions()
    {
        var (columns, tops) = MasonryLayout.Assign([], columnCount: 3);

        Assert.Empty(columns);
        Assert.Empty(tops);
    }

    [Fact]
    public void InvalidColumnCount_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => MasonryLayout.Assign([10], columnCount: 0));
    }

    [Fact]
    public void TallBlock_DoesNotForceWholeRow()
    {
        // Стопка дала бы высоту100+900+50+50=1100; masonry — глубина колонок.
        double[] heights = [100, 900, 50, 50];
        var (columns, tops) = MasonryLayout.Assign(heights, columnCount: 2);

        var bottoms = new Dictionary<int, double>();
        for (var i = 0; i < columns.Length; i++)
        {
            var bottom = tops[i] + heights[i];
            bottoms[columns[i]] = Math.Max(bottoms.GetValueOrDefault(columns[i]), bottom);
        }

        var deepest = bottoms.Values.Max();
        Assert.True(deepest < 1100, $"deepest={deepest}");
        Assert.True(deepest >= 900, $"deepest={deepest}");
    }
}
