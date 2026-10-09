using Parrhesia.App.Views;

namespace Parrhesia.App.Tests.Views;

/// <summary>
/// Sticky-привязки (F1): привязка узла не стирается, когда устройство
/// временно пропало из списка активных (BT уснул) — как в VoiceMeeter.
/// </summary>
public class DeviceSelectionTests
{
    private static readonly DeviceChoice Unbound = new(DeviceSelection.UnboundName, null);
    private static readonly DeviceChoice Speakers = new("Speakers (Realtek)", "{0.0.0.00000000}.{speakers}");
    private static readonly DeviceChoice Mic = new("Microphone Array", "{0.0.1.00000000}.{mic}");

    private static readonly DeviceChoice[] Items = [Unbound, Speakers, Mic];

    [Fact]
    public void Resolve_Match_ReturnsListEntry()
    {
        var target = DeviceSelection.Resolve("{0.0.0.00000000}.{speakers}", Items);
        Assert.Equal(Speakers, target);
    }

    [Fact]
    public void Resolve_EmptyId_PicksUnbound()
    {
        var target = DeviceSelection.Resolve(null, Items);
        Assert.NotNull(target);
        Assert.Null(target!.Value);
        Assert.Equal(DeviceSelection.UnboundName, target.Name);
    }

    [Fact]
    public void Resolve_StaleId_ReturnsPhantomWithSameId_BindingKept()
    {
        // Устройство пропало из ACTIVE-списка: раньше фолбэк был items[0]
        // = «(не привязан)» → SetNodeDevice(null) стирал привязку.
        var staleId = "{0.0.1.00000000}.{gone-bt-headset}";
        var target = DeviceSelection.Resolve(staleId, Items);

        Assert.NotNull(target);
        // Фантом НЕСЁТ ТОТ ЖЕ id → обработчик выбора видит совпадение
        // (node.DeviceId == choice.Value) и ничего не перезаписывает.
        Assert.Equal(staleId, target!.Value);
        Assert.StartsWith(DeviceSelection.UnavailablePrefix, target.Name);
    }

    [Fact]
    public void Resolve_EmptyItems_ReturnsNull()
    {
        Assert.Null(DeviceSelection.Resolve("{0.0.0.00000000}.{x}", []));
    }

    [Fact]
    public void ShortLabel_UsesNameCache_OrGuidTail()
    {
        // Кэш-имя (заполнен движком при успешных открытиях) важнее хвоста id.
        var withCache = DeviceSelection.ShortLabel("{0.0.0.00000000}.{b2bdfbf8-75f7-4844-9e7b-51524267b6fa}");
        Assert.False(string.IsNullOrWhiteSpace(withCache));

        var loopback = DeviceSelection.ShortLabel("loopback:{0.0.0.00000000}.{b2bdfbf8-75f7-4844-9e7b-51524267b6fa}");
        Assert.False(string.IsNullOrWhiteSpace(loopback));
    }
}
