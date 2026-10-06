using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Parrhesia.Core.Profiles;

namespace Parrhesia.App.Views;

/// <summary>
/// Вкладки профилей в шапке окна — переключение как в браузере:
/// клик мгновенно меняет активный профиль, двойной клик — переименование,
/// «+» создаёт и открывает новый, крестик закрывает (с подтверждением).
/// </summary>
public partial class ProfileStripView : UserControl
{
    private static readonly TimeSpan DoubleClickWindow = TimeSpan.FromMilliseconds(400);

    private Profile? _lastClicked;
    private DateTime _lastClickTime;

    public ProfileStripView()
    {
        InitializeComponent();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        AppServices.Profiles.ProfilesChanged += OnProfilesChanged;
        AppServices.Profiles.ActiveProfileChanged += OnActiveChanged;
        Rebuild();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        AppServices.Profiles.ProfilesChanged -= OnProfilesChanged;
        AppServices.Profiles.ActiveProfileChanged -= OnActiveChanged;
    }

    private void OnProfilesChanged(object? sender, EventArgs e) => Rebuild();

    private void OnActiveChanged(object? sender, EventArgs e) => Rebuild();

    private void Rebuild()
    {
        TabsHost.Children.Clear();
        foreach (var profile in AppServices.Profiles.Profiles)
        {
            TabsHost.Children.Add(CreateTab(profile));
        }
    }

    private UIElement CreateTab(Profile profile)
    {
        var isActive = ReferenceEquals(profile, AppServices.Profiles.Active);

        var close = new Border
        {
            Width = 18,
            Height = 18,
            CornerRadius = new CornerRadius(9),
            VerticalAlignment = VerticalAlignment.Center,
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            Child = new TextBlock
            {
                Text = "×",
                FontSize = 12,
                Foreground = ResolveBrush("Brush.TextFaint", "#FF5C6472"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, -2, 0, 0),
            },
        };
        ToolTipService.SetToolTip(close, "Закрыть профиль");
        close.MouseLeftButtonDown += (_, args) =>
        {
            // Клик по крестику не должен переключать профиль.
            args.Handled = true;
            CloseFlow(profile);
        };

        var title = new TextBlock
        {
            Text = profile.Name,
            FontSize = 12.5,
            FontWeight = isActive ? FontWeights.SemiBold : FontWeights.Normal,
            Foreground = isActive
                ? ResolveBrush("Brush.Accent", "#FFFFB020")
                : ResolveBrush("Brush.TextDim", "#FF8B93A1"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 4, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 180,
        };

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(title);
        row.Children.Add(close);

        var tab = new Border
        {
            Background = isActive
                ? ResolveBrush("Brush.Panel", "#FF14171C")
                : Brushes.Transparent,
            BorderBrush = isActive
                ? ResolveBrush("Brush.Stroke", "#FF262B33")
                : Brushes.Transparent,
            BorderThickness = isActive ? new Thickness(1, 1, 1, 0) : new Thickness(0),
            CornerRadius = new CornerRadius(6, 6, 0, 0),
            Padding = new Thickness(2, 0, 6, 0),
            Margin = new Thickness(2, 0, 0, 0),
            MinWidth = 70,
            Child = row,
            Cursor = Cursors.Hand,
            Tag = profile,
        };

        tab.MouseLeftButtonDown += OnTabClick;
        tab.MouseRightButtonDown += OnTabRightClick;
        tab.MouseEnter += (_, _) =>
        {
            if (!isActive)
            {
                tab.Background = ResolveBrush("Brush.Hover", "#FF232830");
            }
        };
        tab.MouseLeave += (_, _) =>
        {
            if (!isActive)
            {
                tab.Background = Brushes.Transparent;
            }
        };

        return tab;
    }

    private void OnTabClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border { Tag: Profile profile })
        {
            return;
        }

        var now = DateTime.UtcNow;
        if (ReferenceEquals(profile, _lastClicked) && now - _lastClickTime < DoubleClickWindow)
        {
            _lastClicked = null;
            RenameFlow(profile);
            e.Handled = true;
            return;
        }

        _lastClicked = profile;
        _lastClickTime = now;
        SwitchFlow(profile);
    }

    private void OnTabRightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border { Tag: Profile profile })
        {
            return;
        }

        var menu = new ContextMenu { Placement = PlacementMode.MousePoint, MinWidth = 160 };

        var rename = new MenuItem { Header = "Переименовать" };
        rename.Click += (_, _) => RenameFlow(profile);

        var close = new MenuItem { Header = "Закрыть" };
        close.Click += (_, _) => CloseFlow(profile);

        menu.Items.Add(rename);
        menu.Items.Add(new Separator());
        menu.Items.Add(close);
        menu.IsOpen = true;
        e.Handled = true;
    }

    private void OnAddClick(object sender, RoutedEventArgs e)
    {
        var profile = AppServices.Profiles.CreateEmpty();
        SwitchFlow(profile);
    }

    private void SwitchFlow(Profile profile)
    {
        if (!AppServices.Profiles.SwitchTo(profile, out var error))
        {
            MessageBox.Show(error, "Профиль", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        System.Diagnostics.Trace.WriteLine($"[Профиль] → {profile.Name}");
    }

    private void RenameFlow(Profile profile)
    {
        var owner = Window.GetWindow(this);
        if (!PromptDialog.Show(owner, "Переименовать профиль", profile.Name, out var name))
        {
            return;
        }

        if (!AppServices.Profiles.Rename(profile, name, out var error))
        {
            MessageBox.Show(error, "Профиль", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void CloseFlow(Profile profile)
    {
        var answer = MessageBox.Show(
            $"Закрыть профиль «{profile.Name}»? Его схема будет удалена.",
            "Профиль",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        if (!AppServices.Profiles.Close(profile, out var error))
        {
            MessageBox.Show(error, "Профиль", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static Brush ResolveBrush(string key, string fallbackHex)
    {
        if (Application.Current?.TryFindResource(key) is Brush brush)
        {
            return brush;
        }

        var fallback = new SolidColorBrush((Color)ColorConverter.ConvertFromString(fallbackHex)!);
        fallback.Freeze();
        return fallback;
    }
}
