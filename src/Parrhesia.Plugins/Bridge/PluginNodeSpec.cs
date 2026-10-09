namespace Parrhesia.Plugins.Bridge;

/// <summary>
/// Спецификация узла-плагина для процесса-исполнителя: хост пишет файл
/// во время Prepare, ребёнок читает и грузит модуль. (Аргументами не передаём —
/// пути плагинов содержат пробелы и кавычки.)
/// </summary>
public sealed class PluginNodeSpec
{
    /// <summary>Формат: "Vst3" | "Clap" (PluginFormat, строкой).</summary>
    public string Format { get; set; } = string.Empty;

    /// <summary>Абсолютный путь к модулю плагина.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>Id плагина в модуле (CLAP id / VST3 class id).</summary>
    public string PluginId { get; set; } = string.Empty;

    /// <summary>Отображаемое имя (диагностика).</summary>
    public string Name { get; set; } = string.Empty;

    public int SampleRate { get; set; }

    public int MaxBlockFrames { get; set; }

    public int Channels { get; set; }
}
