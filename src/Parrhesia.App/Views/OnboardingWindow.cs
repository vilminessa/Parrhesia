using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Parrhesia.App.Views;

/// <summary>Приветственные подсказки первого запуска (U3) — четыре коротких шага.</summary>
internal sealed class OnboardingWindow : Window
{
    public OnboardingWindow()
    {
        Title = "Добро пожаловать";
        Width = 540;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ResizeMode = ResizeMode.NoResize;
        Background = BrushOf("Brush.Panel", "#FF14171C");

        var root = new StackPanel();
        root.Children.Add(new TextBlock
        {
            Text = "Parrhesia — модульный аудио-миксер",
            FontSize = 16,
            FontWeight = FontWeights.Bold,
            Foreground = BrushOf("Brush.Text", "#FFE6E9EF"),
            Margin = new Thickness(20, 16, 20, 4),
        });

        (string Title, string Body)[] steps =
        [
            ("1. Вывод", "Вкладка «Устройства»: выбери, во что идёт звук (наушники/колонки)."),
            ("2. Приложения", "Discord, браузер или игру направь в кабель «Parrhesia In» — их звук появится отдельной полосой в микшере."),
            ("3. Микрофон и плагины", "Микрофон — палитра «Захват устройства»; плагины — «Плагин…». Каждый VST3/CLAP работает в отдельном процессе и не может уронить приложение."),
            ("Готово", "Ctrl+Z вернёт последнее удаление; «Расставить узлы» приведёт схему в порядок; статусы — точками на пультах."),
        ];

        foreach (var (title, body) in steps)
        {
            root.Children.Add(new TextBlock
            {
                Text = title,
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = BrushOf("Brush.Cyan", "#FF35D0C8"),
                Margin = new Thickness(20, 12, 20, 2),
            });
            root.Children.Add(new TextBlock
            {
                Text = body,
                FontSize = 12,
                Foreground = BrushOf("Brush.Text", "#FFE6E9EF"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(20, 0, 20, 0),
            });
        }

        var suppress = new CheckBox
        {
            Content = "Больше не показывать",
            IsChecked = true,
            Foreground = BrushOf("Brush.Text", "#FFE6E9EF"),
            Margin = new Thickness(20, 16, 0, 0),
        };

        var ok = new Button
        {
            Content = "Понятно",
            MinWidth = 110,
            Margin = new Thickness(20, 12, 20, 18),
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        ok.Click += (_, _) =>
        {
            Suppress = suppress.IsChecked == true;
            Close();
        };

        root.Children.Add(suppress);
        root.Children.Add(ok);
        Content = root;
    }

    /// <summary>Юзер оставил «Больше не показывать» — вызывающая сторона сохраняет в настройки.</summary>
    public bool Suppress { get; private set; }

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
