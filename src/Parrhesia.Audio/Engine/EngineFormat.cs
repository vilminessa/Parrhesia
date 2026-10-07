namespace Parrhesia.Audio.Engine;

/// <summary>
/// Правила формата движка (чистая логика, тестируется без WASAPI).
/// База: всегда float32 и 2 канала (ограничение GraphProcessor); частота —
/// либо настройка пользователя, либо авто-выбор.
/// Авто: виртуальный сник требует 48k (канон фида драйвера), иначе mix-частота
/// первого реального назначения, иначе 48k (чистый профиль/первый запуск).
/// </summary>
public static class EngineFormat
{
    /// <summary>Частота по умолчанию — канон фида (совпадает с DriverFeed.Rate).</summary>
    public const int DefaultRate = DriverFeed.Rate;

    /// <summary>Каналы движка: GraphProcessor работает только со стерео (см. план B3).</summary>
    public const int Channels = 2;

    private static readonly int[] SupportedRates = [44100, 48000, 88200, 96000];

    public static bool IsSupportedRate(int rate) => Array.IndexOf(SupportedRates, rate) >= 0;

    /// <summary>Частоты, доступные в настройке (для UI).</summary>
    public static IReadOnlyList<int> Rates => SupportedRates;

    /// <summary>
    /// Разбор значения настройки: "auto"/пусто/мусор → null (авто), поддержанная
    /// частота → она же; недопустимая частота → null (авто, а не отказ).
    /// </summary>
    public static int? ParseSetting(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        if (string.Equals(trimmed, "auto", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return int.TryParse(trimmed, out var rate) && IsSupportedRate(rate) ? rate : null;
    }

    /// <summary>Итоговая частота движка: настройка → виртуал (48k) → mix реального → 48k.</summary>
    public static int Resolve(int? configuredRate, bool hasVirtualSink, int? firstRealMixRate)
    {
        if (configuredRate is int configured && IsSupportedRate(configured))
        {
            return configured;
        }

        if (hasVirtualSink)
        {
            return DriverFeed.Rate;
        }

        if (firstRealMixRate is int rate && rate > 0)
        {
            return rate;
        }

        return DriverFeed.Rate;
    }
}
