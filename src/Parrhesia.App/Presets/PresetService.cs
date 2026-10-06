using System.IO;
using Parrhesia.Core.Graph;
using Parrhesia.Core.Serialization;

namespace Parrhesia.App.Presets;

/// <summary>
/// Хранение пресетов (полных схем) в %AppData%\Parrhesia\presets\*.json.
/// Загрузка заменяет содержимое общего графа атомарно (событие Reset).
/// </summary>
public sealed class PresetService
{
    private readonly AudioGraph _graph;
    private readonly string _directory;

    public PresetService(AudioGraph graph)
    {
        _graph = graph;
        _directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Parrhesia",
            "presets");
    }

    /// <summary>Имена сохранённых пресетов (без расширения), по алфавиту.</summary>
    public IReadOnlyList<string> List()
    {
        if (!Directory.Exists(_directory))
        {
            return [];
        }

        return Directory.EnumerateFiles(_directory, "*.json")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)
            .ToList()!;
    }

    public bool Exists(string name)
    {
        try
        {
            return File.Exists(PathFor(name, createDirectory: false));
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Сохраняет текущий граф под именем (существующее перезаписывается).</summary>
    public void Save(string name)
    {
        var path = PathFor(name, createDirectory: true);
        File.WriteAllText(path, GraphSerializer.Serialize(_graph));
    }

    /// <summary>Загружает пресет в общий граф. При ошибке граф не изменяется.</summary>
    public bool TryLoad(string name, out string? error)
    {
        error = null;
        string path;
        try
        {
            path = PathFor(name, createDirectory: false);
        }
        catch (ArgumentException ex)
        {
            error = ex.Message;
            return false;
        }

        if (!File.Exists(path))
        {
            error = $"Пресет «{name}» не найден.";
            return false;
        }

        try
        {
            if (!GraphSerializer.TryDeserialize(File.ReadAllText(path), out var preset, out error))
            {
                return false;
            }

            _graph.ReplaceWith(preset!);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = $"Не удалось прочитать пресет: {ex.Message}";
            return false;
        }
    }

    public void Delete(string name)
    {
        var path = PathFor(name, createDirectory: false);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private string PathFor(string name, bool createDirectory)
    {
        var safe = name.Trim();
        if (safe.Length == 0)
        {
            throw new ArgumentException("Имя пресета пустое.", nameof(name));
        }

        if (safe.Length > 64)
        {
            safe = safe[..64];
        }

        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            safe = safe.Replace(invalid, '_');
        }

        if (createDirectory)
        {
            Directory.CreateDirectory(_directory);
        }

        return Path.Combine(_directory, safe + ".json");
    }
}
