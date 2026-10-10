using System.Diagnostics;
using System.Text.Json;
using Parrhesia.Core.Graph;
using Parrhesia.Plugins;
using Parrhesia.Plugins.Bridge;

namespace Parrhesia.Audio.Processing;

/// <summary>
/// Узел-плагин в дочернем процессе (S-волна): хост держит мост
/// (<see cref="PluginBridge"/>), управляющий канал и процесс-исполнитель;
/// ребёнок грузит VST3/CLAP по spec-файлу. Единый механизм для ВСЕХ
/// плагинов: крэш модуля убивает только исполнителя — приложение цело.
/// Process (аудио-поток) не блокируется: публикует вход и читает последний
/// готовый выход; если ребёнок не успел — блок проходит «сухим» (dry),
/// пропуск виден по <see cref="Drops"/>.
/// </summary>
public sealed class ProcessBridgePlugin : IAudioPlugin
{
    private static readonly TimeSpan SpawnTimeout = TimeSpan.FromSeconds(15);

    private readonly Guid _nodeId;
    private readonly PluginSlot _slot;
    private readonly object _lifecycle = new();

    private PluginBridge.HostSide? _host;
    private NodeControlClient? _control;
    private Process? _child;
    private byte[]? _pendingState;
    private int _maxBlockFrames;
    private long _lastSeenBlocks;
    private int _latency;
    private int _forcedRestart;
    private bool _ready;
    private bool _disposed;
    private long _drops;

    private ProcessBridgePlugin(Guid nodeId, PluginSlot slot)
    {
        _nodeId = nodeId;
        _slot = slot;
    }

    /// <summary>Создаёт обёртку (спавн происходит в <see cref="Prepare"/> — там известен формат движка).</summary>
    public static ProcessBridgePlugin Create(Guid nodeId, PluginSlot slot) => new(nodeId, slot);

    public string Name => _slot.Name;

    /// <summary>Задержка моста: один аудио-блок конвейера (верхняя граница).</summary>
    public int LatencySamples => Volatile.Read(ref _latency);

    /// <summary>Пропущенные (dry) блоки — ребёнок не успевал/умер.</summary>
    public long Drops => Interlocked.Read(ref _drops);

    /// <summary>Блоков реально обработано исполнителем (статус узла, диагностика).</summary>
    public long ProcessedBlocks => _host?.ProcessedBlocks ?? 0;

    /// <summary>Pid процесса-исполнителя (0 — не запущен); диагностика и тесты.</summary>
    public int ChildPid
    {
        get
        {
            lock (_lifecycle)
            {
                try
                {
                    return _child is { HasExited: false } ? _child.Id : 0;
                }
                catch
                {
                    return 0;
                }
            }
        }
    }

    /// <summary>Ребёнок жив по heartbeat моста (под локом — мост может освобождаться).</summary>
    public bool ChildAlive
    {
        get
        {
            lock (_lifecycle)
            {
                return _host is { } host && host.ChildAlive(TimeSpan.FromSeconds(2));
            }
        }
    }

    /// <summary>
    /// Исполнитель умер или завис (heartbeat старше2с) — нужен рестарт.
    /// Читается фоновым тикером SlotChainManager (под локом жизненного цикла:
    /// TearDown обнуляет _host под этим же локом — UAF исключён).
    /// </summary>
    public bool NeedsRestart
    {
        get
        {
            if (Volatile.Read(ref _forcedRestart) != 0)
            {
                return true;
            }

            lock (_lifecycle)
            {
                return _ready && _host is { } host && !host.ChildAlive(TimeSpan.FromSeconds(2));
            }
        }
    }

    /// <summary>Последнее известное состояние — передаётся экземпляру-замене при рестарте.</summary>
    public byte[]? TakePendingState()
    {
        lock (_lifecycle)
        {
            var state = _pendingState;
            _pendingState = null;
            return state;
        }
    }

    /// <summary>Принудительный рестарт: следующая сверка менеджера заменит исполнителя.</summary>
    public void ForceRestart() => Interlocked.Exchange(ref _forcedRestart, 1);

    public void SetState(byte[]? state)
    {
        lock (_lifecycle)
        {
            if (_disposed)
            {
                return;
            }

            if (!_ready)
            {
                _pendingState = state; // уйдёт ребёнку после спавна
                return;
            }

            try
            {
                _control!.Request(new NodeControlRequest
                {
                    Op = "setState",
                    Data = state is null ? null : Convert.ToBase64String(state),
                });
                _pendingState = null;
            }
            catch
            {
                // Исполнитель недоступен — состояние сохраняем до рестарта (S3).
                _pendingState = state;
            }
        }
    }

    public byte[]? GetState()
    {
        lock (_lifecycle)
        {
            if (!_ready)
            {
                return _pendingState;
            }

            try
            {
                var response = _control!.Request(new NodeControlRequest { Op = "getState" });
                if (response.Ok)
                {
                    var bytes = response.Data is null ? null : Convert.FromBase64String(response.Data);
                    if (bytes is not null)
                    {
                        _pendingState = bytes;
                    }

                    return bytes;
                }

                return _pendingState;
            }
            catch
            {
                return _pendingState;
            }
        }
    }

    public void Prepare(int sampleRate, int maxBlockFrames, int channels)
    {
        lock (_lifecycle)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_ready && _child is { HasExited: false } && _maxBlockFrames == maxBlockFrames)
            {
                return; // формат тот же — исполнитель уже готов
            }

            // Пересоздание (новый формат или умерший ребёнок): state старого — в новый.
            var state = _pendingState;
            if (_ready)
            {
                state = GetState() ?? state;
                TearDown();
            }

            Spawn(sampleRate, maxBlockFrames, channels);
            Volatile.Write(ref _latency, maxBlockFrames);

            if (state is not null)
            {
                try
                {
                    _control!.Request(new NodeControlRequest
                    {
                        Op = "setState",
                        Data = Convert.ToBase64String(state),
                    });
                }
                catch
                {
                    _pendingState = state;
                }
            }
        }
    }

    public void Process(Span<float> interleaved, int frames)
    {
        // Аудио-поток: только RT-safe операции моста, без локов и аллокаций.
        var host = _host;
        if (!_ready || host is null || frames <= 0 ||
            interleaved.Length < frames * PluginBridge.Channels)
        {
            return; // mono/неготово — сухой проход (лучше dry, чем стоп-кадр)
        }

        var block = interleaved[..(frames * PluginBridge.Channels)];
        host.PublishInput(block); // кик первым — конвейер стартует раньше

        // Фрешнесс = «счётчик вырос с прошлого чтения»: ребёнок обрабатывает
        // асинхронно (обычно между вызовами Process), поэтому сравниваем с
        // последним прочитанным блоком, а не с состоянием до публикации.
        // ProcessedBlocks растёт ПОСЛЕ целого выхода (seqlock) — дельта
        // гарантирует свежий буфер; отставание → dry (счётчик Drops).
        if (host.ProcessedBlocks != _lastSeenBlocks && host.TryReadOutput(block))
        {
            _lastSeenBlocks = host.ProcessedBlocks;
        }
        else
        {
            Interlocked.Increment(ref _drops);
        }
    }

    public void Dispose()
    {
        lock (_lifecycle)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            TearDown();
        }
    }

    /// <summary>Закрывает канал, убивает исполнителя и освобождает мост.</summary>
    private void TearDown()
    {
        _ready = false;

        try
        {
            _control?.Dispose();
        }
        catch
        {
            // Канал уже закрыт.
        }

        _control = null;

        if (_child is { } child)
        {
            try
            {
                if (!child.HasExited)
                {
                    child.Kill(entireProcessTree: true);
                }

                child.WaitForExit(2000);
            }
            catch
            {
                // Уже завершился.
            }

            child.Dispose();
            _child = null;
        }

        try
        {
            _host?.Dispose();
        }
        catch
        {
            // Секция уже отпущена.
        }

        _host = null;
        Volatile.Write(ref _latency, 0);
    }

    /// <summary>
    /// Спавн исполнителя: мост создаётся ДО процесса (ребёнок открывает
    /// существующую секцию), spec — во временный файл. Готовность — флаг
    /// Ready в заголовке моста; смерть/таймаут → PluginLoadException
    /// (слот молчит с ошибкой — как у легаси-загрузок).
    /// </summary>
    private void Spawn(int sampleRate, int maxBlockFrames, int channels)
    {
        var exe = FindAppExe()
            ?? throw new PluginLoadException("исполнитель Parrhesia.App.exe не найден (S-волна)");

        _host = new PluginBridge.HostSide(_nodeId, maxBlockFrames);
        _maxBlockFrames = maxBlockFrames;

        var specPath = Path.Combine(Path.GetTempPath(), $"parrhesia-node-{_nodeId:N}.json");
        File.WriteAllText(specPath, JsonSerializer.Serialize(new PluginNodeSpec
        {
            Format = _slot.Format.ToString(),
            Path = _slot.Path,
            PluginId = _slot.PluginId,
            Name = _slot.Name,
            SampleRate = sampleRate,
            MaxBlockFrames = maxBlockFrames,
            Channels = channels,
        }, NodeControl.JsonOptions));

        try
        {
            _child = System.Diagnostics.Process.Start(new ProcessStartInfo(
                    exe,
                    $"--plugin-host {_nodeId:N} {maxBlockFrames} \"{specPath}\"")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                })
                ?? throw new PluginLoadException("не удалось запустить процесс-исполнитель");

            // Потоки читаем сразу — иначе ребёнок может зависнуть на полном буфере.
            var stdout = _child.StandardOutput.ReadToEndAsync();
            var stderr = _child.StandardError.ReadToEndAsync();

            var deadline = Environment.TickCount64 + (long)SpawnTimeout.TotalMilliseconds;
            while (!_host.IsReady)
            {
                if (_child.HasExited)
                {
                    var detail = DescribeExit(_child.ExitCode, stdout, stderr);
                    TearDown();
                    throw new PluginLoadException(
                        $"исполнитель умер при загрузке «{_slot.Name}» ({_slot.Path}): {detail}");
                }

                if (Environment.TickCount64 >= deadline)
                {
                    TearDown();
                    throw new PluginLoadException(
                        $"исполнитель не готов за {SpawnTimeout.TotalSeconds:0.#} с «{_slot.Name}» ({_slot.Path})");
                }

                Thread.Sleep(20);
            }
        }
        catch
        {
            if (_child is not null)
            {
                TearDown();
            }

            throw;
        }
        finally
        {
            try
            {
                File.Delete(specPath);
            }
            catch
            {
                // Временный файл — не критично.
            }
        }

        _control = new NodeControlClient(NodeControl.PipeName(_nodeId));
        _lastSeenBlocks = 0;
        _drops = 0;
        _pendingState = null;
        _ready = true;
    }

    private static string DescribeExit(
        int exitCode,
        Task<string> stdout,
        Task<string> stderr)
    {
        string message;
        try
        {
            message = (stderr.Wait(2000) ? stderr.Result.Trim() : string.Empty);
            if (message.Length == 0)
            {
                message = stdout.Wait(500) ? stdout.Result.Trim() : string.Empty;
            }
        }
        catch
        {
            message = string.Empty;
        }

        return message.Length > 0 ? message : $"код выхода {exitCode}";
    }

    /// <summary>App.exe: env-переопределение → рядом с хостом → поиск вверх по дереву (тесты).</summary>
    internal static string? FindAppExe()
    {
        var fromEnv = Environment.GetEnvironmentVariable("PARRHESIA_APP_EXE");
        if (!string.IsNullOrEmpty(fromEnv) && File.Exists(fromEnv))
        {
            return fromEnv;
        }

        var direct = Path.Combine(AppContext.BaseDirectory, "Parrhesia.App.exe");
        if (File.Exists(direct))
        {
            return direct;
        }

        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            foreach (var configuration in new[] { "Debug", "Release" })
            {
                var candidate = Path.Combine(
                    dir.FullName, "src", "Parrhesia.App", "bin", configuration,
                    "net10.0-windows", "Parrhesia.App.exe");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }
}
