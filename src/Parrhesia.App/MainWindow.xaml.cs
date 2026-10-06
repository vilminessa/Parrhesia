using System.Windows;

namespace Parrhesia.App;

public partial class MainWindow
{
    public MainWindow()
    {
        InitializeComponent();
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
