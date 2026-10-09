using System.IO;
using System.Text.Json;

namespace Parrhesia.Audio.Devices;

/// <summary>
/// Кэш «MMDeviceId → последнее известное имя» (E3: стабильность профилей).
/// Каждый переустанов/rebind драйвера пересоздаёт GUID всех endpoints —
/// сырые id в профиле перестают резолвиться (0x80070490). Кэш заполняется
/// при каждом успешном открытии, а при отказе по имени находится ЗАМЕНА
/// (переименованные своими руками устройства подстраиваются — ищем точное
/// имя; совпадений >1 → не рискуем).
/// </summary>
public static class DeviceNameCache
{
    private static readonly object Gate = new();
    private static Dictionary<string, string>? _map;

    private static string PathCache =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Parrhesia",
            "device-names.json");

    /// <summary>Имя для id (null — неизвестно).</summary>
    public static string? GetName(string deviceId)
    {
        lock (Gate)
        {
            Load();
            return _map!.TryGetValue(deviceId, out var name) ? name : null;
        }
    }

    /// <summary>Запомнить id→имя (+сразу на диск; вызывается при старте движка).</summary>
    public static void Update(string deviceId, string name)
    {
        if (string.IsNullOrEmpty(deviceId) || string.IsNullOrEmpty(name))
        {
            return;
        }

        lock (Gate)
        {
            Load();
            if (_map!.TryGetValue(deviceId, out var existing) &&
                string.Equals(existing, name, StringComparison.Ordinal))
            {
                return;
            }

            _map[deviceId] = name;
            Save();
        }
    }

    /// <summary>
    /// Чистый выбор: из кандидатов (id, имя) ровно один с искомым именем
    /// (OrdinalIgnoreCase) → его id;0 или≥2 совпадений → null (неоднозначно).
    /// </summary>
    public static string? PickUniqueByName(
        IEnumerable<(string Id, string Name)> candidates,
        string name)
    {
        string? match = null;
        foreach (var (id, candidateName) in candidates)
        {
            if (!string.Equals(candidateName, name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (match is not null)
            {
                return null; // неоднозначно
            }

            match = id;
        }

        return match;
    }

    private static void Load()
    {
        if (_map is not null)
        {
            return;
        }

        try
        {
            _map = File.Exists(PathCache)
                ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(PathCache))
                      ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            _map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PathCache)!);
            File.WriteAllText(PathCache, JsonSerializer.Serialize(_map, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception)
        {
            // Кэш не критичен для работы.
        }
    }
}
