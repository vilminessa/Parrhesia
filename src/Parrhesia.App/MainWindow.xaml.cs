using System.Windows;

namespace Parrhesia.App;

public partial class MainWindow
{
    public MainWindow()
    {
        InitializeComponent();
        SelectStartupTab();
    }

    /// <summary>
    /// Аргумент запуска "--tab N" (0 Микшер, 1 Схема, 2 Устройства,
    /// 3 Настройки, 4 Диагностика) — для тестов и быстрых переходов.
    /// </summary>
    private void SelectStartupTab()
    {
        var flag = Array.FindIndex(App.StartupArgs, a => a == "--tab");
        if (flag < 0 || flag + 1 >= App.StartupArgs.Length)
        {
            return;
        }

        if (int.TryParse(App.StartupArgs[flag + 1], out var index) &&
            index >= 0 &&
            index < Tabs.Items.Count)
        {
            Tabs.SelectedIndex = index;
        }
    }

    private void OnMinimizeClick(object sender, RoutedEventArgs e)
    {
        SystemCommands.MinimizeWindow(this);
    }

    private void OnMaximizeClick(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized)
        {
            SystemCommands.RestoreWindow(this);
        }
        else
        {
            SystemCommands.MaximizeWindow(this);
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        SystemCommands.CloseWindow(this);
    }

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        if (RestoreIcon is not null)
        {
            RestoreIcon.Visibility = WindowState == WindowState.Maximized
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
    }
}
