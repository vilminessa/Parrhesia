using System.Globalization;
using System.Windows.Data;

namespace Parrhesia.App.Themes;

/// <summary>
/// Отображение выбранного значения ComboBox: строки — как есть, объекты
/// с свойством Name — по нему (DeviceChoice), иначе ToString.
/// Без этого показывался record.ToString() → «DeviceChoice {Name = …».
/// </summary>
public sealed class DisplayTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        PickDisplayText(value);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    /// <summary>Чистое правило выбора текста (юнит-тестируемо).</summary>
    public static string PickDisplayText(object? value)
    {
        if (value is null)
        {
            return string.Empty;
        }

        if (value is string text)
        {
            return text;
        }

        var name = value.GetType().GetProperty("Name")?.GetValue(value) as string;
        return string.IsNullOrWhiteSpace(name) ? value.ToString() ?? string.Empty : name;
    }
}
