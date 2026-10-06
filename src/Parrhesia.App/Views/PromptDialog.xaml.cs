using System.Windows;

namespace Parrhesia.App.Views;

/// <summary>Модальный ввод строки (имя пресета и т.п.). Esc/Отмена — отказ.</summary>
public partial class PromptDialog : Window
{
    public PromptDialog(string title, string? defaultValue)
    {
        InitializeComponent();
        TitleText.Text = title;
        ValueBox.Text = defaultValue ?? string.Empty;
        Loaded += (_, _) =>
        {
            ValueBox.Focus();
            ValueBox.SelectAll();
        };
    }

    public string Value => ValueBox.Text.Trim();

    public static bool Show(Window? owner, string title, string? defaultValue, out string value)
    {
        var dialog = new PromptDialog(title, defaultValue);
        if (owner is not null)
        {
            dialog.Owner = owner;
        }

        var accepted = dialog.ShowDialog() == true;
        value = dialog.Value;
        return accepted && value.Length > 0;
    }

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        if (Value.Length == 0)
        {
            ValueBox.Focus();
            return;
        }

        DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;
}
