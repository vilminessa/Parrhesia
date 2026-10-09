using Parrhesia.App.Themes;

namespace Parrhesia.App.Tests.Themes;

/// <summary>Отображение выбранного значения ComboBox: без «DeviceChoice {…».</summary>
public class DisplayTextConverterTests
{
    private sealed record SampleChoice(string Name, string? Value);

    [Fact]
    public void Pick_StringPassedThrough()
    {
        Assert.Equal("48000", DisplayTextConverter.PickDisplayText("48000"));
    }

    [Fact]
    public void Pick_ObjectWithNamedProperty_UsesName()
    {
        Assert.Equal(
            "Parrhesia In1 (Parrhesia)",
            DisplayTextConverter.PickDisplayText(
                new SampleChoice("Parrhesia In1 (Parrhesia)", "{0.0.0.00000000}.{x}")));
    }

    [Fact]
    public void Pick_ObjectWithoutUsableName_FallsBackToString()
    {
        // Пустое Name — не годится, берём ToString (не пустая строка).
        var text = DisplayTextConverter.PickDisplayText(new SampleChoice("", "v"));
        Assert.False(string.IsNullOrEmpty(text));
    }

    [Fact]
    public void Pick_Null_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, DisplayTextConverter.PickDisplayText(null));
    }
}
