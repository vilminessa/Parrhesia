using Parrhesia.Audio.Engine;

namespace Parrhesia.Audio.Tests.Engine;

/// <summary>Правила мониторинга и аддитивность статуса (Ф6-B2).</summary>
public class MonitorOutputTests
{
    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("{0.0.0.00000000}.{aaa}", "{0.0.0.00000000}.{aaa}")]
    [InlineData("default:render", "default:render")]
    public void NormalizeDeviceId_HandledAsPlanned(string? input, string? expected)
    {
        Assert.Equal(expected, MonitorOutput.NormalizeDeviceId(input));
    }

    [Theory]
    [InlineData(null, true, false)]
    [InlineData("", true, false)]
    [InlineData("{device}", true, true)]
    public void ShouldStart_OnlyWithDeviceAndVirtualSink(string? deviceId, bool sinkIsVirtual, bool expected)
    {
        Assert.Equal(expected, MonitorOutput.ShouldStart(deviceId, sinkIsVirtual));
    }

    [Fact]
    public void ShouldStart_RealSink_DoesNotDoubleSound()
    {
        // Основной выход и так реальный — монитор избыточен (дубль звука).
        Assert.False(MonitorOutput.ShouldStart("{device}", sinkIsVirtual: false));
    }

    [Fact]
    public void EngineStatus_AdditiveMonitorFields_DefaultOff()
    {
        // Аддитивный контракт: старый код создаёт статус без монитора — поля по умолчанию.
        var stopped = EngineStatus.Stopped;
        Assert.Null(stopped.MonitorName);
        Assert.False(stopped.MonitorActive);

        var custom = new EngineStatus(true, 48000, 2, "sink", 3, 0, 0);
        Assert.Null(custom.MonitorName);
        Assert.False(custom.MonitorActive);

        var active = custom with { MonitorName = "Наушники", MonitorActive = true };
        Assert.Equal("Наушники", active.MonitorName);
        Assert.True(active.MonitorActive);
    }
}
