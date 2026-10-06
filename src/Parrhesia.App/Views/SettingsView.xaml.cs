using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Parrhesia.App.Views;

/// <summary>Управление пресетами: сохранение, загрузка, удаление, автозагрузка.</summary>
public partial class SettingsView : UserControl
{
    private bool _syncing;

    public SettingsView()
    {
        InitializeComponent();
    }

    private string? SelectedPreset => PresetList.SelectedItem as string;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        RefreshList(selectName: AppServices.Settings.AutoLoadPreset);

        _syncing = true;
        try
        {
            AutoLoadBox.IsChecked = AppServices.Settings.AutoLoadPreset is not null;
            if (AppServices.Settings.AutoLoadPreset is { } autoPreset)
            {
                NameBox.Text = autoPreset;
            }
        }
        finally
        {
            _syncing = false;
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
    }

    private void RefreshList(string? selectName = null)
    {
        var names = AppServices.Presets.List();
        PresetList.ItemsSource = names;
        if (selectName is not null)
        {
            PresetList.SelectedItem = names.FirstOrDefault(n =>
                string.Equals(n, selectName, StringComparison.OrdinalIgnoreCase));
        }
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var hasSelection = SelectedPreset is not null;
        LoadButton.IsEnabled = hasSelection;
        DeleteButton.IsEnabled = hasSelection;

        if (!hasSelection || _syncing)
        {
            return;
        }

        NameBox.Text = SelectedPreset;

        // Автозагрузка следует за выбором, пока включена.
        if (AutoLoadBox.IsChecked == true)
        {
            AppServices.Settings.AutoLoadPreset = SelectedPreset;
            AppServices.Settings.Save();
        }
    }

    private void OnNameKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            SaveSelected();
            e.Handled = true;
        }
    }

    private void OnSaveClick(object sender, RoutedEventArgs e) => SaveSelected();

    private void SaveSelected()
    {
        var name = NameBox.Text.Trim();
        if (name.Length == 0)
        {
            NameBox.Focus();
            return;
        }

        AppServices.Presets.Save(name);
        RefreshList(selectName: name);
        NameBox.Text = name;
    }

    private void OnLoadClick(object sender, RoutedEventArgs e) => LoadSelected();

    private void OnLoadDoubleClick(object sender, MouseButtonEventArgs e) => LoadSelected();

    private void LoadSelected()
    {
        if (SelectedPreset is { } name && !AppServices.Presets.TryLoad(name, out var error))
        {
            MessageBox.Show(error, "Пресет", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        if (SelectedPreset is not { } name)
        {
            return;
        }

        var answer = MessageBox.Show(
            $"Удалить пресет «{name}»?",
            "Пресет",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        AppServices.Presets.Delete(name);
        if (AppServices.Settings.AutoLoadPreset == name)
        {
            AppServices.Settings.AutoLoadPreset = null;
            AppServices.Settings.Save();
            _syncing = true;
            try
            {
                AutoLoadBox.IsChecked = false;
            }
            finally
            {
                _syncing = false;
            }
        }

        RefreshList();
        NameBox.Text = string.Empty;
    }

    private void OnAutoLoadChanged(object sender, RoutedEventArgs e)
    {
        if (_syncing)
        {
            return;
        }

        if (AutoLoadBox.IsChecked != true)
        {
            AppServices.Settings.AutoLoadPreset = null;
            AppServices.Settings.Save();
            return;
        }

        var name = SelectedPreset ?? NameBox.Text.Trim();
        if (name.Length == 0)
        {
            _syncing = true;
            try
            {
                AutoLoadBox.IsChecked = false;
            }
            finally
            {
                _syncing = false;
            }

            MessageBox.Show(
                "Введите имя или выберите пресет в списке.",
                "Автозагрузка",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        AppServices.Settings.AutoLoadPreset = name;
        AppServices.Settings.Save();
    }
}
