using Parrhesia.Core.Graph;

namespace Parrhesia.Core.Tests.Graph;

public class ChannelMapTests
{
    [Fact]
    public void Direct_ConnectsStraightStereoPair()
    {
        var map = ChannelMap.Direct;

        Assert.True(map.Has(ChannelMap.Left, ChannelMap.Left));
        Assert.True(map.Has(ChannelMap.Right, ChannelMap.Right));
        Assert.False(map.Has(ChannelMap.Left, ChannelMap.Right));
        Assert.False(map.Has(ChannelMap.Right, ChannelMap.Left));
        Assert.False(map.IsEmpty);
    }

    [Fact]
    public void Pair_ContainsOnlyThatChannelPair()
    {
        var map = ChannelMap.Pair(ChannelMap.Left, ChannelMap.Right);

        Assert.True(map.Has(ChannelMap.Left, ChannelMap.Right));
        Assert.False(map.Has(ChannelMap.Left, ChannelMap.Left));
        Assert.False(map.Has(ChannelMap.Right, ChannelMap.Right));
        Assert.False(map.Has(ChannelMap.Right, ChannelMap.Left));
    }

    [Fact]
    public void With_EnablesAndDisablesPairs()
    {
        var map = ChannelMap.Direct
            .With(ChannelMap.Left, ChannelMap.Right, enabled: true)
            .With(ChannelMap.Left, ChannelMap.Left, enabled: false);

        Assert.False(map.Has(ChannelMap.Left, ChannelMap.Left));
        Assert.True(map.Has(ChannelMap.Left, ChannelMap.Right));
        Assert.True(map.Has(ChannelMap.Right, ChannelMap.Right));
    }

    [Fact]
    public void With_DisablingEverything_GivesEmpty()
    {
        var map = ChannelMap.Direct
            .With(ChannelMap.Left, ChannelMap.Left, enabled: false)
            .With(ChannelMap.Right, ChannelMap.Right, enabled: false);

        Assert.True(map.IsEmpty);
    }

    [Fact]
    public void Pairs_EnumeratesInLrOrder()
    {
        var map = ChannelMap.Pair(ChannelMap.Right, ChannelMap.Left)
            .With(ChannelMap.Left, ChannelMap.Left, enabled: true);

        var pairs = map.Pairs().ToList();

        Assert.Equal(new[] { (0, 0), (1, 0) }, pairs);
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, -1)]
    [InlineData(2, 0)]
    [InlineData(0, 2)]
    public void InvalidChannel_Throws(int from, int to)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ChannelMap.Pair(from, to));
        Assert.Throws<ArgumentOutOfRangeException>(() => ChannelMap.Direct.Has(from, to));
    }

    [Fact]
    public void BitsRoundTrip_ThroughConstructor()
    {
        var map = new ChannelMap(ChannelMap.Direct.Bits);

        Assert.Equal(ChannelMap.Direct, map);
        Assert.Equal(ChannelMap.Pair(0, 1), new ChannelMap(ChannelMap.Pair(0, 1).Bits));
    }

    [Fact]
    public void InvalidBits_RejectedByConstructor()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ChannelMap(0x10));
    }

    [Fact]
    public void ToString_ShowsPairs()
    {
        Assert.Equal("L→L|R→R", ChannelMap.Direct.ToString());
        Assert.Equal("L→R", ChannelMap.Pair(0, 1).ToString());
    }
}
