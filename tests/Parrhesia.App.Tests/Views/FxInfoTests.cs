using Parrhesia.App.Views.Mixer;

namespace Parrhesia.App.Tests.Views;

/// <summary>Справочник эффекторов (K-волна): стабильные id и подписи — контракт с моделью.</summary>
public class FxInfoTests
{
    [Fact]
    public void All_ContainsFourEffects_WithUniqueStableIds()
    {
        Assert.Equal(4, FxInfo.All.Length);

        var ids = FxInfo.All.Select(e => e.Id).Distinct().ToArray();
        Assert.Equal(4, ids.Length);
        Assert.Contains(FxInfo.Eq, ids);
        Assert.Contains(FxInfo.Comp, ids);
        Assert.Contains(FxInfo.Gate, ids);
        Assert.Contains(FxInfo.Denoise, ids);
    }

    [Fact]
    public void Title_MapsEveryEffectToRussianName()
    {
        Assert.Equal("Эквалайзер", FxInfo.Title(FxInfo.Eq));
        Assert.Equal("Компрессор", FxInfo.Title(FxInfo.Comp));
        Assert.Equal("Гейт", FxInfo.Title(FxInfo.Gate));
        Assert.Equal("Денойзер", FxInfo.Title(FxInfo.Denoise));

        // Неизвестный id не падает — возвращается как есть.
        Assert.Equal("mystery", FxInfo.Title("mystery"));
    }

    [Fact]
    public void ParamsHint_PresentForEveryEffect()
    {
        foreach (var (id, _) in FxInfo.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(FxInfo.ParamsHint(id)), id);
        }
    }
}
