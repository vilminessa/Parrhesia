using System.IO;
using System.Text.Json;
using Parrhesia.Core.Graph;
using Parrhesia.Core.Serialization;

namespace Parrhesia.Core.Profiles;

/// <summary>
/// Система профилей: как вкладки в браузере / сцены в OBS — все профили
/// загружены в память, переключение мгновенное, правки активного сохраняются.
///
/// Хранение: %AppData%\Parrhesia\profiles\*.profile.json (снимки)
/// + index.json (порядок вкладок и активный профиль).
/// Живой граф приложения — это состояние активного профиля.
/// </summary>
public sealed class ProfileService
{
    public const int MaxNameLength = 64;

    private const string IndexFileName = "index.json";
    private const string ProfileExtension = ".profile.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly AudioGraph _liveGraph;
    private readonly string _directory;
    private readonly string _legacyDirectory;
    private readonly List<Profile> _profiles = [];
    private readonly object _gate = new();

    private Profile? _active;
    private string? _initialActiveName;

    public ProfileService(AudioGraph liveGraph, string? directory = null, string? legacyDirectory = null)
    {
        _liveGraph = liveGraph;
        _directory = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Parrhesia",
            "profiles");
        _legacyDirectory = legacyDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Parrhesia",
            "presets");

        Load();
    }

    /// <summary>Список профилей в порядке вкладок.</summary>
    public IReadOnlyList<Profile> Profiles
    {
        get
        {
            lock (_gate)
            {
                return _profiles.ToArray();
            }
        }
    }

    public Profile? Active
    {
        get
        {
            lock (_gate)
            {
                return _active;
            }
        }
    }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _profiles.Count;
            }
        }
    }

    /// <summary>Папка хранения профилей (для «открыть папку» и диагностики).</summary>
    public string DirectoryPath => _directory;

    /// <summary>Список изменился: создание, закрытие, переименование.</summary>
    public event EventHandler? ProfilesChanged;

    /// <summary>Активный профиль сменился (живой граф уже содержит его состояние).</summary>
    public event EventHandler? ActiveProfileChanged;

    /// <summary>
    /// Применяет сохранённый активный профиль (или первый) к живому графу.
    /// Вызывается один раз при старте до запуска движка.
    /// </summary>
    public bool ActivateInitial(out string? error)
    {
        error = null;
        bool raised;
        lock (_gate)
        {
            if (_active is not null)
            {
                return true;
            }

            var target = _initialActiveName is { } wanted
                ? _profiles.FirstOrDefault(p => string.Equals(p.Name, wanted, StringComparison.OrdinalIgnoreCase))
                : null;
            target ??= _profiles.FirstOrDefault();
            if (target is null)
            {
                error = "Нет ни одного профиля.";
                return false;
            }

            if (!ApplySnapshotLocked(target, out error))
            {
                return false;
            }

            _active = target;
            SaveIndexLocked();
            raised = true;
        }

        if (raised)
        {
            ActiveProfileChanged?.Invoke(this, EventArgs.Empty);
        }

        return true;
    }

    /// <summary>Создаёт профиль из текущего состояния живого графа (активным не делает).</summary>
    public Profile CreateFromLive(string? name = null)
    {
        Profile profile;
        lock (_gate)
        {
            profile = new Profile(UniqueNameLocked(name ?? NextAutoNameLocked()))
            {
                SnapshotJson = GraphSerializer.Serialize(_liveGraph),
            };
            _profiles.Add(profile);
            WriteProfileLocked(profile);
            SaveIndexLocked();
        }

        ProfilesChanged?.Invoke(this, EventArgs.Empty);
        return profile;
    }

    /// <summary>Создаёт пустой профиль (активным не делает).</summary>
    public Profile CreateEmpty(string? name = null)
    {
        Profile profile;
        lock (_gate)
        {
            profile = new Profile(UniqueNameLocked(name ?? NextAutoNameLocked()));
            _profiles.Add(profile);
            SaveIndexLocked();
        }

        ProfilesChanged?.Invoke(this, EventArgs.Empty);
        return profile;
    }

    /// <summary>
    /// Переключение: состояние живого графа уходит в текущий профиль,
    /// целевой профиль загружается в живой граф. Мгновенно, как вкладка.
    /// </summary>
    public bool SwitchTo(Profile profile, out string? error)
    {
        error = null;
        var raiseActive = false;
        lock (_gate)
        {
            if (!_profiles.Contains(profile))
            {
                error = "Профиль не найден.";
                return false;
            }

            if (ReferenceEquals(_active, profile))
            {
                return true;
            }

            // Захват правок активного профиля перед уходом.
            if (_active is not null)
            {
                _active.SnapshotJson = GraphSerializer.Serialize(_liveGraph);
                WriteProfileLocked(_active);
            }

            if (!ApplySnapshotLocked(profile, out error))
            {
                // Живой граф не тронут — правки предыдущего профиля уже сохранены шагом выше.
                return false;
            }

            _active = profile;
            SaveIndexLocked();
            raiseActive = true;
        }

        if (raiseActive)
        {
            ActiveProfileChanged?.Invoke(this, EventArgs.Empty);
        }

        return true;
    }

    /// <summary>Переименование: меняет имя, файл и порядок сохраняются.</summary>
    public bool Rename(Profile profile, string newName, out string? error)
    {
        error = null;
        string safe;
        try
        {
            safe = SanitizeName(newName);
        }
        catch (ArgumentException ex)
        {
            error = ex.Message;
            return false;
        }

        lock (_gate)
        {
            if (!_profiles.Contains(profile))
            {
                error = "Профиль не найден.";
                return false;
            }

            if (string.Equals(profile.Name, safe, StringComparison.Ordinal))
            {
                return true;
            }

            if (_profiles.Any(p => !ReferenceEquals(p, profile) &&
                    string.Equals(p.Name, safe, StringComparison.OrdinalIgnoreCase)))
            {
                error = $"Профиль «{safe}» уже существует.";
                return false;
            }

            var oldPath = PathForLocked(profile.Name);
            var newPath = PathForLocked(safe);
            if (profile.SnapshotJson is not null && File.Exists(oldPath))
            {
                File.Move(oldPath, newPath, overwrite: true);
            }

            profile.Name = safe;
            SaveIndexLocked();
        }

        ProfilesChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>
    /// Закрывает профиль. Единственный закрыть нельзя; при закрытии активного
    /// переключается на соседний.
    /// </summary>
    public bool Close(Profile profile, out string? error)
    {
        error = null;
        var raiseActive = false;
        var raiseProfiles = false;
        lock (_gate)
        {
            if (!_profiles.Contains(profile))
            {
                error = "Профиль не найден.";
                return false;
            }

            if (_profiles.Count == 1 && ReferenceEquals(_active, profile))
            {
                error = "Нельзя закрыть единственный профиль.";
                return false;
            }

            var closingActive = ReferenceEquals(_active, profile);

            // Сосед по порядку вкладок: следующий, иначе предыдущий.
            Profile? neighbor = null;
            if (closingActive)
            {
                var index = _profiles.IndexOf(profile);
                neighbor = index + 1 < _profiles.Count
                    ? _profiles[index + 1]
                    : _profiles[index - 1];
            }

            if (closingActive)
            {
                // Сначала переключаемся (при ошибке снимка — выходим, ничего не удаляя).
                if (neighbor is not null && !ApplySnapshotLocked(neighbor, out error))
                {
                    return false;
                }

                _active = neighbor;
                raiseActive = true;
            }

            _profiles.Remove(profile);
            raiseProfiles = true;

            try
            {
                var path = PathForLocked(profile.Name);
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (IOException)
            {
                // Файл мог не существовать — не критично.
            }

            SaveIndexLocked();
        }

        if (raiseProfiles)
        {
            ProfilesChanged?.Invoke(this, EventArgs.Empty);
        }

        if (raiseActive)
        {
            ActiveProfileChanged?.Invoke(this, EventArgs.Empty);
        }

        return true;
    }

    /// <summary>Сохраняет текущее состояние живого графа в активный профиль (память + диск).</summary>
    public void SaveActive()
    {
        lock (_gate)
        {
            if (_active is null)
            {
                return;
            }

            _active.SnapshotJson = GraphSerializer.Serialize(_liveGraph);
            WriteProfileLocked(_active);
            SaveIndexLocked();
        }
    }

    // ===== Диск =====

    private void Load()
    {
        try
        {
            MigrateLegacyPresets();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Миграция не критична — работаем с тем, что есть.
        }

        string? requestedActive = null;
        List<string>? order = null;

        var indexPath = Path.Combine(_directory, IndexFileName);
        try
        {
            if (File.Exists(indexPath))
            {
                var index = JsonSerializer.Deserialize<IndexDocument>(File.ReadAllText(indexPath), JsonOptions);
                requestedActive = index?.Active;
                order = index?.Order;
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // Битый индекс — восстановимся из файлов.
        }

        _initialActiveName = requestedActive;

        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (Directory.Exists(_directory))
        {
            foreach (var file in Directory.EnumerateFiles(_directory, "*" + ProfileExtension))
            {
                var name = Path.GetFileName(file);
                name = name[..^ProfileExtension.Length];
                if (name.Length == 0)
                {
                    continue;
                }

                try
                {
                    files[name] = File.ReadAllText(file);
                }
                catch (IOException)
                {
                    // Нечитаемый файл пропускаем.
                }
            }
        }

        // Порядок из индекса, затем всё остальное (новые/ручные файлы) — в хвост.
        if (order is not null)
        {
            foreach (var name in order)
            {
                if (files.Remove(name, out var content))
                {
                    _profiles.Add(new Profile(name, content));
                }
                else if (!string.IsNullOrWhiteSpace(name))
                {
                    // Файл удалили руками, но вкладка осталась — пустой профиль.
                    _profiles.Add(new Profile(name));
                }
            }
        }

        foreach (var name in files.Keys.OrderBy(k => k, StringComparer.CurrentCultureIgnoreCase))
        {
            _profiles.Add(new Profile(name, files[name]));
        }
    }

    private void MigrateLegacyPresets()
    {
        if (Directory.Exists(_directory) &&
            Directory.EnumerateFiles(_directory, "*" + ProfileExtension).Any())
        {
            return; // профили уже есть — старые пресеты не трогаем
        }

        if (!Directory.Exists(_legacyDirectory))
        {
            return;
        }

        Directory.CreateDirectory(_directory);
        foreach (var file in Directory.EnumerateFiles(_legacyDirectory, "*.json"))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            if (name.Length == 0)
            {
                continue;
            }

            try
            {
                var target = Path.Combine(_directory, SanitizeName(name) + ProfileExtension);
                if (!File.Exists(target))
                {
                    File.Copy(file, target);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // Слабое имя файла пропускаем.
            }
        }
    }

    private bool ApplySnapshotLocked(Profile profile, out string? error)
    {
        error = null;
        if (profile.SnapshotJson is null)
        {
            _liveGraph.ReplaceWith(new AudioGraph());
            return true;
        }

        if (!GraphSerializer.TryDeserialize(profile.SnapshotJson, out var snapshot, out error))
        {
            return false;
        }

        _liveGraph.ReplaceWith(snapshot!);
        return true;
    }

    private void WriteProfileLocked(Profile profile)
    {
        if (profile.SnapshotJson is null)
        {
            return;
        }

        Directory.CreateDirectory(_directory);
        File.WriteAllText(PathForLocked(profile.Name), profile.SnapshotJson);
    }

    private void SaveIndexLocked()
    {
        var index = new IndexDocument
        {
            Active = _active?.Name,
            Order = _profiles.Select(p => p.Name).ToList(),
        };
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, IndexFileName), JsonSerializer.Serialize(index, JsonOptions));
    }

    private string PathForLocked(string name) => Path.Combine(_directory, name + ProfileExtension);

    private string UniqueNameLocked(string requested)
    {
        var safe = SanitizeName(requested);
        if (!_profiles.Any(p => string.Equals(p.Name, safe, StringComparison.OrdinalIgnoreCase)))
        {
            return safe;
        }

        for (var suffix = 2; suffix < 1000; suffix++)
        {
            var candidate = SanitizeName($"{safe} {suffix}");
            if (!_profiles.Any(p => string.Equals(p.Name, candidate, StringComparison.OrdinalIgnoreCase)))
            {
                return candidate;
            }
        }

        return safe + " " + Guid.NewGuid().ToString("N")[..6];
    }

    private string NextAutoNameLocked()
    {
        const string baseName = "Профиль";
        if (!_profiles.Any(p => string.Equals(p.Name, baseName, StringComparison.OrdinalIgnoreCase)))
        {
            return baseName;
        }

        for (var suffix = 2; suffix < 1000; suffix++)
        {
            var candidate = $"{baseName} {suffix}";
            if (!_profiles.Any(p => string.Equals(p.Name, candidate, StringComparison.OrdinalIgnoreCase)))
            {
                return candidate;
            }
        }

        return baseName + " " + Guid.NewGuid().ToString("N")[..6];
    }

    private static string SanitizeName(string name)
    {
        var safe = name.Trim();
        if (safe.Length == 0)
        {
            throw new ArgumentException("Имя профиля пустое.", nameof(name));
        }

        if (safe.Length > MaxNameLength)
        {
            safe = safe[..MaxNameLength];
        }

        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            safe = safe.Replace(invalid, '_');
        }

        // Имя файла конфликтует со служебным index.json.
        if (string.Equals(safe, "index", StringComparison.OrdinalIgnoreCase))
        {
            safe += "_";
        }

        return safe;
    }

    private sealed class IndexDocument
    {
        public int Version { get; set; } = 1;

        public string? Active { get; set; }

        public List<string> Order { get; set; } = [];
    }
}
