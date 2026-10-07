namespace Parrhesia.Audio.Engine;

/// <summary>
/// Правила мониторинга (чистая логика, тестируется без WASAPI):
/// монитор — реальный вывод, звучащий параллельно виртуальному снику, пока
/// основной выход уходит в драйвер. Когда основной выход и так реальный,
/// мониторинг избыточен — включается только для виртуального сника.
/// </summary>
public static class MonitorOutput
{
    /// <summary>Нормализация идентификатора: null/пусто/пробелы — «монитор выключен».</summary>
    public static string? NormalizeDeviceId(string? deviceId) =>
        string.IsNullOrWhiteSpace(deviceId) ? null : deviceId.Trim();

    /// <summary>Стоит ли запускать монитор-плеер при текущих настройках.</summary>
    public static bool ShouldStart(string? deviceId, bool sinkIsVirtual) =>
        NormalizeDeviceId(deviceId) is not null && sinkIsVirtual;
}
