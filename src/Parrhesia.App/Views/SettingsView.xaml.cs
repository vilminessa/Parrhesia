using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace Parrhesia.App.Views;

/// <summary>Обзор профилей и ссылка на папку хранения (переключение — в шапке окна).</summary>
public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        AppServices.Profiles.ProfilesChanged += OnProfilesChanged;
        AppServices.Profiles.ActiveProfileChanged += OnProfilesChanged;
        RefreshList();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        AppServices.Profiles.ProfilesChanged -= OnProfilesChanged;
        AppServices.Profiles.ActiveProfileChanged -= OnProfilesChanged;
    }

    private void OnProfilesChanged(object? sender, EventArgs e) => Dispatcher.InvokeAsync(RefreshList);

    private void RefreshList()
    {
        var active = AppServices.Profiles.Active;
        ProfileList.ItemsSource = AppServices.Profiles.Profiles
            .Select(p => ReferenceEquals(p, active) ? p.Name + "  ● активный" : p.Name)
            .ToList();
    }

    private void OnOpenFolderClick(object sender, RoutedEventArgs e)
    {
        var directory = AppServices.Profiles.DirectoryPath;
        Directory.CreateDirectory(directory);
        Process.Start(new ProcessStartInfo("explorer.exe", directory));
    }
}
