using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using Microsoft.Win32;

namespace Parrhesia.App;

public partial class App : Application
{
    private const int AttachParentProcess = -1;
    private const long MaxLogBytes = 1_000_000;

    /// <summary>Аргументы командной строки текущего запуска.</summary>
    public static string[] StartupArgs { get; private set; } = [];

    protected override void OnStartup(StartupEventArgs e)
    {
        StartupArgs = e.Args;

        // Песочница VST3 (T-волна): дочерний probe-процесс грузит модуль
        // ДО принятия в основной процесс. Аварийное завершение здесь — сигнал
        // «плагин крашит хост»; UI/движок не создаются.
        if (e.Args.Length >= 2 &&
            string.Equals(e.Args[0], "--vst3-probe", StringComparison.OrdinalIgnoreCase))
        {
            Environment.Exit(RunVst3Probe(e.Args[1]));
            return;
        }

        // Режим-исполнитель плагина (S-волна): один аудио-узел через мост.
        if (e.Args.Length >= 3 &&
            string.Equals(e.Args[0], "--plugin-host", StringComparison.OrdinalIgnoreCase))
        {
            //4-й аргумент: число — bench-задержка мс; путь — spec-файл узла (VST3/CLAP).
            var mode = e.Args.Length >= 4 ? e.Args[3] : "0";
            Environment.Exit(PluginHostMode.Run(e.Args[1], int.Parse(e.Args[2]), mode));
            return;
        }

        var console = Array.IndexOf(e.Args, "--console") >= 0;
        InitializeLogging(console);
        InstallCrashHooks();
        TryEnableCrashDumps();

        AppServices.Initialize();

        // Тема — сразу после ресурсов приложения (слой ThemeManager ложится
        // поверх Colors.xaml, DynamicResource перекрасивается мгновенно).
        Themes.ThemeManager.Apply(AppServices.Settings.ThemeId);

        AppServices.StartEngine();
        Trace.WriteLine($"[Parrhesia] запущен (профиль: {AppServices.Profiles.Active?.Name ?? "?"})");
        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Trace.WriteLine("[Parrhesia] останавливается...");
        AppServices.Shutdown();
        Trace.Flush();
        base.OnExit(e);
    }

    /// <summary>
    /// Логирование: всегда в файл %AppData%\Parrhesia\logs\app.log
    /// (ротация при 1 МБ); с --console — ещё и в родительскую консоль.
    /// Необработанные исключения пишутся в файл до выхода.
    /// </summary>
    private static void InitializeLogging(bool console)
    {
        Trace.AutoFlush = true;

        try
        {
            var logDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Parrhesia",
                "logs");
            Directory.CreateDirectory(logDirectory);

            var logFile = Path.Combine(logDirectory, "app.log");
            var info = new FileInfo(logFile);
            if (info.Exists && info.Length > MaxLogBytes)
            {
                var rotated = Path.Combine(logDirectory, "app.old.log");
                File.Delete(rotated);
                File.Move(logFile, rotated);
            }

            Trace.Listeners.Add(new TextWriterTraceListener(logFile));
        }
        catch (Exception ex)
        {
            // Лог не должен ломать запуск.
            Debug.WriteLine("Не удалось открыть файл лога: " + ex.Message);
        }

        if (console)
        {
            AttachConsole(AttachParentProcess);
            Trace.Listeners.Add(new ConsoleTraceListener());
            Trace.WriteLine("[Parrhesia] логи подключены к консоли");
        }

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            Trace.WriteLine("[Parrhesia] НЕПЕРЕХВАЧЕННОЕ ИСКЛЮЧЕНИЕ:");
            Trace.WriteLine(args.ExceptionObject?.ToString() ?? "(нет объекта исключения)");
            Trace.Flush();
        };
    }

    /// <summary>
    /// Проба модуля VST3: перечисление классов грузит модуль целиком
    /// (DllMain/внутренний лоадер) — «ядовитые» плагины падают здесь.
    /// 0 — совместим;1 — ошибка;аварийное завершение ловит родитель
    /// (<see cref="Processing.Vst3Sandbox"/>).
    /// </summary>
    private static int RunVst3Probe(string path)
    {
        try
        {
            var classes = Plugins.Vst3.Vst3Loader.Enumerate(path);
            Console.WriteLine($"ok: {classes.Count} классов");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    /// <summary>
    /// Перехватчики «тихих» путей: исключение в фоновой задаче и на диспетчере.
    /// Нативные AV рантайма этими хуками не ловятся — для них дампы WER
    /// (см. <see cref="TryEnableCrashDumps"/>).
    /// </summary>
    private void InstallCrashHooks()
    {
        DispatcherUnhandledException += (_, args) =>
        {
            Trace.WriteLine("[Parrhesia] НЕПЕРЕХВАЧЕННОЕ ИСКЛЮЧЕНИЕ (диспетчер):");
            Trace.WriteLine(args.Exception.ToString());
            Trace.Flush();
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Trace.WriteLine("[Parrhesia] НЕПЕРЕХВАЧЕННОЕ ИСКЛЮЧЕНИЕ (задача):");
            Trace.WriteLine(args.Exception?.ToString() ?? "(нет исключения)");
            Trace.Flush();
            args.SetObserved();
        };
    }

    /// <summary>
    /// WER LocalDumps (HKCU — без админа): мини-дампы крэшей в
    /// %LOCALAPPDATA%\CrashDumps. Нативный AV внутри VST3-хоста без дампа
    /// не локализуется; существующая ручная настройка не перезаписывается.
    /// </summary>
    private static void TryEnableCrashDumps()
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(
                @"Software\Microsoft\Windows\Windows Error Reporting\LocalDumps\Parrhesia.App.exe");
            if (key is null || key.GetValue("DumpFolder") is not null)
            {
                return;
            }

            key.SetValue(
                "DumpFolder",
                Environment.ExpandEnvironmentVariables(@"%LOCALAPPDATA%\CrashDumps"),
                RegistryValueKind.ExpandString);
            key.SetValue("DumpType", 1, RegistryValueKind.DWord); // мини-дамп
            key.SetValue("DumpCount", 3, RegistryValueKind.DWord);
            Trace.WriteLine("[Parrhesia] включены мини-дампы WER: %LOCALAPPDATA%\\CrashDumps");
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[Parrhesia] не удалось включить WER-дампы: {ex.Message}");
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int processId);
}
