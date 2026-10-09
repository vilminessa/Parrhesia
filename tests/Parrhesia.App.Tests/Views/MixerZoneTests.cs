using Parrhesia.App.Views.Mixer;
using Parrhesia.Audio.Engine;
using Parrhesia.Core.Graph;

namespace Parrhesia.App.Tests.Views;

/// <summary>
/// M-волна «зоны»: классификация пультов микшера по происхождению узла —
/// авто-зоны вычисляются на лету и не хранятся в профиле.
/// </summary>
public class MixerZoneTests
{
    private static readonly IReadOnlySet<string> Own = new HashSet<string>(
        StringComparer.OrdinalIgnoreCase) { "{0.0.1.00000000}.{parrhesia-out1}" };

    private static AudioNode Source(string? deviceId)
    {
        var graph = new AudioGraph();
        var node = graph.AddNode("Узел", NodeKind.Source);
        graph.SetNodeDevice(node.Id, deviceId);
        return node;
    }

    [Fact]
    public void Source_NoDevice_IsUnbound()
    {
        Assert.Equal(MixerZone.Unbound, MixerZoneInfo.Of(Source(null), Own));
        Assert.Equal(MixerZone.Unbound, MixerZoneInfo.Of(Source("  "), Own));
    }

    [Fact]
    public void Source_DefaultCapture_IsInputs()
    {
        Assert.Equal(MixerZone.Inputs, MixerZoneInfo.Of(Source(DeviceSpec.DefaultCapture), Own));
    }

    [Theory]
    [InlineData(DeviceSpec.DefaultLoopback)]
    [InlineData("loopback:{0.0.0.00000000}.{speakers}")]
    public void Source_LoopbackOfForeignDevice_IsInputs(string deviceId)
    {
        Assert.Equal(MixerZone.Inputs, MixerZoneInfo.Of(Source(deviceId), Own));
    }

    [Fact]
    public void Source_ForeignEndpoint_IsInputs()
    {
        Assert.Equal(
            MixerZone.Inputs,
            MixerZoneInfo.Of(Source("{0.0.1.00000000}.{mic-array}"), Own));
    }

    [Fact]
    public void Source_OurEndpoint_IsVirtualCables()
    {
        Assert.Equal(
            MixerZone.VirtualCables,
            MixerZoneInfo.Of(Source("{0.0.1.00000000}.{parrhesia-out1}"), Own));
    }

    [Fact]
    public void Source_OurEndpointViaLoopback_IsVirtualCables()
    {
        Assert.Equal(
            MixerZone.VirtualCables,
            MixerZoneInfo.Of(Source("loopback:{0.0.1.00000000}.{parrhesia-out1}"), Own));
    }

    [Fact]
    public void Source_VirtualParrhesia_IsVirtualCables()
    {
        Assert.Equal(
            MixerZone.VirtualCables,
            MixerZoneInfo.Of(Source(DeviceSpec.VirtualParrhesia), Own));
    }

    [Fact]
    public void Source_NoDriverInstalled_EverythingFallsBackToInputs()
    {
        // Драйвер не установлен → «своих» нет → id узлов неотличимы от чужих.
        var empty = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Assert.Equal(MixerZone.Inputs, MixerZoneInfo.Of(Source("{0.0.1.00000000}.{parrhesia-out1}"), empty));
    }

    [Fact]
    public void BusAndSink_AreOutputs()
    {
        var graph = new AudioGraph();
        var bus = graph.AddNode("Шина", NodeKind.Bus);
        var sink = graph.AddNode("Динамики", NodeKind.Sink);

        Assert.Equal(MixerZone.Outputs, MixerZoneInfo.Of(bus, Own));
        Assert.Equal(MixerZone.Outputs, MixerZoneInfo.Of(sink, Own));
    }

    [Fact]
    public void Titles_AreHumanReadableRussian()
    {
        Assert.Equal("УСТРОЙСТВА ВВОДА", MixerZoneInfo.Title(MixerZone.Inputs));
        Assert.Equal("ВИРТУАЛЬНЫЕ КАБЕЛИ", MixerZoneInfo.Title(MixerZone.VirtualCables));
        Assert.Equal("ПРОЧЕЕ", MixerZoneInfo.Title(MixerZone.Unbound));
        Assert.Equal("ШИНЫ И ВЫВОДЫ", MixerZoneInfo.Title(MixerZone.Outputs));

        Assert.Equal("Устройства ввода", MixerZoneInfo.Label(MixerZone.Inputs));
        Assert.Equal("Виртуальные кабели", MixerZoneInfo.Label(MixerZone.VirtualCables));
        Assert.Equal("Прочее", MixerZoneInfo.Label(MixerZone.Unbound));
        Assert.Equal("Шины и выводы", MixerZoneInfo.Label(MixerZone.Outputs));
    }
}
