using System.Windows;

namespace Parrhesia.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
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
