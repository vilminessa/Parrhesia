using Parrhesia.Core.Graph;

namespace Parrhesia.Core.Tests.Graph;

public class ChannelMapTests
{
    [Fact]
    public void Diagonal_EqualCounts_ConnectsStraightPairs()
    {
        var map = ChannelMap.Diagonal(2, 2);

        Assert.True(map.Has(0, 0));
        Assert.True(map.Has(1, 1));
        Assert.False(map.Has(0, 1));
        Assert.False(map.Has(1, 0));
        Assert.False(map.IsEmpty);
    }

    [Fact]
    public void Diagonal_MonoToStereo_DuplicatesToAllChannels()
    {
        var map = ChannelMap.Diagonal(1, 2);

        Assert.True(map.Has(0, 0));
        Assert.True(map.Has(0, 1));
        Assert.False(map.Has(1, 0));
    }

    [Fact]
    public void Diagonal_StereoToMono_FeedsBothChannels()
    {
        var map = ChannelMap.Diagonal(2, 1);

        Assert.True(map.Has(0, 0));
        Assert.True(map.Has(1, 0));
        Assert.False(map.Has(0, 1));
    }

    [Fact]
    public void Pair_ContainsOnlyThatChannelPair()
    {
        var map = ChannelMap.Pair(0, 1);

        Assert.True(map.Has(0, 1));
        Assert.False(map.Has(0, 0));
        Assert.False(map.Has(1, 1));
        Assert.False(map.Has(1, 0));
    }

    [Fact]
    public void With_EnablesAndDisablesPairs()
    {
        var map = ChannelMap.Diagonal(2, 2)
            .With(0, 1, enabled: true)
            .With(0, 0, enabled: false);

        Assert.False(map.Has(0, 0));
        Assert.True(map.Has(0, 1));
        Assert.True(map.Has(1, 1));
    }

    [Fact]
    public void With_DisablingEverything_GivesEmpty()
    {
        var map = ChannelMap.Diagonal(2, 2)
            .With(0, 0, enabled: false)
            .With(1, 1, enabled: false);

        Assert.True(map.IsEmpty);
    }

    [Fact]
    public void Restrict_DropsPairsOutsideChannelCounts()
    {
        // (0,0) остаётся при моно-назначении, (0,1) — отбрасывается.
        var map = ChannelMap.Pair(0, 0).With(0, 1, enabled: true);
        var restricted = map.Restrict(1, 1);

        Assert.Equal(ChannelMap.Pair(0, 0), restricted);
        Assert.True(map.Restrict(1, 1).Has(0, 0));
    }

    [Fact]
    public void FromLegacyBits_MigratesV1Layout()
    {
        // v1: бит = from*2 + to; 9 = биты0 и3 = L→L, R→R.
        var migrated = ChannelMap.FromLegacyBits(9);

        Assert.Equal(ChannelMap.Diagonal(2, 2), migrated);
        Assert.Equal(ChannelMap.Pair(0, 1), ChannelMap.FromLegacyBits(2));
    }

    [Fact]
    public void Pairs_EnumeratesInOrder()
    {
        var map = ChannelMap.Pair(1, 0).With(0, 0, enabled: true);

        var pairs = map.Pairs().ToList();

        Assert.Equal(new[] { (0, 0), (1, 0) }, pairs);
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, -1)]
    [InlineData(8, 0)]
    [InlineData(0, 8)]
    public void InvalidChannel_Throws(int from, int to)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ChannelMap.Pair(from, to));
        Assert.Throws<ArgumentOutOfRangeException>(() => ChannelMap.Diagonal(2, 2).Has(from, to));
    }

    [Fact]
    public void BitsRoundTrip_ThroughConstructor()
    {
        var map = new ChannelMap(ChannelMap.Diagonal(2, 2).Bits);

        Assert.Equal(ChannelMap.Diagonal(2, 2), map);
        Assert.Equal(ChannelMap.Pair(0, 1), new ChannelMap(ChannelMap.Pair(0, 1).Bits));
    }

    [Fact]
    public void ToString_ShowsOneBasedChannelNumbers()
    {
        Assert.Equal("1→1|2→2", ChannelMap.Diagonal(2, 2).ToString());
        Assert.Equal("1→2", ChannelMap.Pair(0, 1).ToString());
    }
}
