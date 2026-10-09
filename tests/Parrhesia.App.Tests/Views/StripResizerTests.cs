using Parrhesia.App.Views.Mixer;

namespace Parrhesia.App.Tests.Views;

/// <summary>Ресайз пульта за нижний край (H-волна): кламп диапазона и снап к шагу.</summary>
public class StripResizerTests
{
    [Theory]
    [InlineData(-50, 160)]
    [InlineData(0, 160)]
    [InlineData(100, 160)]
    [InlineData(160, 160)]
    [InlineData(200, 200)]
    [InlineData(360, 360)]
    [InlineData(424, 424)]
    [InlineData(9999, 424)]
    public void Clamp_StaysInRange(double input, double expected)
    {
        Assert.Equal(expected, MixerStrip.ClampCardHeight(input));
    }

    [Theory]
    [InlineData(200, 200)]
    [InlineData(160, 160)]
    [InlineData(424, 424)]
    [InlineData(203, 200)]
    [InlineData(161, 160)]
    [InlineData(204, 208)]
    [InlineData(207, 208)]
    [InlineData(197, 200)]
    public void Snap_RoundsToHeightGrid(double input, double expected)
    {
        Assert.Equal(expected, MixerStrip.SnapCardHeight(input));
    }

    [Fact]
    public void Defaults_AreWithinClampRange()
    {
        Assert.InRange(MixerStrip.DefaultCardHeight, MixerStrip.MinCardHeight, MixerStrip.MaxCardHeight);
    }
}
