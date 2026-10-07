using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Parrhesia.Plugins;

namespace Parrhesia.App.Views;

/// <summary>
/// Окно редактора плагина: Win32-контейнер (HwndHost) внутри WPF-окна,
/// в него встраивается окно плагина (CLAP set_parent / VST3 IPlugView).
/// Протокол: Loaded → Open(дочерний HWND) → размер по PreferredSize →
/// Closed → Close(). Показывает подсказку, если редактора нет.
/// </summary>
public sealed class PluginEditorWindow : Window
{
    private readonly IPluginEditor _editor;
    private readonly NativeHostPanel _host;
    private bool _opened;

    public PluginEditorWindow(IPluginEditor editor, string title)
    {
        _editor = editor;
        Title = "Редактор: " + title;
        Width = 440;
        Height = 240;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize; // MVP: фиксированный размер под плагин
        Background = BrushOf("Brush.Panel", "#FF14171C");

        _host = new NativeHostPanel();
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
        if (!_editor.SupportsEditor || !_editor.Open(_host.Handle))
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
            // Подгоняем окно под контент (MVP: без учёта DPI-рамок — погрешность не критична).
            Width = width + 26;
            Height = height + 66;
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        if (_opened)
        {
            try
            {
                _editor.Close();
            }
            catch
            {
                // Закрытие редактора не должно валить приложение.
            }
        }
    }

    /// <summary>Чистое Win32-дочернее окно-контейнер для окна плагина.</summary>
    private sealed class NativeHostPanel : HwndHost
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
