using Parrhesia.App.Views;
using Parrhesia.Audio.Devices;
using Parrhesia.Audio.Engine;
using Parrhesia.Core.Graph;

namespace Parrhesia.App.Tests.Views;

/// <summary>
/// Ф4: пункты выбора устройства с ланами драйвера — входы приложений
/// первыми (loopback), виртуальный микрофон первым назначением,
/// подсказки по кабельной привязке.
/// </summary>
public class NodeDeviceChoicesTests
{
    private const string In1Id = "{0.0.0.00000000}.{in1}";
    private const string Out1Id = "{0.0.1.00000000}.{out1}";
    private const string MicId = "{0.0.1.00000000}.{mic}";
    private const string SpeakersId = "{0.0.0.00000000}.{spk}";

    private static readonly AudioDeviceInfo[] Capture =
    [
        new(MicId, "Microphone Array", DeviceFlow.Capture, true),
        new(Out1Id, "Parrhesia Out1", DeviceFlow.Capture, false),
    ];

    private static readonly AudioDeviceInfo[] Render =
    [
        new(SpeakersId, "Speakers (Realtek)", DeviceFlow.Render, true),
        new(In1Id, "Parrhesia In1", DeviceFlow.Render, false),
    ];

    private static readonly LaneEndpoint[] Lanes =
    [
        new(1, In1Id, DeviceFlow.Render, "Parrhesia In1"),
        new(1, Out1Id, DeviceFlow.Capture, "Parrhesia Out1"),
    ];

    [Fact]
    public void Source_LaneInputFirst_AsLoopback()
    {
        var items = NodeDeviceChoices.Build(NodeKind.Source, Capture, Render, Lanes);

        // После «(не привязан)» и дефолтов — вход приложений, ДО прочих устройств.
        var lane = items[3];
        Assert.Equal("Вход приложений: Parrhesia In1", lane.Name);
        Assert.Equal("loopback:" + In1Id, lane.Value);
        Assert.Equal(DeviceSpec.DefaultCapture, items[1].Value);
    }

    [Fact]
    public void Source_LaneInputNotDuplicatedAsGenericLoopback()
    {
        var items = NodeDeviceChoices.Build(NodeKind.Source, Capture, Render, Lanes);

        var loopbacksOfLane = items.Where(i => i.Value == "loopback:" + In1Id).ToList();
        Assert.Single(loopbacksOfLane); // ровно один пункт — ланный
    }

    [Fact]
    public void Source_OurCaptureLabeledAsVirtualMicrophone()
    {
        var items = NodeDeviceChoices.Build(NodeKind.Source, Capture, Render, Lanes);

        var out1 = items.First(i => i.Value == Out1Id);
        Assert.Equal("Виртуальный микрофон: Parrhesia Out1", out1.Name);
    }

    [Fact]
    public void Sink_VirtualMicrophoneFirst_LaneInputNotOffered()
    {
        var items = NodeDeviceChoices.Build(NodeKind.Sink, Capture, Render, Lanes);

        // Первое назначение после «По умолчанию (вывод)» — вирт. микрофон.
        var virtualMic = items[2];
        Assert.Equal("Виртуальный микрофон: Parrhesia Out1", virtualMic.Name);
        Assert.Equal(DeviceSpec.VirtualParrhesia, virtualMic.Value);

        // Вход приложений — не назначение (петля на себя): пункта нет.
        Assert.DoesNotContain(items, i => i.Value == In1Id);
    }

    [Fact]
    public void Sink_NoDriver_KeepsLegacyLabel()
    {
        var items = NodeDeviceChoices.Build(NodeKind.Sink, Capture, Render, []);

        Assert.Contains(items, i =>
            i.Name == "Parrhesia Out (виртуальный)" && i.Value == DeviceSpec.VirtualParrhesia);
    }

    [Fact]
    public void Hint_ForLaneLoopback_TellsWindowsRouting()
    {
        var hint = NodeDeviceChoices.Hint("loopback:" + In1Id, Lanes);

        Assert.NotNull(hint);
        Assert.Contains("Parrhesia In1", hint);
        Assert.Contains("выход", hint); // назначение выхода приложения в Windows
    }

    [Fact]
    public void Hint_ForVirtualSink_TellsMicrophoneChoice()
    {
        var hint = NodeDeviceChoices.Hint(DeviceSpec.VirtualParrhesia, Lanes);

        Assert.NotNull(hint);
        Assert.Contains("Parrhesia Out1", hint);
        Assert.Contains("микрофон", hint);
    }

    [Fact]
    public void Hint_OrdinaryOrMissingBinding_None()
    {
        Assert.Null(NodeDeviceChoices.Hint(null, Lanes));
        Assert.Null(NodeDeviceChoices.Hint(DeviceSpec.DefaultRender, Lanes));
        Assert.Null(NodeDeviceChoices.Hint(SpeakersId, Lanes));
        Assert.Null(NodeDeviceChoices.Hint("loopback:" + In1Id, [])); // драйвера нет
    }
}
