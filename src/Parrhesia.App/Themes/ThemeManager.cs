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
        IReadOnlyDictionary<string, double> Radii,
        (IReadOnlyDictionary<string, string> Colors, IReadOnlyDictionary<string, double> Radii)? Legacy = null,
        bool Hidden = false);

    /// <summary>Версия сида: файлы без неё (старый сид) пересеиваются при совпадении значений.</summary>
    private const int CurrentSeedVersion = 2;

    // Сид-значения до D-волны контраста: по ним распознаём «нетронутый старый
    // сид» на диске и обновляем его; чужие правки не трогаем.
    private static readonly IReadOnlyDictionary<string, string> ParrhesiaLegacyColors = new Dictionary<string, string>
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
    };

    private static readonly IReadOnlyDictionary<string, double> ParrhesiaLegacyRadii = new Dictionary<string, double>
    {
        ["RadiusS"] = 4,
        ["RadiusM"] = 6,
        ["RadiusL"] = 10,
    };

    private static readonly IReadOnlyDictionary<string, string> LiquidLegacyColors = new Dictionary<string, string>
    {
        ["Deep"] = "#FF05080F",
        ["Panel"] = "#801F345A",
        ["Elevated"] = "#8016223D",
        ["Hover"] = "#A62F4A7A",
        ["Pressed"] = "#CC3A5A96",
        ["Stroke"] = "#33FFFFFF",
        ["StrokeStrong"] = "#4DFFFFFF",
        ["Text"] = "#FFE9F1FF",
        ["TextDim"] = "#8CE9F1FF",
        ["TextFaint"] = "#59E9F1FF",
        ["Accent"] = "#FF69C1FF",
        ["Cyan"] = "#FF8AD1FF",
        ["Danger"] = "#FFFF6B7A",
        ["Success"] = "#FF4FD08A",
    };

    private static readonly IReadOnlyDictionary<string, double> LiquidLegacyRadii = new Dictionary<string, double>
    {
        ["RadiusS"] = 12,
        ["RadiusM"] = 18,
        ["RadiusL"] = 26,
    };

    // Встроенные темы: основная — текущая палитра Parrhesia; Moon — серые
    // тона с луной на фоне (слой-задник в MainWindow включается по Id).
    /// <summary>
    /// Встроенные темы. «moon» помечена hidden (владелец: «отменим пока») —
    /// не показывается в списке; снять hidden в theme.json, чтобы вернуть.
    /// </summary>
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
                // Подписи/подсказки: контраст к Elevated поднят ~3.2→4.4:1 (D-волна).
                ["TextFaint"] = "#FF737C8C",
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
            },
            Legacy: (ParrhesiaLegacyColors, ParrhesiaLegacyRadii)),
        new(
            "liquid-glass",
            "Liquid Glass",
            new Dictionary<string, string>
            {
                // Палитра Synfronia (liquid_glass) + стеклянная полупрозрачность:
                // панели/кнопки полупрозрачны — сквозь них видна фон-сцена,
                // рамки — светлые «кромки стекла». D-волна: альфы текста и рамок
                // подняты (читаемость поверх живой сцены), радиусы чуть меньше.
                ["Deep"] = "#FF05080F",
                ["Panel"] = "#A61F345A",
                ["Elevated"] = "#8016223D",
                ["Hover"] = "#A62F4A7A",
                ["Pressed"] = "#CC3A5A96",
                ["Stroke"] = "#4DFFFFFF",
                ["StrokeStrong"] = "#66FFFFFF",
                ["Text"] = "#FFE9F1FF",
                ["TextDim"] = "#B3E9F1FF",
                ["TextFaint"] = "#80E9F1FF",
                ["Accent"] = "#FF69C1FF",
                ["Cyan"] = "#FF8AD1FF",
                ["Danger"] = "#FFFF6B7A",
                ["Success"] = "#FF4FD08A",
            },
            new Dictionary<string, double>
            {
                ["RadiusS"] = 10,
                ["RadiusM"] = 15,
                ["RadiusL"] = 22,
            },
            Legacy: (LiquidLegacyColors, LiquidLegacyRadii)),
        new(
            "moon",
            "Moon",
            new Dictionary<string, string>
            {
                // Палитра владельца (colorhunt f1eef2/82888d/404248/04030a):
                // тёмно-серая ночь, светлый текст; акцент — светлый (тёмный
                // глейк/текст на нём читается во всех контролах).
                ["Deep"] = "#FF04030A",
                ["Panel"] = "#FF191A21",
                ["Elevated"] = "#FF404248",
                ["Hover"] = "#FF5C6067",
                ["Pressed"] = "#FF82888D",
                ["Stroke"] = "#FF262830",
                ["StrokeStrong"] = "#FF404248",
                ["Text"] = "#FFF1EEF2",
                ["TextDim"] = "#FF82888D",
                ["TextFaint"] = "#FF575C63",
                ["Accent"] = "#FFF1EEF2",
                ["Cyan"] = "#FFC7C2CC",
                ["Danger"] = "#FFC04A43",
                ["Success"] = "#FF3E9C6A",
            },
            new Dictionary<string, double>
            {
                ["RadiusS"] = 10,
                ["RadiusM"] = 14,
                ["RadiusL"] = 20,
            },
            Hidden: true),
    ];

    /// <summary>Тема-слой (после Colors.xaml в MergedDictionaries).</summary>
    private static ResourceDictionary? _layer;

    private static readonly JsonSerializerOptions SeedOptions = new() { WriteIndented = true };

    private static List<ThemeSpec>? _cache;

    /// <summary>Идентификатор применённой темы (для настроек/слоя луны).</summary>
    public static string CurrentId { get; private set; } = "parrhesia";

    /// <summary>Поднимается после смены темы (UI перечитывает состояние).</summary>
    public static event Action? ThemeChanged;

    /// <summary>Темы с фоновой сценой: id → имя слоя-задника в MainWindow.</summary>
    public static readonly IReadOnlyDictionary<string, string> BackdropScenes =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["moon"] = "MoonScene",
            ["liquid-glass"] = "GlassScene",
        };

    /// <summary>Слой-сцена для текущей темы (null — без фона).</summary>
    public static string? CurrentBackdrop =>
        BackdropScenes.TryGetValue(CurrentId, out var scene) ? scene : null;

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

                // Файл-сид старой версии (не совпал с миграцией — были правки):
                // предупреждаем, что обновлённые значения контраста не применены.
                var builtinHasLegacy = Builtins.Any(b =>
                    string.Equals(b.Id, id, StringComparison.OrdinalIgnoreCase) && b.Legacy is not null);
                if (builtinHasLegacy && !HasCurrentSeedVersion(doc.RootElement))
                {
                    spec = spec with
                    {
                        Warnings =
                        [
                            .. spec.Warnings,
                            "сида v2 не было — контраст/радиусы не обновлены (удалите theme.json, чтобы пересеять)",
                        ],
                    };
                }

                raw[id] = spec;
                if (!order.Contains(id, StringComparer.OrdinalIgnoreCase))
                {
                    order.Add(id);
                }
            }
            catch (Exception ex)
            {
                raw[id] = new RawTheme(id, id, null, [], [], [], $"не читается: {ex.Message}", null, false);
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
        bool Hidden);

    private static RawTheme RawFromBuiltin(Builtin builtin) => new(
        builtin.Id,
        builtin.Label,
        null,
        new Dictionary<string, string>(builtin.Colors, StringComparer.OrdinalIgnoreCase),
        new Dictionary<string, double>(builtin.Radii),
        [],
        null,
        null,
        builtin.Hidden);

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

        var hidden = false;
        if (data.TryGetProperty("hidden", out var hp))
        {
            hidden = hp.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.String => hp.GetString() is { } s &&
                    (s.Equals("1", StringComparison.OrdinalIgnoreCase) ||
                     s.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                     s.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
                     s.Equals("on", StringComparison.OrdinalIgnoreCase)),
                _ => false,
            };
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
            hidden);
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

        var hasSelf = raw.TryGetValue(id, out var self);

        return new ThemeSpec
        {
            Id = id,
            Label = label,
            Colors = colors,
            Radii = radii,
            Extends = hasSelf ? self!.Extends : null,
            Author = author,
            Warnings = warnings,
            Hidden = self?.Hidden == true,
        };
    }

    private static void SeedBuiltin(Builtin builtin)
    {
        try
        {
            var dir = Path.Combine(ThemesRoot, builtin.Id);
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, "theme.json");

            var data = new Dictionary<string, object?>
            {
                ["label"] = builtin.Label,
                ["seedVersion"] = CurrentSeedVersion,
            };
            if (builtin.Hidden)
            {
                data["hidden"] = true;
            }

            foreach (var (key, value) in builtin.Colors)
            {
                data[key] = value;
            }

            foreach (var (key, value) in builtin.Radii)
            {
                data[key] = value;
            }

            var json = JsonSerializer.Serialize(data, SeedOptions);

            if (!File.Exists(file))
            {
                File.WriteAllText(file, json, new UTF8Encoding(false));
                return;
            }

            // Миграция сида (D-волна контраста): дословно-нетронутый старый сид
            // пересеиваем новыми значениями; чужие правки не трогаем.
            if (builtin.Legacy is { } legacy && LegacyFileUntouched(file, legacy))
            {
                File.WriteAllText(file, json, new UTF8Encoding(false));
            }
        }
        catch (Exception)
        {
            // Сид не критичен: тема и так живёт во встроенном описании.
        }
    }

    /// <summary>Файл = старый сид дословно (label + все цвета/радиусы, никаких других ключей).</summary>
    private static bool LegacyFileUntouched(
        string file,
        (IReadOnlyDictionary<string, string> Colors, IReadOnlyDictionary<string, double> Radii) legacy)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file, Encoding.UTF8));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            var count = 0;
            foreach (var _ in root.EnumerateObject())
            {
                count++;
            }

            // label + все цвета + все радиусы — ровно набор старого сида.
            if (count != legacy.Colors.Count + legacy.Radii.Count + 1)
            {
                return false;
            }

            foreach (var (key, value) in legacy.Colors)
            {
                if (!root.TryGetProperty(key, out var prop) || prop.GetString() != value)
                {
                    return false;
                }
            }

            foreach (var (key, value) in legacy.Radii)
            {
                if (!root.TryGetProperty(key, out var prop) || prop.GetDouble() != value)
                {
                    return false;
                }
            }

            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Файл посеян текущей (или более новой) версией сида.</summary>
    private static bool HasCurrentSeedVersion(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty("seedVersion", out var version)
        && version.ValueKind == JsonValueKind.Number
        && version.TryGetInt32(out var number)
        && number >= CurrentSeedVersion;

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
