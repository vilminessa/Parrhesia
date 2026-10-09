using System.Windows;
using Parrhesia.App.Themes;

namespace Parrhesia.App.Tests.Themes;

/// <summary>Адаптивное скругление (D-волна): радиус темы клампится к размеру элемента.</summary>
public class AdaptiveCornerTests
{
    [Theory]
    [InlineData(15, 300, 200, 15)]  // Крупная панель: радиус темы не меняется
    [InlineData(12, 26, 26, 12)]     // Чип26:12 ≤ 26/2−1=12 — остаётся
    [InlineData(15, 21, 21, 9.5)]    // Пилюля: радиус Liquid жмётся к высоте
    [InlineData(26, 30, 30, 14)]     // Экстремальный радиус — строго половина
    [InlineData(26, 0, 0, 0)]        // До первого layout —0 (применится по SizeChanged)
    [InlineData(8, 24, 200, 8)]      // Высокий узкий элемент — по меньшей стороне
    public void ClampFor_LimitsRadiusBySmallerSide(double radius, double width, double height, double expected)
    {
        var clamped = AdaptiveCorner.ClampFor(new CornerRadius(radius), width, height);

        Assert.Equal(expected, clamped.TopLeft);
        Assert.Equal(expected, clamped.TopRight);
        Assert.Equal(expected, clamped.BottomLeft);
        Assert.Equal(expected, clamped.BottomRight);
    }

    [Fact]
    public void ClampFor_KeepsAsymmetricCorners()
    {
        var clamped = AdaptiveCorner.ClampFor(new CornerRadius(20, 2, 6, 4), width: 30, height: 30);

        Assert.Equal(14, clamped.TopLeft);  // 30/2−1
        Assert.Equal(2, clamped.TopRight);
        Assert.Equal(6, clamped.BottomRight);
        Assert.Equal(4, clamped.BottomLeft);
    }
}
