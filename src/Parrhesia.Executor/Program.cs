using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using Parrhesia.Core.Graph;
using Parrhesia.Plugins;
using Parrhesia.Plugins.Bridge;
using Parrhesia.Plugins.Clap;
using Parrhesia.Plugins.Vst3;

namespace Parrhesia.Executor;

/// <summary>
/// Процесс-исполнитель плагинов (P-волна): консольный хост (подсистема
/// CONSOLE) с WPF-циклом только под окна редакторов. Модули, которые не
/// инициализируются в GUI-процессе (класс Supertone Clear: висят на
/// getPluginFactory внутри WPF-приложения), здесь грузятся за~9 с.
/// Аргументы: --plugin-host &lt;nodeId32hex&gt; &lt;maxBlockFrames&gt; &lt;spec.json&gt;.
/// </summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length < 3 ||
            !string.Equals(args[0], "--plugin-host", StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine(
                "usage: Parrhesia.Executor --plugin-host <nodeId32hex> <maxBlockFrames> <spec.json>");
            return (2);
        }

        if (!Guid.TryParseExact(args[1], "N", out var nodeId) ||
            !int.TryParse(args[2], out var maxBlockFrames) ||
            maxBlockFrames <= 0)
        {
            Console.Error.WriteLine("--plugin-host: некорректные аргументы узла");
            return (2);
        }

        InstallLogging(nodeId);

        // Загрузка модуля — в ГЛАВНОМ потоке и БЕЗ WPF-приложения (как чистый
        // пробник): Application до загрузки меняет окружение модулей (Clear).
        // WPF создаётся ПОСЛЕ загрузки, но ДО ready — иначе хост-команды
        // «openEditor» придут раньше UI-потока. Цикл аудио — в Task.
        PluginBridge.ChildSide child;
        IAudioPlugin plugin;
        object gate;
        Application? wpf = null;
        try
        {
            child = new PluginBridge.ChildSide(nodeId, maxBlockFrames, TimeSpan.FromSeconds(5));
            gate = new object();
            (plugin, var sampleRate) = LoadSpec(args[3]);
            plugin.Prepare(sampleRate, maxBlockFrames, PluginBridge.Channels);

            var server = new NodeControlServer(
                NodeControl.PipeName(nodeId),
                request => HandleControl(plugin, gate, request));
            server.Start();

            wpf = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            wpf.DispatcherUnhandledException += (_, e) =>
            {
                Log($"DispatcherUnhandledException: {e.Exception}");
                e.Handled = true; // падение окна-редактора не должно убивать исполнителя
            };

            child.MarkReady();
        }
        catch (Exception ex)
        {
            Log($"executor load: {ex}");
            Console.Error.WriteLine($"executor: {ex.Message}");
            Environment.Exit(3);
            return (3);
        }

        var runningChild = child;
        var runningPlugin = plugin;
        var runningGate = gate;
        _ = Task.Run(() => ExecuteLoop(runningChild, runningPlugin, runningGate, maxBlockFrames));

        wpf.Run();
        Log("wpf.Run() вернулся — цикл сообщений закончился");
        return (0);
    }

    /// <summary>
    /// Лог процесса (P-волна): stderr хост читает только до ready — после
    /// смерть молчит. Пишем в %AppData%\Parrhesia\logs\executor-{nodeId}.log.
    /// </summary>
    private static string? _logPath;

    private static void InstallLogging(Guid nodeId)
    {
        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Parrhesia", "logs");
            Directory.CreateDirectory(directory);
            _logPath = Path.Combine(directory, $"executor-{nodeId:N}.log");

            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
                Log($"UnhandledException: {e.ExceptionObject}");
            TaskScheduler.UnobservedTaskException += (_, e) =>
            {
                Log($"UnobservedTaskException: {e.Exception}");
                e.SetObserved();
            };

            Log($"executor старт pid={Environment.ProcessId}");
        }
        catch
        {
            _logPath = null;
        }
    }

    private static void Log(string message)
    {
        if (_logPath is null)
        {
            return;
        }

        try
        {
            lock (typeof(Program))
            {
                File.AppendAllText(_logPath, $"[{DateTime.Now:HH:mm:ss.fff}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Лог не критичен.
        }
    }

    private static void RunExecutor(Guid nodeId, int maxBlockFrames, string specPath)
    {
        using var child = new PluginBridge.ChildSide(
            nodeId, maxBlockFrames, TimeSpan.FromSeconds(5));
        var gate = new object();

        var (plugin, sampleRate) = LoadSpec(specPath);
        plugin.Prepare(sampleRate, maxBlockFrames, PluginBridge.Channels);

        var server = new NodeControlServer(
            NodeControl.PipeName(nodeId),
            request => HandleControl(plugin, gate, request));
        server.Start();

        child.MarkReady();
        ExecuteLoop(child, plugin, gate, maxBlockFrames); // не возвращается
    }

    /// <summary>Цикл: ждёт кик → вход → обработка (под gate) → выход. Не возвращается.</summary>
    private static void ExecuteLoop(
        PluginBridge.ChildSide child,
        IAudioPlugin plugin,
        object gate,
        int maxBlockFrames)
    {
        var channels = PluginBridge.Channels;
        var input = new float[maxBlockFrames * channels];
        var output = new float[maxBlockFrames * channels];

        while (true)
        {
            // Дыхание50мс: молчание хоста не капканит процесс; heartbeat —
            // «жив» независимо от того, гонит ли хост блоки.
            if (!child.WaitForKick(TimeSpan.FromMilliseconds(50)))
            {
                child.Heartbeat();
                continue;
            }

            if (!child.TryTakeInput(input))
            {
                continue;
            }

            lock (gate)
            {
                try
                {
                    plugin.Process(input, maxBlockFrames);
                    Array.Copy(input, output, input.Length);
                }
                catch (Exception)
                {
                    // Отказ обработки блока: публикуем тишину — узел молчит, но жив.
                    Array.Clear(output);
                }
            }

            child.PublishOutput(output);
        }
    }

    private static NodeControlResponse HandleControl(
        IAudioPlugin plugin,
        object gate,
        NodeControlRequest request)
    {
        // openEditor — отдельно: показ окна НЕ под gate хост-хендлера
        // (иначе автосейв хоста ждал бы, пока юзер закроет редактор).
        if (request.Op == "openEditor")
        {
            return OpenEditor(plugin, gate);
        }

        lock (gate)
        {
            return request.Op switch
            {
                "ping" => new NodeControlResponse { Ok = true },
                "getState" => new NodeControlResponse { Ok = true, Data = Encode(plugin.GetState()) },
                "setState" => ApplyState(plugin, request.Data),
                "latency" => new NodeControlResponse { Ok = true, Value = plugin.LatencySamples },
                _ => new NodeControlResponse { Ok = false, Error = $"неизвестная операция: {request.Op}" },
            };
        }
    }

    /// <summary>Окно редактора плагина в этом процессе (ответ сразу, окно асинхронно).</summary>
    private static NodeControlResponse OpenEditor(IAudioPlugin plugin, object gate)
    {
        if (plugin is not IPluginEditor { SupportsEditor: true })
        {
            return new NodeControlResponse { Ok = false, Error = "у плагина нет редактора" };
        }

        var editor = (IPluginEditor)plugin;
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            return new NodeControlResponse { Ok = false, Error = "нет UI-потока процесса" };
        }

        dispatcher.BeginInvoke(() =>
        {
            try
            {
                new EditorHostWindow(editor, plugin.Name, gate).Show();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"executor: редактор не открылся: {ex.Message}");
            }
        });

        return new NodeControlResponse { Ok = true };
    }

    private static NodeControlResponse ApplyState(IAudioPlugin plugin, string? data)
    {
        try
        {
            plugin.SetState(data is null ? null : Convert.FromBase64String(data));
            return new NodeControlResponse { Ok = true };
        }
        catch (Exception ex)
        {
            return new NodeControlResponse { Ok = false, Error = ex.Message };
        }
    }

    private static string? Encode(byte[]? state) => state is null ? null : Convert.ToBase64String(state);

    private static (IAudioPlugin Plugin, int SampleRate) LoadSpec(string specPath)
    {
        var spec = JsonSerializer.Deserialize<PluginNodeSpec>(
                File.ReadAllText(specPath), NodeControl.JsonOptions)
            ?? throw new PluginLoadException($"пустой spec-файл ({specPath})");

        File.Delete(specPath); // прочитан — не мусорим во временном каталоге

        IAudioPlugin plugin = spec.Format switch
        {
            nameof(PluginFormat.Vst3) => LoadVst3(spec),
            nameof(PluginFormat.Clap) => ClapLoader.Load(spec.Path, spec.PluginId),
            _ => throw new PluginLoadException($"неизвестный формат: {spec.Format}"),
        };

        return (plugin, spec.SampleRate);
    }

    /// <summary>VST3: мусорный pluginId (путь из старых профилей) → CID из перечисления.</summary>
    private static IAudioPlugin LoadVst3(PluginNodeSpec spec)
    {
        var classId = Vst3Loader.LooksLikeClassId(spec.PluginId)
            ? spec.PluginId
            : Vst3Loader.Enumerate(spec.Path).FirstOrDefault()?.PluginId
              ?? throw new PluginLoadException($"VST3: в модуле нет аудио-классов ({spec.Path})");

        return Vst3Loader.Load(spec.Path, classId);
    }
}
