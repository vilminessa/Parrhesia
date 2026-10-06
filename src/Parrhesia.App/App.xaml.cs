using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;

namespace Parrhesia.App;

public partial class App : Application
{
    private const int AttachParentProcess = -1;

    /// <summary>Аргументы командной строки текущего запуска.</summary>
    public static string[] StartupArgs { get; private set; } = [];

    protected override void OnStartup(StartupEventArgs e)
    {
        StartupArgs = e.Args;

        if (Array.IndexOf(e.Args, "--console") >= 0)
        {
            EnableConsoleLogging();
        }

        AppServices.Initialize();
        AppServices.StartEngine();
        Trace.WriteLine($"[Parrhesia] запущен (профиль: {AppServices.Profiles.Active?.Name ?? "?"})");
        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Trace.WriteLine("[Parrhesia] останавливается...");
        AppServices.Shutdown();
        base.OnExit(e);
    }

    /// <summary>
    /// Подключает консоль родительского процесса (debug.bat) и выводит
    /// туда Trace-логи движка в реальном времени.
    /// </summary>
    private static void EnableConsoleLogging()
    {
        AttachConsole(AttachParentProcess);
        Trace.AutoFlush = true;
        Trace.Listeners.Add(new ConsoleTraceListener());
        Trace.WriteLine("[Parrhesia] логи подключены к консоли");
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int processId);
}
