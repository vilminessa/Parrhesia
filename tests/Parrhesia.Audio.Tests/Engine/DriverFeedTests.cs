using Parrhesia.Audio.Engine;

namespace Parrhesia.Audio.Tests.Engine;

public class DriverFeedTests
{
    [Fact]
    public void FrameBytes_MatchesContract()
    {
        // Канон фида: PCM32 @48k stereo — кадр8 байт (как в feed.h).
        Assert.Equal(8, DriverFeed.FrameBytes);
        Assert.Equal(48000, DriverFeed.Rate);
        Assert.Equal(2, DriverFeed.Channels);
        Assert.Equal(32, DriverFeed.Bits);
    }

    [Fact]
    public void ConvertBlock_ZeroStaysZero()
    {
        var src = new float[4];
        var dst = new byte[src.Length * sizeof(int)];
        DriverFeed.ConvertBlock(src, dst);

        Assert.All(dst, b => Assert.Equal(0, b));
    }

    [Fact]
    public void ConvertBlock_HalfScale()
    {
        var src = new[] { 0.5f };
        var dst = new byte[sizeof(int)];
        DriverFeed.ConvertBlock(src, dst);

        Assert.Equal(1073741824, BitConverter.ToInt32(dst));
    }

    [Fact]
    public void ConvertBlock_ClipsOutOfRange()
    {
        var src = new[] { 2f, -2f };
        var dst = new byte[src.Length * sizeof(int)];
        DriverFeed.ConvertBlock(src, dst);

        Assert.Equal(int.MaxValue, BitConverter.ToInt32(dst, 0));
        Assert.Equal(int.MinValue, BitConverter.ToInt32(dst, 4));
    }

    [Fact]
    public void ConvertBlock_NegativeOneGivesMinValue()
    {
        var src = new[] { -1f };
        var dst = new byte[sizeof(int)];
        DriverFeed.ConvertBlock(src, dst);

        Assert.Equal(int.MinValue, BitConverter.ToInt32(dst));
    }

    [Fact]
    public void ConvertBlock_ShortDestination_Throws()
    {
        var src = new float[4];
        var dst = new byte[8]; // мало: нужно16
        Assert.Throws<ArgumentException>(() => DriverFeed.ConvertBlock(src, dst));
    }
}
