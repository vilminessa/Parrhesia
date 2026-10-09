using System.IO;
using System.Text;
using Parrhesia.App.Themes;

namespace Parrhesia.App.Tests.Themes;

/// <summary>
/// Миграция посеянных тем (D-волна контраста): нетронутый старый сид
/// пересеивается новыми значениями, чужие правки не трогаются (⚠ в списке).
/// </summary>
[Collection(ThemesCollection.Name)]
public class ThemeSeedMigrationTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "parr-seed-" + Guid.NewGuid().ToString("N"));

    public ThemeSeedMigrationTests()
    {
        ThemeManager.ThemesRootOverride = _root;
        ThemeManager.Reload();
    }

    public void Dispose()
    {
        ThemeManager.ThemesRootOverride = null;
        ThemeManager.Reload();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception)
        {
            // Временная папка — не критично.
        }
    }

    /// <summary>Старый сид Parrhesia дословно (до D-волны; без seedVersion).</summary>
    private const string LegacyParrhesiaSeed =
        """
        {
          "label": "Parrhesia",
          "Deep": "#FF0B0D10",
          "Panel": "#FF14171C",
          "Elevated": "#FF1B1F26",
          "Hover": "#FF232830",
          "Pressed": "#FF2C323C",
          "Stroke": "#FF262B33",
          "StrokeStrong": "#FF39404B",
          "Text": "#FFE6E9EF",
          "TextDim": "#FF8B93A1",
          "TextFaint": "#FF5C6472",
          "Accent": "#FFFFB020",
          "Cyan": "#FF35D0C8",
          "Danger": "#FFFF5A52",
          "Success": "#FF3DDC84",
          "RadiusS": 4,
          "RadiusM": 6,
          "RadiusL": 10
        }
        """;

    private string WriteParrhesiaFile(string content)
    {
        var dir = Path.Combine(_root, "parrhesia");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "theme.json");
        File.WriteAllText(file, content, new UTF8Encoding(false));
        return file;
    }

    [Fact]
    public void UntouchedLegacySeed_IsReSeeded_WithNewContrast()
    {
        var file = WriteParrhesiaFile(LegacyParrhesiaSeed);

        var theme = ThemeManager.LoadAll().Single(t => t.Id == "parrhesia");

        var updated = File.ReadAllText(file, Encoding.UTF8);
        Assert.Contains("#FF737C8C", updated);   // новый TextFaint
        Assert.Contains("seedVersion", updated);
        Assert.DoesNotContain("#FF5C6472", updated);
        Assert.Equal("#FF737C8C", theme.Colors["TextFaint"]);
        Assert.False(theme.HasWarnings);
    }

    [Fact]
    public void UserEditedSeed_IsKept_AndWarned()
    {
        // Юзер правил палитру до миграции: одно значение своё + ключей столько же.
        var file = WriteParrhesiaFile(LegacyParrhesiaSeed.Replace("#FF5C6472", "#FF123456"));

        var theme = ThemeManager.LoadAll().Single(t => t.Id == "parrhesia");

        var kept = File.ReadAllText(file, Encoding.UTF8);
        Assert.Contains("#FF123456", kept);
        Assert.DoesNotContain("seedVersion", kept);
        Assert.Equal("#FF123456", theme.Colors["TextFaint"]);
        Assert.True(theme.HasWarnings, "правленый старый сид должен получить ⚠");
    }

    [Fact]
    public void CurrentSeedFile_IsNeverTouched()
    {
        // Уже посеян новой версией (значения могли быть изменены) — не трогаем.
        var file = WriteParrhesiaFile(LegacyParrhesiaSeed
            .Replace("\"label\": \"Parrhesia\",", "\"label\": \"Parrhesia\",\n  \"seedVersion\": 2,"));

        var theme = ThemeManager.LoadAll().Single(t => t.Id == "parrhesia");

        Assert.DoesNotContain("#FF737C8C", File.ReadAllText(file, Encoding.UTF8));
        Assert.False(theme.HasWarnings);
    }
}
