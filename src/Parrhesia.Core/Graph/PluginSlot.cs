namespace Parrhesia.Core.Graph;

/// <summary>Формат аудио-плагина.</summary>
public enum PluginFormat
{
    /// <summary>CLAP (MIT, заголовки вендорятся в репозиторий).</summary>
    Clap,

    /// <summary>VST3 (официальный SDK Steinberg — внешняя зависимость).</summary>
    Vst3,
}

/// <summary>
/// Слот-вставка эффекта внутри шины: обрабатывает суммарный сигнал узла
/// до его стадии (gain → метр → mute). Порядок слотов в цепочке =
/// порядок обработки. Поле State — чанк состояния плагина, он хранится
/// в профиле и переживает перезапуск приложения.
/// </summary>
public sealed record PluginSlot
{
    public PluginFormat Format { get; init; }

    /// <summary>Путь к модулю плагина (файл .clap/.dll/.vst3).</summary>
    public string Path { get; init; } = string.Empty;

    /// <summary>Стабильный id внутри модуля (CLAP id / VST3 class id).</summary>
    public string PluginId { get; init; } = string.Empty;

    /// <summary>Отображаемое имя (заполняется сканером).</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// Слот включён. Выключенный слот обходится без вызова плагина —
    /// это не то же самое, что обход всего узла (Bypassed).
    /// </summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Состояние плагина (state-чанк); null — ещё не сохранялось.</summary>
    public byte[]? State { get; init; }
}
