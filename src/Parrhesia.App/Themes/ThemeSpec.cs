using System.Text.Json;
using System.Text.RegularExpressions;

namespace Parrhesia.App.Themes;

/// <summary>
/// Спецификация темы (порт механики Synfronia, см. themes.py): палитра тех же
/// ключей, что Colors.xaml, радиусы и мета. Значения валидируются при загрузке:
/// некорректное отбрасывается (остаётся дефолт/значение родителя), а тема
/// получает предупреждение (⚠ в списке, текст — в лог).
/// </summary>
public sealed partial class ThemeSpec
{
    /// <summary>Ключи палитры: цвета (HEX/rgb()/имя) — один в один с Colors.xaml.</summary>
    public static readonly string[] ColorKeys =
    [
        "Deep", "Panel", "Elevated", "Hover", "Pressed",
        "Stroke", "StrokeStrong",
        "Text", "TextDim", "TextFaint",
        "Accent", "Cyan", "Danger", "Success",
    ];

    /// <summary>Радиусы скругления (px): бейджи/поля/карточки.</summary>
    public static readonly string[] RadiusKeys = ["RadiusS", "RadiusM", "RadiusL"];

    public string Id { get; init; } = string.Empty;

    public string Label { get; init; } = string.Empty;

    /// <summary>Цвет палитры (после валидации); null — использовать дефолт.</summary>
    public IReadOnlyDictionary<string, string> Colors { get; init; } = new Dictionary<string, string>();

    /// <summary>Радиусы (после валидации).</summary>
    public IReadOnlyDictionary<string, double> Radii { get; init; } = new Dictionary<string, double>();

    public string? Extends { get; init; }

    public string? Author { get; init; }

    /// <summary>Предупреждения валидации (для ⚠ и лога).</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    public bool HasWarnings => Warnings.Count > 0;
}

/// <summary>Валидация значений theme.json — зеркало validate_theme из Synfronia.</summary>
public static partial class ThemeValidation
{
    [GeneratedRegex(@"#(?:[0-9a-fA-F]{3,4}|[0-9a-fA-F]{6}|[0-9a-fA-F]{8})$")]
    private static partial Regex HexColor();

    [GeneratedRegex(@"(?:rgb|rgba|hsl|hsla)\([0-9a-z.,%/\s-]{1,64}\)$", RegexOptions.IgnoreCase)]
    private static partial Regex FuncColor();

    private static readonly HashSet<string> ColorWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "transparent", "currentcolor", "inherit", "initial", "unset", "none",
        "black", "white", "red", "green", "blue", "yellow", "orange", "purple",
        "gray", "grey", "silver", "maroon", "olive", "lime", "aqua", "teal",
        "navy", "fuchsia", "pink", "brown", "beige", "gold", "cyan", "magenta",
    };

    /// <summary>Цвет для палитры: #hex, rgb()/hsl() или имя из списка; иначе null.</summary>
    public static string? CleanColor(object? value)
    {
        if (value is null)
        {
            return null;
        }

        var text = CleanText(value, 80).Replace(" ", string.Empty);
        if (text.Length == 0)
        {
            return null;
        }

        if (HexColor().IsMatch(text) || FuncColor().IsMatch(text) || ColorWords.Contains(text))
        {
            return text;
        }

        return null;
    }

    /// <summary>Число в [lo, hi] с клампингом; null — мусор/NaN/∞.</summary>
    public static double? CleanNumber(object? value, double lo, double hi, int digits = 2)
    {
        double num;
        try
        {
            num = Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (Exception)
        {
            return null;
        }

        if (double.IsNaN(num) || double.IsInfinity(num))
        {
            return null;
        }

        return Math.Round(Math.Clamp(num, lo, hi), digits);
    }

    /// <summary>Строка без управляющих символов, обрезанная до limit.</summary>
    public static string CleanText(object? value, int limit = 80)
    {
        var text = value?.ToString() ?? string.Empty;
        text = ControlChars().Replace(text, " ").Trim();
        return text.Length > limit ? text[..limit] : text;
    }

    [GeneratedRegex(@"[\x00-\x1f\x7f]")]
    private static partial Regex ControlChars();

    /// <summary>
    /// Разбор theme.json в валидную спецификацию: невалидные поля
    /// отбрасываются с предупреждением (значение возьмёт родитель/дефолт).
    /// </summary>
    public static (Dictionary<string, string> Colors, Dictionary<string, double> Radii, List<string> Warnings)
        ValidateColorsAndRadii(JsonElement data)
    {
        var colors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var radii = new Dictionary<string, double>();
        var warnings = new List<string>();

        foreach (var key in ThemeSpec.ColorKeys)
        {
            if (!data.TryGetProperty(key, out var prop))
            {
                continue;
            }

            var color = CleanColor(prop.ValueKind == JsonValueKind.String ? prop.GetString() : null);
            if (color is null)
            {
                warnings.Add($"{key}: некорректный цвет ({Truncate(prop)})");
                continue;
            }

            colors[key] = color;
        }

        var limits = new Dictionary<string, (double Lo, double Hi)>
        {
            ["RadiusS"] = (0, 64),
            ["RadiusM"] = (0, 64),
            ["RadiusL"] = (0, 64),
        };

        foreach (var key in ThemeSpec.RadiusKeys)
        {
            if (!data.TryGetProperty(key, out var prop))
            {
                continue;
            }

            var number = CleanNumber(prop.ValueKind is JsonValueKind.Number or JsonValueKind.String
                    ? prop.ToString()
                    : null,
                limits[key].Lo,
                limits[key].Hi);
            if (number is null)
            {
                warnings.Add($"{key}: не число ({Truncate(prop)})");
                continue;
            }

            radii[key] = number.Value;
        }

        return (colors, radii, warnings);
    }

    private static string Truncate(JsonElement prop)
    {
        var text = prop.ToString();
        return text.Length <= 40 ? text : text[..40] + "…";
    }
}
