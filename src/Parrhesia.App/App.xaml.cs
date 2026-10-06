using System.Windows;

namespace Parrhesia.App;

public partial class App : Application
{
    /// <summary>Аргументы командной строки текущего запуска.</summary>
    public static string[] StartupArgs { get; private set; } = [];

    protected override void OnStartup(StartupEventArgs e)
    {
        StartupArgs = e.Args;
        AppServices.Initialize();
        AppServices.StartEngine();
        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        AppServices.Shutdown();
        base.OnExit(e);
    }
}
