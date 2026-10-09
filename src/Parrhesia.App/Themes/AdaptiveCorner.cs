using System.Windows;
using System.Windows.Controls;

namespace Parrhesia.App.Themes;

/// <summary>
/// Адаптивное скругление (D-волна): CornerRadius = min(тема-значение, размер/2 − 1).
/// Крупные панели остаются с заданным радиусом темы; у мелких элементов
/// (чипы, пилюли, карточки) дуга никогда не доедает текст — в Liquid-теме
/// с её большими радиусами это было заметно.
/// </summary>
public static class AdaptiveCorner
{
    public static readonly DependencyProperty RadiusProperty =
        DependencyProperty.RegisterAttached(
            "Radius",
            typeof(CornerRadius),
            typeof(AdaptiveCorner),
            new PropertyMetadata(new CornerRadius(), OnRadiusChanged));

    public static void SetRadius(DependencyObject element, CornerRadius value) =>
        element.SetValue(RadiusProperty, value);

    public static CornerRadius GetRadius(DependencyObject element) =>
        (CornerRadius)element.GetValue(RadiusProperty);

    private static void OnRadiusChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element)
        {
            return;
        }

        element.SizeChanged -= OnElementSizeChanged;
        element.SizeChanged += OnElementSizeChanged;
        Apply(element);
    }

    private static void OnElementSizeChanged(object sender, SizeChangedEventArgs e) =>
        Apply((FrameworkElement)sender);

    private static void Apply(FrameworkElement element)
    {
        var radius = GetRadius(element);
        var clamped = ClampFor(radius, element.ActualWidth, element.ActualHeight);
        if (element is Border border && border.CornerRadius != clamped)
        {
            border.CornerRadius = clamped;
        }
    }

    /// <summary>Кламп радиуса к размеру элемента (пурия-функция, для тестов).</summary>
    internal static CornerRadius ClampFor(CornerRadius radius, double width, double height)
    {
        var max = Math.Min(width, height) / 2 - 1;
        if (!double.IsNaN(max) && max >= 0)
        {
            return new CornerRadius(
                Math.Min(radius.TopLeft, max),
                Math.Min(radius.TopRight, max),
                Math.Min(radius.BottomRight, max),
                Math.Min(radius.BottomLeft, max));
        }

        return new CornerRadius(0);
    }
}
