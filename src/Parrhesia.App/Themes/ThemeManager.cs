using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;

namespace Parrhesia.App.Themes;

/// <summary>
/// Менеджер тем (порт механики Synfronia): встроенные палитры сидятся в
/// %APPDATA%\Parrhesia\themes\{id}\theme.json при первом запуске (правки
/// сохраняются), пользовательские темы = новая подпапка — подхватывается сразу,
/// поля проходят валидацию (⚠ в списке), поддерживается «extends».
/// Применение — слой ResourceDictionary ПОСЛЕ Colors.xaml с теми же ключами:
/// DynamicResource перечитывает их мгновенно, без перезапуска.
/// </summary>
public static partial class ThemeManager
{
    private sealed record Builtin(
        string Id,
        string Label,
        IReadOnlyDictionary<string, string> Colors,
        IReadOnlyDictionary<string, double> Radii);

    // Встроенные темы: основная — текущая палитра Parrhesia; Moon — серые
    // тона с луной на фоне (слой-задник в MainWindow включается по Id).
    private static readonly Builtin[] Builtins =
    [
        new(
            "parrhesia",
            "Parrhesia",
            new Dictionary<string, string>
            {
                ["Deep"] = "#FF0B0D10",
                ["Panel"] = "#FF14171C",
                ["Elevated"] = "#FF1B1F26",
                ["Hover"] = "#FF232830",
                ["Pressed"] = "#FF2C323C",
                ["Stroke"] = "#FF262B33",
                ["StrokeStrong"] = "#FF39404B",
                ["Text"] = "#FFE6E9EF",
                ["TextDim"] = "#FF8B93A1",
                ["TextFaint"] = "#FF5C6472",
                ["Accent"] = "#FFFFB020",
                ["Cyan"] = "#FF35D0C8",
                ["Danger"] = "#FFFF5A52",
                ["Success"] = "#FF3DDC84",
            },
            new Dictionary<string, double>
            {
                ["RadiusS"] = 4,
                ["RadiusM"] = 6,
                ["RadiusL"] = 10,
            }),
        new(
            "moon",
            "Moon",
            new Dictionary<string, string>
            {
                // Серо-белая гамма: светлое поле, белые карточки, тёмный
                // текст — контраст; луна (тёмный диск) читается на светлом.
                ["Deep"] = "#FFEDEEF1",
                ["Panel"] = "#FFF5F6F8",
                ["Elevated"] = "#FFFFFFFF",
                ["Hover"] = "#FFE7E9EE",
                ["Pressed"] = "#FFDCDFE6",
                ["Stroke"] = "#FFD4D8DF",
                ["StrokeStrong"] = "#FFAFB6C1",
                ["Text"] = "#FF22252B",
                ["TextDim"] = "#FF5D636D",
                ["TextFaint"] = "#FF969CA6",
                ["Accent"] = "#FF6E8CA8",
                ["Cyan"] = "#FF3E7CA8",
                ["Danger"] = "#FFC04A43",
                ["Success"] = "#FF3E9C6A",
            },
            new Dictionary<string, double>
            {
                ["RadiusS"] = 10,
                ["RadiusM"] = 14,
                ["RadiusL"] = 20,
            }),
    ];

    /// <summary>Тема-слой (после Colors.xaml в MergedDictionaries).</summary>
    private static ResourceDictionary? _layer;

    private static List<ThemeSpec>? _cache;

    /// <summary>Идентификатор применённой темы (для настроек/слоя луны).</summary>
    public static string CurrentId { get; private set; } = "parrhesia";

    /// <summary>Поднимается после смены темы (UI перечитывает состояние).</summary>
    public static event Action? ThemeChanged;

    /// <summary>Папка тем: %APPDATA%\Parrhesia\themes\{id}\theme.json.
    /// Override — для тестов (изоляция от реального AppData).</summary>
    internal static string? ThemesRootOverride;

    public static string ThemesRoot =>
        ThemesRootOverride
        ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Parrhesia",
            "themes");

    /// <summary>Загрузка тем: сид встроенных → скан папок → валидация → extends.</summary>
    public static IReadOnlyList<ThemeSpec> LoadAll()
    {
        if (_cache is not null)
        {
            return _cache;
        }

        try
        {
            Directory.CreateDirectory(ThemesRoot);
        }
        catch (Exception)
        {
            // Нет папки — работаем только встроенными (без сида на диск).
        }

        // 1) сид встроенных: записать, если файла ещё нет (правки не трогаем).
        foreach (var builtin in Builtins)
        {
            SeedBuiltin(builtin);
        }

        // 2) скан: id подпапок → «сырые» спецификации.
        var raw = new Dictionary<string, RawTheme>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();
        foreach (var builtin in Builtins)
        {
            raw[builtin.Id] = RawFromBuiltin(builtin);
            order.Add(builtin.Id);
        }

        foreach (var dir in SafeEnumerateDirectories(ThemesRoot))
        {
            var id = Path.GetFileName(dir);
            if (!ThemeIdPattern().IsMatch(id))
            {
                continue;
            }

            var file = Path.Combine(dir, "theme.json");
            if (!File.Exists(file))
            {
                continue;
            }

            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file, Encoding.UTF8));
                var spec = ParseRaw(id, doc.RootElement);
                raw[id] = spec;
                if (!order.Contains(id, StringComparer.OrdinalIgnoreCase))
                {
                    order.Add(id);
                }
            }
            catch (Exception ex)
            {
                raw[id] = new RawTheme(id, id, null, [], [], [], $"не читается: {ex.Message}", null, null);
                if (!order.Contains(id, StringComparer.OrdinalIgnoreCase))
                {
                    order.Add(id);
                }
            }
        }

        // 3) extends: base-first, потомки поверх родителя (цвета/радиусы/мета).
        var resolved = new List<ThemeSpec>();
        foreach (var id in order)
        {
            resolved.Add(Resolve(raw, id));
        }

        _cache = resolved;
        return resolved;
    }

    /// <summary>Сброс кэша (тесты/повторная загрузка после правок тем на диске).</summary>
    public static void Reload() => _cache = null;

    /// <summary>Применить тему по id: слой ресурсов (Brush.*/Radius.*) обновляется мгновенно.</summary>
    public static bool Apply(string id)
    {
        var themes = LoadAll();
        var spec = themes.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase))
                   ?? themes[0];

        var dict = BuildResourceLayer(spec);

        if (Application.Current is null)
        {
            CurrentId = spec.Id;
            return false;
        }

        var merged = Application.Current.Resources.MergedDictionaries;
        if (_layer is null)
        {
            merged.Add(_layer = dict);
        }
        else
        {
            // Точечная замена ключей: DynamicResource получает change-событие
            // на каждый ключ и перекрашивается без перезагрузки окна.
            foreach (var key in _layer.Keys.Cast<object>().ToList())
            {
                _layer.Remove(key);
            }

            foreach (System.Collections.DictionaryEntry pair in dict)
            {
                _layer[pair.Key] = pair.Value;
            }
        }

        CurrentId = spec.Id;
        ThemeChanged?.Invoke();
        return true;
    }

    /// <summary>Слой ресурсов темы: Brush.* (SolidColorBrush) + Radius.S/M/L.</summary>
    private static ResourceDictionary BuildResourceLayer(ThemeSpec spec)
    {
        var dict = new ResourceDictionary();
        foreach (var key in ThemeSpec.ColorKeys)
        {
            var hex = spec.Colors.TryGetValue(key, out var value) ? value : null;
            if (hex is null)
            {
                continue;
            }

            try
            {
                var color = (Color)ColorConverter.ConvertFromString(hex);
                dict[$"Brush.{key}"] = new SolidColorBrush(color);
            }
            catch (Exception)
            {
                // Цвет уже прошёл валидацию — ошибка парсинга практически невозможна.
            }
        }

        var radiusMap = new Dictionary<string, string>
        {
            ["RadiusS"] = "Radius.S",
            ["RadiusM"] = "Radius.M",
            ["RadiusL"] = "Radius.L",
        };

        foreach (var (key, resource) in radiusMap)
        {
            if (spec.Radii.TryGetValue(key, out var value))
            {
                dict[resource] = new CornerRadius(value);
            }
        }

        return dict;
    }

    // ---- внутренности ----

    private sealed record RawTheme(
        string Id,
        string Label,
        string? Extends,
        Dictionary<string, string> Colors,
        Dictionary<string, double> Radii,
        List<string> Warnings,
        string? Warning,
        string? Author,
        string? Unused);

    private static RawTheme RawFromBuiltin(Builtin builtin) => new(
        builtin.Id,
        builtin.Label,
        null,
        new Dictionary<string, string>(builtin.Colors, StringComparer.OrdinalIgnoreCase),
        new Dictionary<string, double>(builtin.Radii),
        [],
        null,
        null,
        null);

    private static RawTheme ParseRaw(string id, JsonElement data)
    {
        var (colors, radii, warnings) = ThemeValidation.ValidateColorsAndRadii(data);
        string? label = null;
        string? extends = null;
        string? author = null;

        if (data.TryGetProperty("label", out var lp))
        {
            label = ThemeValidation.CleanText(lp.GetString(), 60);
        }

        if (data.TryGetProperty("extends", out var ep))
        {
            var value = ThemeValidation.CleanText(ep.GetString(), 40);
            if (ThemeIdPattern().IsMatch(value))
            {
                extends = value;
            }
            else
            {
                warnings.Add($"extends: некорректный id ({Truncate(ep)})");
            }
        }

        if (data.TryGetProperty("author", out var ap))
        {
            author = ThemeValidation.CleanText(ap.GetString(), 60);
        }

        return new RawTheme(
            id,
            string.IsNullOrWhiteSpace(label) ? id : label,
            extends,
            colors,
            radii,
            warnings,
            warnings.Count > 0 ? string.Join("; ", warnings) : null,
            author,
            null);
    }

    /// <summary>Цепочка extends base-first (без цycles) → итоговая спецификация.</summary>
    private static ThemeSpec Resolve(Dictionary<string, RawTheme> raw, string id)
    {
        var chain = new List<RawTheme>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? cursor = id;
        while (cursor is not null && seen.Add(cursor))
        {
            if (!raw.TryGetValue(cursor, out var current))
            {
                break;
            }

            chain.Insert(0, current);
            cursor = current.Extends;
        }

        var colors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var radii = new Dictionary<string, double>();
        var warnings = new List<string>();
        string label = id;
        string? author = null;

        foreach (var item in chain)
        {
            foreach (var (key, value) in item.Colors)
            {
                colors[key] = value;
            }

            foreach (var (key, value) in item.Radii)
            {
                radii[key] = value;
            }

            foreach (var warning in item.Warnings)
            {
                if (!warnings.Contains(warning))
                {
                    warnings.Add(warning);
                }
            }

            label = item.Label;
            author ??= item.Author;
        }

        return new ThemeSpec
        {
            Id = id,
            Label = label,
            Colors = colors,
            Radii = radii,
            Extends = raw.TryGetValue(id, out var self) ? self.Extends : null,
            Author = author,
            Warnings = warnings,
        };
    }

    private static void SeedBuiltin(Builtin builtin)
    {
        try
        {
            var dir = Path.Combine(ThemesRoot, builtin.Id);
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, "theme.json");
            if (File.Exists(file))
            {
                return;
            }

            var data = new Dictionary<string, object?>
            {
                ["label"] = builtin.Label,
            };
            foreach (var (key, value) in builtin.Colors)
            {
                data[key] = value;
            }

            foreach (var (key, value) in builtin.Radii)
            {
                data[key] = value;
            }

            File.WriteAllText(
                file,
                JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false));
        }
        catch (Exception)
        {
            // Сид не критичен: тема и так живёт во встроенном описании.
        }
    }

    private static IEnumerable<string> SafeEnumerateDirectories(string root)
    {
        try
        {
            return Directory.EnumerateDirectories(root);
        }
        catch (Exception)
        {
            return [];
        }
    }

    private static string Truncate(JsonElement prop)
    {
        var text = prop.ToString();
        return text.Length <= 40 ? text : text[..40] + "…";
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9_-]*$")]
    private static partial System.Text.RegularExpressions.Regex ThemeIdPattern();
}
