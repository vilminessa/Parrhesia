using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Parrhesia.Plugins;

namespace Parrhesia.Executor;

/// <summary>
/// Окно редактора плагина в процессе-исполнителе (P-волна): Win32-контейнер
/// (HwndHost), в него встраивается окно плагина (VST3 IPlugView / CLAP GUI).
/// Протокол: Loaded → Open(дочерний HWND) → размер по PreferredSize →
/// Closed → Close. sync — gate процесса: Open/Close не параллельно с process.
/// </summary>
internal sealed class EditorHostWindow : Window
{
    private readonly IPluginEditor _editor;
    private readonly HostPanel _host;
    private readonly object? _sync;
    private bool _opened;

    public EditorHostWindow(IPluginEditor editor, string title, object? sync)
    {
        _editor = editor;
        _sync = sync;
        Title = "Редактор: " + title;
        Width = 440;
        Height = 240;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ResizeMode = ResizeMode.NoResize;
        Background = Solid("#FF14171C");

        _host = new HostPanel();
        Content = new Border
        {
            Child = _host,
            Padding = new Thickness(4),
            Background = Background,
        };

        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var opened = _editor.SupportsEditor &&
            (_sync is null
                ? _editor.Open(_host.Handle)
                : WithSync(() => _editor.Open(_host.Handle)));
        if (!opened)
        {
            MessageBox.Show(
                this,
                "У плагина нет редактора (или он отказал в открытии).",
                Title,
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Close();
            return;
        }

        _opened = true;
        var (width, height) = _editor.PreferredSize;
        if (width > 0 && height > 0)
        {
            Width = width + 26;
            Height = height + 66;
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        if (!_opened)
        {
            return;
        }

        try
        {
            if (_sync is null)
            {
                _editor.Close();
            }
            else
            {
                WithSync(() =>
                {
                    _editor.Close();
                    return true;
                });
            }
        }
        catch
        {
            // Закрытие редактора не должно валить процесс.
        }
    }

    private T WithSync<T>(Func<T> action)
    {
        lock (_sync!)
        {
            return action();
        }
    }

    private static Brush Solid(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)!);
        brush.Freeze();
        return brush;
    }

    /// <summary>Чистое Win32-дочернее окно-контейнер для окна плагина.</summary>
    private sealed class HostPanel : HwndHost
    {
        private const uint WsChild = 0x40000000;
        private const uint WsVisible = 0x10000000;
        private const uint WsClipChildren = 0x02000000;

        protected override HandleRef BuildWindowCore(HandleRef hwndParent) =>
            new HandleRef(
                this,
                CreateWindowExW(
                    0,
                    "STATIC",
                    string.Empty,
                    WsChild | WsVisible | WsClipChildren,
                    0,
                    0,
                    0,
                    0,
                    hwndParent.Handle,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    IntPtr.Zero));

        protected override void DestroyWindowCore(HandleRef hwnd) => DestroyWindow(hwnd.Handle);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateWindowExW(
            uint exStyle,
            string className,
            string windowName,
            uint style,
            int x,
            int y,
            int width,
            int height,
            IntPtr parent,
            IntPtr menu,
            IntPtr instance,
            IntPtr param);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DestroyWindow(IntPtr hwnd);
    }
}
