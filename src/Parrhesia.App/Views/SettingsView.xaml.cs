using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Parrhesia.App.Settings;
using Parrhesia.App.Themes;

namespace Parrhesia.App.Views;

/// <summary>Обзор профилей, тем интерфейса и ссылка на папку хранения.</summary>
public partial class SettingsView : UserControl
{
    /// <summary>Глушит обработчики при программном наполнении (U3).</summary>
    private bool _syncingUi;

    private static readonly int[] UiScales = [100, 125, 150, 175, 200];

    public SettingsView()
    {
        InitializeComponent();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        AppServices.Profiles.ProfilesChanged += OnProfilesChanged;
        AppServices.Profiles.ActiveProfileChanged += OnProfilesChanged;
        RefreshList();
        RefreshThemes();
        InitializeInterfaceSection();
        RefreshPluginFolders();
    }

    /// <summary>Секция «Интерфейс» (U3): состояние из настроек и реестра автозапуска.</summary>
    private void InitializeInterfaceSection()
    {
        _syncingUi = true;
        try
        {
            TrayBox.IsChecked = AppServices.Settings.CloseToTray;
            AutostartBox.IsChecked = AutostartService.IsEnabled();

            ScaleCombo.ItemsSource = UiScales
                .Select(s => s == 100 ? "100 % (обычный)" : $"{s} %")
                .ToList();
            var current = Math.Clamp(AppServices.Settings.UiScalePercent, 50, 300);
            var index = System.Array.IndexOf(UiScales, current);
            ScaleCombo.SelectedIndex = index < 0 ? 0 : index;
        }
        finally
        {
            _syncingUi = false;
        }
    }

    private void OnTrayToggle(object sender, RoutedEventArgs e)
    {
        if (_syncingUi)
        {
            return;
        }

        AppServices.Settings.CloseToTray = TrayBox.IsChecked == true;
        AppServices.Settings.Save();
    }

    private void OnAutostartToggle(object sender, RoutedEventArgs e)
    {
        if (_syncingUi)
        {
            return;
        }

        try
        {
            AutostartService.SetEnabled(AutostartBox.IsChecked == true);
        }
        catch (Exception ex)
        {
            // Реестр может быть недоступен — вернём честное состояние.
            MessageBox.Show(
                Window.GetWindow(this), ex.Message, "Автозапуск",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            AutostartBox.IsChecked = AutostartService.IsEnabled();
        }
    }

    private void OnScaleChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingUi || ScaleCombo.SelectedIndex < 0)
        {
            return;
        }

        AppServices.Settings.UiScalePercent = UiScales[ScaleCombo.SelectedIndex];
        AppServices.Settings.Save();
        (Window.GetWindow(this) as MainWindow)?.ApplyUiScale();
    }

    // ===== Папки плагинов (T): свои каталоги сверх стандартных =====

    private void RefreshPluginFolders()
    {
        PluginFoldersList.ItemsSource = AppServices.Settings.PluginExtraFolders.ToList();
    }

    private void OnAddPluginFolder(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Папка с плагинами",
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
        {
            return;
        }

        var folder = dialog.FolderName;
        if (string.IsNullOrWhiteSpace(folder))
        {
            return;
        }

        var folders = AppServices.Settings.PluginExtraFolders;
        if (folders.Contains(folder, StringComparer.OrdinalIgnoreCase))
        {
            return; // уже добавлена
        }

        folders.Add(folder);
        AppServices.Settings.Save();
        RefreshPluginFolders();
    }

    private void OnRemovePluginFolder(object sender, RoutedEventArgs e)
    {
        if (PluginFoldersList.SelectedItem is not string folder)
        {
            return;
        }

        AppServices.Settings.PluginExtraFolders.Remove(folder);
        AppServices.Settings.Save();
        RefreshPluginFolders();
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

    /// <summary>Список тем: превью-палитра (3 точки), название, ⚠ для спорных значений.</summary>
    private void RefreshThemes()
    {
        ThemeList.Children.Clear();
        var current = ThemeManager.CurrentId;

        foreach (var theme in ThemeManager.LoadAll())
        {
            if (theme.Hidden)
            {
                continue; // скрытые темы («отменим пока») в список не идут
            }

            var active = string.Equals(theme.Id, current, StringComparison.OrdinalIgnoreCase);
            var row = new Border
            {
                Tag = theme.Id,
                Cursor = Cursors.Hand,
                Padding = new Thickness(10, 7, 10, 7),
                CornerRadius = new CornerRadius(6),
                Margin = new Thickness(0, 0, 0, 6),
                BorderThickness = new Thickness(1),
            };
            row.SetResourceReference(
                Border.BackgroundProperty,
                active ? "Brush.Elevated" : "Brush.Panel");
            row.SetResourceReference(
                Border.BorderBrushProperty,
                active ? "Brush.Accent" : "Brush.Stroke");
            row.MouseLeftButtonUp += OnThemeClick;

            var line = new StackPanel { Orientation = Orientation.Horizontal };

            // Превью-пэллета: фон / панель / акцент (что есть в теме).
            foreach (var key in new[] { "Deep", "Elevated", "Accent" })
            {
                if (!theme.Colors.TryGetValue(key, out var hex))
                {
                    continue;
                }

                try
                {
                    var dot = new Ellipse
                    {
                        Width = 10,
                        Height = 10,
                        Margin = new Thickness(0, 0, 5, 0),
                        VerticalAlignment = VerticalAlignment.Center,
                        Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)),
                    };
                    line.Children.Add(dot);
                }
                catch (Exception)
                {
                    // Некорректный цвет уже помечен ⚠ в спецификации темы.
                }
            }

            var label = new TextBlock
            {
                Text = theme.HasWarnings ? theme.Label + "   ⚠" : theme.Label,
                FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 0, 0),
            };
            label.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text");
            line.Children.Add(label);

            row.Child = line;
            ThemeList.Children.Add(row);
        }
    }

    private void OnThemeClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string id })
        {
            return;
        }

        ThemeManager.Apply(id);
        AppServices.Settings.ThemeId = id;
        AppServices.Settings.Save();
        RefreshThemes();
    }
}
