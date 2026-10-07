using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Parrhesia.Core.Graph;
using Parrhesia.Plugins;
using Parrhesia.Plugins.Scanning;

namespace Parrhesia.App.Views;

/// <summary>
/// Выбор эффекта для слота шины: сканирует стандартные папки (с кэшем)
/// в фоне, список с фильтром, двойной клик = выбрать.
/// Пока показываются только CLAP — VST3 подключается на этапе V4.
/// </summary>
public sealed class PluginPickerWindow : Window
{
    private sealed record ScanChoice(string Title, PluginDescriptor Descriptor);

    private readonly TextBox _filter;
    private readonly TextBlock _status;
    private readonly ListBox _list;
    private readonly Button _chooseButton;
    private readonly Button _rescanButton;

    private IReadOnlyList<PluginDescriptor> _all = [];
    private bool _scanning;

    public PluginDescriptor? Selected { get; private set; }

    public PluginPickerWindow()
    {
        Title = "Эффект";
        Width = 520;
        Height = 440;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = BrushOf("Brush.Panel", "#FF14171C");
        Foreground = BrushOf("Brush.Text", "#FFE6E9EF");

        _filter = new TextBox
        {
            Margin = new Thickness(0, 0, 0, 6),
        };
        _filter.TextChanged += (_, _) => ApplyFilter();

        _status = new TextBlock
        {
            FontSize = 11,
            Foreground = BrushOf("Brush.TextFaint", "#FF5C6472"),
            Margin = new Thickness(0, 0, 0, 6),
            TextWrapping = TextWrapping.Wrap,
            Text = "Сканирую…",
        };

        _list = new ListBox
        {
            DisplayMemberPath = nameof(ScanChoice.Title),
            BorderThickness = new Thickness(1),
            BorderBrush = BrushOf("Brush.Stroke", "#FF262B33"),
            Background = BrushOf("Brush.Elevated", "#FF1B1F26"),
        };
        _list.MouseDoubleClick += (_, args) =>
        {
            if (_list.SelectedItem is ScanChoice)
            {
                args.Handled = true;
                ChooseSelection();
            }
        };

        _chooseButton = new Button
        {
            Content = "Выбрать",
            IsEnabled = false,
            MinWidth = 90,
            Margin = new Thickness(0, 0, 8, 0),
        };
        _chooseButton.Click += (_, _) => ChooseSelection();
        _list.SelectionChanged += (_, _) =>
            _chooseButton.IsEnabled = _list.SelectedItem is not null;

        _rescanButton = new Button { Content = "Пересканировать", MinWidth = 120 };
        _rescanButton.Click += async (_, _) => await ScanAsync(useCache: false);

        var cancelButton = new Button { Content = "Отмена", MinWidth = 80 };
        cancelButton.Click += (_, _) =>
        {
            Selected = null;
            DialogResult = false;
        };

        var bottom = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 10, 0, 0),
        };
        bottom.Children.Add(_rescanButton);
        bottom.Children.Add(_chooseButton);
        bottom.Children.Add(cancelButton);

        var layout = new DockPanel { Margin = new Thickness(12) };
        DockPanel.SetDock(bottom, Dock.Bottom);
        DockPanel.SetDock(_filter, Dock.Top);
        DockPanel.SetDock(_status, Dock.Top);
        layout.Children.Add(bottom);
        layout.Children.Add(_filter);
        layout.Children.Add(_status);
        layout.Children.Add(_list);
        Content = layout;

        Loaded += async (_, _) => await ScanAsync(useCache: true);
    }

    private async Task ScanAsync(bool useCache)
    {
        if (_scanning)
        {
            return;
        }

        _scanning = true;
        _rescanButton.IsEnabled = false;
        _status.Text = useCache
            ? "Сканирую (с кэшем)…"
            : "Пересканирую без кэша…";

        try
        {
            var result = await Task.Run(() => new PluginScanner().Scan(useCache: useCache));

            _all = result.Plugins
                .OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            _status.Text =
                $"Найдено: {_all.Count} (из кэша: {result.FromCache}, загружено: {result.Loaded}), " +
                $"ошибок: {result.Errors.Count}.";
            ApplyFilter();
        }
        catch (Exception ex)
        {
            _status.Text = "Ошибка сканирования: " + ex.Message;
        }
        finally
        {
            _scanning = false;
            _rescanButton.IsEnabled = true;
        }
    }

    private void ApplyFilter()
    {
        var query = _filter.Text.Trim();
        var choices = _all
            .Where(p =>
                query.Length == 0 ||
                p.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                p.Path.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                p.PluginId.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Select(p => new ScanChoice($"{p.Name}  ·  {p.Format}", p))
            .ToList();

        _list.ItemsSource = choices;
        _chooseButton.IsEnabled = _list.SelectedItem is not null;
    }

    private void ChooseSelection()
    {
        if (_list.SelectedItem is ScanChoice choice)
        {
            Selected = choice.Descriptor;
            DialogResult = true;
        }
    }

    private static Brush BrushOf(string key, string fallbackHex)
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
