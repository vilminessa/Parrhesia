using System.Text.Json;
using System.Text.Json.Serialization;
using Parrhesia.Core.Graph;

namespace Parrhesia.Plugins.Scanning;

/// <summary>Одна запись кэша: содержимое одного файла модуля на момент сканирования.</summary>
public sealed record CacheEntry
{
    public PluginFormat Format { get; set; }

    public string Path { get; set; } = string.Empty;

    public long LastWriteTicks { get; set; }

    public long Size { get; set; }

    /// <summary>Найденные плагины модуля (id + имя); пусто для VST3-бандлов без moduleinfo.</summary>
    public List<PluginIdentity> Plugins { get; set; } = [];

    /// <summary>Ошибка загрузки на момент сканирования (не перечитываем, пока файл не изменится).</summary>
    public string? Error { get; set; }
}

public sealed record PluginIdentity(string PluginId, string Name);

/// <summary>
/// Файловый кэш сканера (%AppData%\Parrhesia\plugins.cache.json):
/// повторный скан не грузит неизменённые модули (и не трогает заведомо
/// битые). Инвалидация — по паре (mtime, size); версия схемы — полный сброс.
/// </summary>
public sealed class PluginCache
{
    public const int CurrentVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly Dictionary<string, CacheEntry> _entries = new(StringComparer.OrdinalIgnoreCase);

    public PluginCache(string path)
    {
        Path = path;
    }

    public string Path { get; }

    public int Count => _entries.Count;

    public static string DefaultPath =>
        System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Parrhesia",
            "plugins.cache.json");

    public static PluginCache Load(string path)
    {
        var cache = new PluginCache(path);
        try
        {
            if (!File.Exists(path))
            {
                return cache;
            }

            var document = JsonSerializer.Deserialize<CacheDocument>(File.ReadAllText(path), JsonOptions);
            if (document is null || document.Version != CurrentVersion)
            {
                return cache; // другая версия схемы — начинаем заново
            }

            foreach (var entry in document.Entries ?? [])
            {
                if (!string.IsNullOrEmpty(entry.Path))
                {
                    cache._entries[entry.Path] = entry;
                }
            }
        }
        catch (JsonException)
        {
            // Повреждённый кэш — не критично, перестроится на следующем скане.
        }

        return cache;
    }

    public CacheEntry? Find(string path) =>
        _entries.TryGetValue(path, out var entry) ? entry : null;

    public void Upsert(CacheEntry entry) => _entries[entry.Path] = entry;

    public void Save()
    {
        var document = new CacheDocument
        {
            Version = CurrentVersion,
            Entries = [.. _entries.Values],
        };

        var directory = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(Path, JsonSerializer.Serialize(document, JsonOptions));
    }

    private sealed class CacheDocument
    {
        public int Version { get; set; }

        public List<CacheEntry>? Entries { get; set; }
    }
}
