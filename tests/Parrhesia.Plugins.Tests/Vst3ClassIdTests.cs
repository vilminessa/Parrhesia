using Parrhesia.Plugins.Vst3;

namespace Parrhesia.Plugins.Tests;

/// <summary>
/// S5: распознавание настоящего VST3 class id (32 hex) — чтобы отличить
/// CID от мусорного pluginId (в старых профилях в это поле сохранялся путь).
/// </summary>
public class Vst3ClassIdTests
{
    [Theory]
    [InlineData("C4A8E5D10123456789ABCDEF01234567", true)]
    [InlineData("c4ae5d10123456789abcdef012345678", true)]
    [InlineData("C4AE5D10-1234-5678-9ABC-DEF01234567", false)] // с дефисами (36)
    [InlineData("C:\\Program Files\\Common Files\\VST3\\Clear.vst3", false)] // мусорный pluginId из профиля
    [InlineData("", false)]
    public void LooksLikeClassId_RecognizesVst3Uids(string value, bool expected)
    {
        Assert.Equal(expected, Vst3Loader.LooksLikeClassId(value));
    }
}
