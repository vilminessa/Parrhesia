using System.Collections.Concurrent;
using System.Diagnostics;

namespace Parrhesia.Audio.Processing;

/// <summary>
/// Песочница VST3 (T-волна): модуль грузится в ДОЧЕРНЕМ probe-процессе
/// (Parrhesia.App.exe --vst3-probe) до принятия в основной процесс —
/// «ядовитые» плагины (падают при Module::create и портят память рантайма)
/// отклоняются мягко: слот молчит с ошибкой, приложение живо.
/// Кэш — в памяти процесса (путь+ mtime+размер); вне приложения (тесты,
/// где рядом нет App exe) проба пропускается.
/// </summary>
public static class Vst3Sandbox
{
    private sealed record ProbeResult(string? Error);

    private static readonly ConcurrentDictionary<string, ProbeResult> Cache = new();

    /// <summary>null — модуль совместим (можно грузить); иначе причина отказа.</summary>
    public static string? Probe(string modulePath)
    {
        string key;
        try
        {
            var info = new FileInfo(modulePath);
            key = $"{modulePath}|{info.LastWriteTimeUtc.Ticks}|{info.Length}";
        }
        catch (Exception ex)
        {
            return $"файл модуля недоступен: {ex.Message}";
        }

        if (Cache.TryGetValue(key, out var hit))
        {
            return hit.Error;
        }

        var exe = Path.Combine(AppContext.BaseDirectory, "Parrhesia.App.exe");
        if (!File.Exists(exe))
        {
            return null; // Не приложение (тесты) — проба не нужна.
        }

        string? error = null;
        try
        {
            using var process = Process.Start(new ProcessStartInfo(
                    exe,
                    $"--vst3-probe \"{modulePath}\"")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                });

            if (process is null)
            {
                error = "не удалось запустить пробный процесс";
            }
            else
            {
                // Читаем потоки ДО ожидания — иначе возможен deadlock по буферу.
                var stdout = process.StandardOutput.ReadToEndAsync();
                var stderr = process.StandardError.ReadToEndAsync();

                if (!process.WaitForExit(8_000))
                {
                    try
                    {
                        process.Kill(entireProcessTree: true);
                    }
                    catch
                    {
                        // Уже завершился.
                    }

                    error = "проба зависла (более8 с) — модуль несовместим";
                }
                else if (process.ExitCode != 0)
                {
                    var message = stdout.Result.Trim();
                    if (message.Length == 0)
                    {
                        message = stderr.Result.Trim();
                    }

                    error = message.Length == 0
                        ? $"пробный процесс завершился с кодом {process.ExitCode} — крэш при загрузке модуля"
                        : message;
                }
            }
        }
        catch (Exception ex)
        {
            error = $"проба не запустилась: {ex.Message}";
        }

        Cache[key] = new ProbeResult(error);
        return error;
    }
}
