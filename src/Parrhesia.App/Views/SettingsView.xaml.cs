using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Parrhesia.App.Themes;

namespace Parrhesia.App.Views;

/// <summary>Обзор профилей, тем интерфейса и ссылка на папку хранения.</summary>
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
        RefreshThemes();
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
