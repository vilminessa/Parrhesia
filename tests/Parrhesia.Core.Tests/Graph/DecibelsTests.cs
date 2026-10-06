using Parrhesia.Core.Graph;

namespace Parrhesia.Core.Tests.Graph;

public class DecibelsTests
{
    [Fact]
    public void ZeroDb_IsUnityGain()
    {
        Assert.Equal(1f, Decibels.FromDb(0f), precision: 5);
        Assert.Equal(0f, Decibels.ToDb(1f), precision: 5);
    }

    [Theory]
    [InlineData(-6f)]
    [InlineData(-20f)]
    [InlineData(-60f)]
    public void RoundTrip_PreservesDb(float db)
    {
        var roundTrip = Decibels.ToDb(Decibels.FromDb(db));
        Assert.Equal(db, roundTrip, precision: 3);
    }

    [Fact]
    public void Silence_IsNegativeInfinityDb()
    {
        Assert.True(float.IsNegativeInfinity(Decibels.ToDb(0f)));
    }

    [Fact]
    public void DeepCut_MapsToZeroGain()
    {
        Assert.Equal(0f, Decibels.FromDb(float.NegativeInfinity));
        Assert.Equal(0f, Decibels.FromDb(Decibels.MinDb - 1f));
        Assert.Equal(0f, Decibels.FromDb(-120f));
    }

    [Fact]
    public void MinusSixDb_IsHalfAmplitude()
    {
        Assert.Equal(0.5f, Decibels.FromDb(-6.0206f), precision: 4);
    }
}
