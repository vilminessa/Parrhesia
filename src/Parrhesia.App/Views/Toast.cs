using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace Parrhesia.App.Views;

/// <summary>
/// Тост-уведомления (U1): маленькая плашка в правом нижнем углу нашего окна,
/// гаснет сама. Последнее сообщение заменяет предыдущее (очередь не нужна —
/// важен свежий статус). Показывать из UI-потока или через Show из любого.
/// </summary>
internal static class Toast
{
    private static Window? _window;
    private static TextBlock? _text;
    private static DispatcherTimer? _timer;

    public static void Show(string message, TimeSpan? duration = null)
    {
        if (Application.Current?.Dispatcher is not { } dispatcher)
        {
            return;
        }

        if (dispatcher.CheckAccess())
        {
            ShowCore(message, duration);
        }
        else
        {
            dispatcher.Invoke(() => ShowCore(message, duration));
        }
    }

    private static void ShowCore(string message, TimeSpan? duration)
    {
        var owner = Application.Current?.Windows.OfType<MainWindow>().FirstOrDefault();
        if (owner is null)
        {
            return;
        }

        if (_window is null || _text is null)
        {
            _text = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(12, 9, 12, 9),
                Foreground = BrushOf("Brush.Text", "#FFE6E9EF"),
                FontSize = 12,
            };

            _window = new Window
            {
                SizeToContent = SizeToContent.WidthAndHeight,
                MaxWidth = 340,
                Content = new Border
                {
                    Background = BrushOf("Brush.Elevated", "#FF1B1F26"),
                    BorderBrush = BrushOf("Brush.Stroke", "#FF262B33"),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(8),
                    Child = _text,
                },
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                ShowActivated = false, // не воруем фокус у юзера
                Owner = owner,
            };
        }

        _text.Text = message;
        if (!_window.IsVisible)
        {
            PositionOver(owner);
            _window.Show();
        }

        _timer ??= new DispatcherTimer();
        _timer.Stop();
        _timer.Interval = duration ?? TimeSpan.FromSeconds(3);
        _timer.Tick -= OnTimerTick;
        _timer.Tick += OnTimerTick;
        _timer.Start();
    }

    private static void OnTimerTick(object? sender, EventArgs e)
    {
        _timer?.Stop();
        _window?.Hide();
    }

    /// <summary>Нижний правый угол owner-окна (свёрнутое owner-окно утащит и тост — корректно).</summary>
    private static void PositionOver(Window owner)
    {
        if (_window is null)
        {
            return;
        }

        _window.Left = owner.Left + owner.ActualWidth - _window.ActualWidth - 16;
        _window.Top = owner.Top + owner.ActualHeight - _window.ActualHeight - 48;
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
