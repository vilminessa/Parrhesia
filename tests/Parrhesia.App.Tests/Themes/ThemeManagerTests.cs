using System.IO;
using System.Text.Json;
using Parrhesia.App.Themes;

namespace Parrhesia.App.Tests.Themes;

/// <summary>
/// Механика тем (порт Synfronia): сид встроенных, скан пользовательских
/// папок, валидация значений (⚠), наследование extends, живое Apply.
/// </summary>
public class ThemeManagerTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "parr-themes-" + Guid.NewGuid().ToString("N"));

    public ThemeManagerTests()
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

    [Fact]
    public void LoadAll_SeedsBuiltins_WithFullPalettes()
    {
        var themes = ThemeManager.LoadAll();

        var parrhesia = themes.Single(t => t.Id == "parrhesia");
        var glass = themes.Single(t => t.Id == "liquid-glass");
        var moon = themes.Single(t => t.Id == "moon");

        // Сид лёг на диск (правки пользователя сохранятся при следующем старте).
        Assert.True(File.Exists(Path.Combine(_root, "parrhesia", "theme.json")));
        Assert.True(File.Exists(Path.Combine(_root, "liquid-glass", "theme.json")));
        Assert.True(File.Exists(Path.Combine(_root, "moon", "theme.json")));

        // Полные палитры + радиусы + без предупреждений.
        foreach (var theme in new[] { parrhesia, glass, moon })
        {
            Assert.All(ThemeSpec.ColorKeys, key => Assert.Contains(key, theme.Colors.Keys));
            Assert.All(ThemeSpec.RadiusKeys, key => Assert.Contains(key, theme.Radii.Keys));
            Assert.False(theme.HasWarnings);
        }

        // Пометки видимости: moon скрыта («отменим пока»), остальные — нет.
        Assert.True(moon.Hidden);
        Assert.False(parrhesia.Hidden);
        Assert.False(glass.Hidden);

        // Liquid Glass — полупрозрачные панели (стекло) и радиусы12/18/26.
        Assert.Equal(12, glass.Radii["RadiusS"]);
        Assert.True(ParseAlpha(glass.Colors["Panel"]) < 0xFF, "панели Liquid Glass должны быть полупрозрачными");

        // Разные палитры у разных тем.
        Assert.NotEqual(parrhesia.Colors["Deep"], moon.Colors["Deep"]);
    }

    private static byte ParseAlpha(string argb)
    {
        var text = argb.TrimStart('#');
        return text.Length == 8 ? Convert.ToByte(text[..2], 16) : (byte)0xFF;
    }

    [Fact]
    public void LoadAll_SeedsOnlyOnce_UserEditsSurvive()
    {
        _ = ThemeManager.LoadAll();

        var file = Path.Combine(_root, "moon", "theme.json");
        var doc = JsonDocument.Parse(File.ReadAllText(file));
        var edited = doc.RootElement.Clone().EnumerateObject()
            .Select(p => p)
            .ToDictionary(p => p.Name, p => p.Value);
        edited["Accent"] = JsonSerializer.SerializeToElement("#FF123456");
        File.WriteAllText(file, JsonSerializer.Serialize(edited));

        ThemeManager.Reload();
        var moon = ThemeManager.LoadAll().Single(t => t.Id == "moon");

        // Правка пользователя пережила повторную загрузку (сид не перезаписывает).
        Assert.Equal("#FF123456", moon.Colors["Accent"]);
    }

    [Fact]
    public void LoadAll_InvalidColor_WarnsAndDrops()
    {
        Directory.CreateDirectory(Path.Combine(_root, "broken"));
        File.WriteAllText(
            Path.Combine(_root, "broken", "theme.json"),
            JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["label"] = "Broken",
                ["Accent"] = "не-цвет",
                ["RadiusM"] = "много",
            }));

        ThemeManager.Reload();
        var broken = ThemeManager.LoadAll().Single(t => t.Id == "broken");

        Assert.True(broken.HasWarnings);
        Assert.Contains(broken.Warnings, w => w.Contains("Accent"));
        Assert.Contains(broken.Warnings, w => w.Contains("RadiusM"));
        Assert.False(broken.Colors.ContainsKey("Accent")); // отброшен
        Assert.False(broken.Radii.ContainsKey("RadiusM"));
    }

    [Fact]
    public void LoadAll_ExtendsInheritsPalette()
    {
        Directory.CreateDirectory(Path.Combine(_root, "child"));
        File.WriteAllText(
            Path.Combine(_root, "child", "theme.json"),
            JsonSerializer.Serialize(new
            {
                label = "Child",
                extends = "parrhesia",
                Accent = "#FF00FF00",
            }));

        ThemeManager.Reload();
        var child = ThemeManager.LoadAll().Single(t => t.Id == "child");

        Assert.Equal("#FF00FF00", child.Colors["Accent"]);       // своё переопределило
        Assert.Equal("#FF0B0D10", child.Colors["Deep"]);          // унаследовано от parrhesia
        Assert.False(child.HasWarnings);
    }

    [Fact]
    public void Validate_ColorsAndNumbers()
    {
        Assert.Equal("#1A2B3C", ThemeValidation.CleanColor("#1A2B3C"));
        Assert.Equal("rgb(1,2,3)", ThemeValidation.CleanColor(" rgb(1,2,3) "));
        Assert.Equal("red", ThemeValidation.CleanColor("red"));
        Assert.Null(ThemeValidation.CleanColor("не-цвет"));
        Assert.Null(ThemeValidation.CleanColor(null));

        Assert.Equal(64, ThemeValidation.CleanNumber("999", 0, 64));
        Assert.Equal(0, ThemeValidation.CleanNumber("-5", 0, 64));
        Assert.Equal(7.5, ThemeValidation.CleanNumber("7.5", 0, 64));
        Assert.Null(ThemeValidation.CleanNumber("мусор", 0, 64));
        Assert.Null(ThemeValidation.CleanNumber(double.NaN, 0, 64));
    }

    [Fact]
    public void Apply_WithoutApplication_SetsCurrentId()
    {
        // В тестах Application.Current нет — Apply запоминает выбор и
        // сообщает false (слой ресурсов не применить без приложения).
        Assert.False(ThemeManager.Apply("moon"));
        Assert.Equal("moon", ThemeManager.CurrentId);

        ThemeManager.Apply("нет-такой");
        Assert.Equal("parrhesia", ThemeManager.CurrentId); // фолбэк на первую

        ThemeManager.Apply("parrhesia");
        Assert.Equal("parrhesia", ThemeManager.CurrentId);
    }
}
