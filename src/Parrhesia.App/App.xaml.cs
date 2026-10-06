using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;

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

        var console = Array.IndexOf(e.Args, "--console") >= 0;
        InitializeLogging(console);

        AppServices.Initialize();
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

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int processId);
}
