using Parrhesia.Audio.Devices;

namespace Parrhesia.Audio.Tests.Devices;

/// <summary>Ф4: каталог ланов — классификация потока и имена endpoint'ов.</summary>
public class LaneCatalogTests
{
    private static AudioDeviceInfo Render(string id, string name) =>
        new(id, name, DeviceFlow.Render, false);

    private static AudioDeviceInfo Capture(string id, string name) =>
        new(id, name, DeviceFlow.Capture, false);

    [Fact]
    public void From_ClassifiesFlow_AndTakesNamesFromDeviceLists()
    {
        var render = new[] { Render("{0.0.0.00000000}.{in1}", "Parrhesia In1") };
        var capture = new[] { Capture("{0.0.1.00000000}.{out1}", "Parrhesia Out1") };
        var lanes = new List<(int Lane, string Id)>
        {
            (1, "{0.0.0.00000000}.{in1}"),
            (1, "{0.0.1.00000000}.{out1}"),
        };

        var result = LaneCatalog.From(lanes, capture, render);

        Assert.Equal(2, result.Count);
        Assert.Equal(1, result[0].Lane);
        Assert.Equal(DeviceFlow.Render, result[0].Flow);
        Assert.Equal("Parrhesia In1", result[0].Name);
        Assert.Equal(DeviceFlow.Capture, result[1].Flow);
        Assert.Equal("Parrhesia Out1", result[1].Name);
    }

    [Fact]
    public void From_UnknownDevice_FallsBackToAutoLaneName()
    {
        var lanes = new List<(int Lane, string Id)> { (3, "{0.0.0.00000000}.{in3}") };

        var result = LaneCatalog.From(lanes, [], []);

        Assert.Single(result);
        Assert.Equal("Parrhesia In3", result[0].Name);
        Assert.Equal(DeviceFlow.Render, result[0].Flow);
    }

    [Fact]
    public void Get_WithoutDeviceLists_DoesNotThrow()
    {
        // PnP-обход не должен падать ни при наличии драйвера, ни без него
        // (на машине с драйвером список непуст — на CI пуст; контракт: не бросает).
        var result = LaneCatalog.Get([], []);

        Assert.NotNull(result);
    }
}
