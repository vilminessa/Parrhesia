using System.Text.Json;
using Parrhesia.Core.Graph;
using Parrhesia.Plugins.Clap;

namespace Parrhesia.Plugins.Scanning;

public sealed record PluginScanError(string Path, string Message);

/// <summary>Итог одного прогона: найденные плагины, ошибки загрузки, статистика кэша.</summary>
public sealed record PluginScanResult(
    IReadOnlyList<PluginDescriptor> Plugins,
    IReadOnlyList<PluginScanError> Errors,
    int FromCache,
    int Loaded);

/// <summary>
/// Сканер плагинов по папкам: CLAP (.clap/.dll — загрузка модуля через
/// <see cref="ClapLoader"/>), VST3 (бандлы *.vst3 — путь и имя без загрузки,
/// SDK подключается на этапе V4). Повторные прогоны идут из кэша
/// (инвалидация по mtime+size); битый модуль запоминается как ошибка и не
/// перечитывается, пока файл не изменится. Загрузка — in-process
/// (доверенные папки); вынесенный сканер-процесс — отдельный этап.
/// </summary>
public sealed class PluginScanner
{
    private static readonly string[] ClapExtensions = [".clap", ".dll"];

    private readonly string _cachePath;

    public PluginScanner(string? cachePath = null)
    {
        _cachePath = cachePath ?? PluginCache.DefaultPath;
    }

    /// <summary>Стандартные папки Windows для формата.</summary>
    public static IReadOnlyList<string> DefaultFolders(PluginFormat format)
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return format switch
        {
            PluginFormat.Clap =>
            [
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles), "CLAP"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFilesX86), "CLAP"),
                Path.Combine(appData, "CLAP"),
            ],
            PluginFormat.Vst3 =>
            [
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles), "VST3"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFilesX86), "VST3"),
                Path.Combine(appData, "VST3"),
            ],
            _ => [],
        };
    }

    /// <summary>Все стандартные папки обоих форматов.</summary>
    public static IReadOnlyList<string> DefaultFolders() =>
        [.. DefaultFolders(PluginFormat.Clap), .. DefaultFolders(PluginFormat.Vst3)];

    /// <summary>
    /// Сканирует папки (по умолчанию — стандартные). useCache=false —
    /// принудительное перечитывание всех модулей (кэш перезапишется).
    /// </summary>
    public PluginScanResult Scan(
        IEnumerable<string>? folders = null,
        bool useCache = true,
        CancellationToken cancellationToken = default)
    {
        var roots = (folders ?? DefaultFolders())
            .Where(Directory.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var cache = useCache ? PluginCache.Load(_cachePath) : new PluginCache(_cachePath);

        var plugins = new List<PluginDescriptor>();
        var errors = new List<PluginScanError>();
        var fromCache = 0;
        var loaded = 0;

        foreach (var (format, path) in EnumerateCandidates(roots, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();

            FileInfo info;
            try
            {
                info = new FileInfo(path);
                if (!info.Exists)
                {
                    continue;
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                continue;
            }

            var cached = cache.Find(path);
            if (cached is not null &&
                cached.LastWriteTicks == info.LastWriteTimeUtc.Ticks &&
                cached.Size == info.Length)
            {
                fromCache++;
                if (cached.Error is not null)
                {
                    errors.Add(new PluginScanError(path, cached.Error));
                }
                else
                {
                    foreach (var identity in cached.Plugins)
                    {
                        plugins.Add(new PluginDescriptor(cached.Format, path, identity.PluginId, identity.Name));
                    }
                }

                continue;
            }

            loaded++;
            try
            {
                var entry = new CacheEntry
                {
                    Format = format,
                    Path = path,
                    LastWriteTicks = info.LastWriteTimeUtc.Ticks,
                    Size = info.Length,
                };

                if (format == PluginFormat.Vst3)
                {
                    // Без SDK модуль не грузим: идентичность из moduleinfo.json бандла.
                    entry.Plugins = ReadVst3Identities(path);
                    foreach (var identity in entry.Plugins)
                    {
                        plugins.Add(new PluginDescriptor(PluginFormat.Vst3, path, identity.PluginId, identity.Name));
                    }
                }
                else
                {
                    var found = ClapLoader.Enumerate(path);
                    entry.Plugins = found
                        .Select(d => new PluginIdentity(d.PluginId, d.Name))
                        .ToList();
                    plugins.AddRange(found);
                }

                cache.Upsert(entry);
            }
            catch (Exception ex) when (
                ex is PluginLoadException or BadImageFormatException or DllNotFoundException or IOException)
            {
                cache.Upsert(new CacheEntry
                {
                    Format = format,
                    Path = path,
                    LastWriteTicks = info.LastWriteTimeUtc.Ticks,
                    Size = info.Length,
                    Error = ex.Message,
                });
                errors.Add(new PluginScanError(path, ex.Message));
            }
        }

        cache.Save();
        return new PluginScanResult(plugins, errors, fromCache, loaded);
    }

    /// <summary>
    /// Кандидаты: файлы .clap/.dll (CLAP), файлы и бандлы-директории *.vst3
    /// (берётся внутренний модуль x86_64-win). Дубликаты отсекаются по пути.
    /// </summary>
    private static IEnumerable<(PluginFormat Format, string Path)> EnumerateCandidates(
        IReadOnlyList<string> roots,
        CancellationToken cancellationToken)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in roots)
        {
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                continue;
            }

            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var extension = Path.GetExtension(file);
                PluginFormat? format =
                    extension.Equals(".clap", StringComparison.OrdinalIgnoreCase) ||
                    extension.Equals(".dll", StringComparison.OrdinalIgnoreCase)
                        ? PluginFormat.Clap
                        : extension.Equals(".vst3", StringComparison.OrdinalIgnoreCase)
                            ? PluginFormat.Vst3
                            : null;
                if (format is null || !seen.Add(file))
                {
                    continue;
                }

                yield return (format.Value, file);
            }

            // Бандлы-директории *.vst3: внутренний модуль (обычно x86_64-win).
            IEnumerable<string> directories;
            try
            {
                directories = Directory.EnumerateDirectories(root, "*.vst3", SearchOption.AllDirectories);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                continue;
            }

            foreach (var directory in directories)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string? inner;
                try
                {
                    var modules = Directory.EnumerateFiles(directory, "*.vst3", SearchOption.AllDirectories);
                    inner = modules.FirstOrDefault(p =>
                        p.Contains("x86_64-win", StringComparison.OrdinalIgnoreCase)) ?? modules.FirstOrDefault();
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
                {
                    continue;
                }

                if (inner is not null && seen.Add(inner))
                {
                    yield return (PluginFormat.Vst3, inner);
                }
            }
        }
    }

    /// <summary>
    /// Идентичность VST3-модуля без загрузки: Resources\moduleinfo.json бандла
    /// (имя модуля + классы «Audio Module Class» → CID); без файла — имя файла.
    /// </summary>
    private static List<PluginIdentity> ReadVst3Identities(string modulePath)
    {
        var bundleRoot = FindBundleRoot(modulePath);
        var infoPath = bundleRoot is null
            ? null
            : Path.Combine(bundleRoot, "Resources", "moduleinfo.json");

        if (infoPath is null || !File.Exists(infoPath))
        {
            return
            [
                new PluginIdentity(modulePath, Path.GetFileNameWithoutExtension(modulePath)),
            ];
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(infoPath));
            var root = document.RootElement;
            var moduleName = root.TryGetProperty("Name", out var name) && name.ValueKind == JsonValueKind.String
                ? name.GetString()
                : Path.GetFileNameWithoutExtension(modulePath);

            var identities = new List<PluginIdentity>();
            if (root.TryGetProperty("Classes", out var classes) && classes.ValueKind == JsonValueKind.Array)
            {
                foreach (var pluginClass in classes.EnumerateArray())
                {
                    if (!pluginClass.TryGetProperty("Category", out var category) ||
                        category.GetString() != "Audio Module Class")
                    {
                        continue;
                    }

                    var cid = pluginClass.TryGetProperty("CID", out var cidElement) ? cidElement.GetString() : null;
                    if (string.IsNullOrEmpty(cid))
                    {
                        continue;
                    }

                    var className = pluginClass.TryGetProperty("Name", out var classNameElement)
                        ? classNameElement.GetString()
                        : moduleName;
                    identities.Add(new PluginIdentity(cid, string.IsNullOrEmpty(className) ? moduleName! : className));
                }
            }

            if (identities.Count == 0)
            {
                identities.Add(new PluginIdentity(modulePath, moduleName ?? Path.GetFileNameWithoutExtension(modulePath)));
            }

            return identities;
        }
        catch (JsonException)
        {
            return
            [
                new PluginIdentity(modulePath, Path.GetFileNameWithoutExtension(modulePath)),
            ];
        }
    }

    private static string? FindBundleRoot(string modulePath)
    {
        var directory = Path.GetDirectoryName(modulePath);
        while (!string.IsNullOrEmpty(directory))
        {
            if (directory.EndsWith(".vst3", StringComparison.OrdinalIgnoreCase))
            {
                return directory;
            }

            directory = Path.GetDirectoryName(directory);
        }

        return null;
    }
}
