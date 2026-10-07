using Parrhesia.Audio.Engine;

namespace Parrhesia.Audio.Tests.Engine;

public class DeviceSpecTests
{
    [Fact]
    public void Parse_VirtualParrhesia()
    {
        Assert.True(DeviceSpec.TryParse(DeviceSpec.VirtualParrhesia, out var spec));
        Assert.Equal(DeviceSpecTarget.Virtual, spec.Target);
        Assert.False(spec.Loopback);
        Assert.Equal("virtual:parrhesia", spec.ToString());
    }

    [Fact]
    public void Parse_DefaultCapture_And_Render()
    {
        Assert.True(DeviceSpec.TryParse("default:capture", out var capture));
        Assert.Equal(DeviceSpecTarget.DefaultCapture, capture.Target);

        Assert.True(DeviceSpec.TryParse("default:render", out var render));
        Assert.Equal(DeviceSpecTarget.DefaultRender, render.Target);
        Assert.False(render.Loopback);
    }

    [Fact]
    public void Parse_LoopbackBy_Id()
    {
        Assert.True(DeviceSpec.TryParse("loopback:{0.0.0.00000000}.{abc}", out var spec));
        Assert.True(spec.Loopback);
        Assert.Equal(DeviceSpecTarget.ById, spec.Target);
        Assert.Equal("{0.0.0.00000000}.{abc}", spec.DeviceId);
        Assert.Equal("loopback:{0.0.0.00000000}.{abc}", spec.ToString());
    }

    [Fact]
    public void Parse_PlainId_IsById()
    {
        Assert.True(DeviceSpec.TryParse("{dead.beef}", out var spec));
        Assert.Equal(DeviceSpecTarget.ById, spec.Target);
        Assert.False(spec.Loopback);
    }

    [Fact]
    public void Parse_Empty_Fails()
    {
        Assert.False(DeviceSpec.TryParse(null, out _));
        Assert.False(DeviceSpec.TryParse("  ", out _));
    }
}
