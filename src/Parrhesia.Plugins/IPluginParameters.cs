namespace Parrhesia.Plugins;

/// <summary>
/// Описание параметра плагина — единая модель для CLAP и VST3.
/// Id стабилен на время жизни экземпляра (CLAP param id / VST3 ParamID).
/// </summary>
public sealed record PluginParameter(
    int Id,
    string Name,
    string Module,
    double Min,
    double Max,
    double Default,
    bool IsStepped,
    bool IsReadOnly,
    bool IsHidden);

/// <summary>
/// Параметры плагина (опциональная возможность <see cref="IAudioPlugin"/>).
/// Контракт потоков: Get*/Format — только UI-поток; Set — UI-поток, значение
/// доставляется в аудио-поток очередью (не блокирует вызывающего и не блокирует
/// аудио-поток). Реализации: CLAP (clap.params + события в process/flush),
/// VST3 (IEditController через шим).
/// </summary>
public interface IPluginParameters
{
    /// <summary>Описание параметров (кэшируется при первом вызове; пусто — плагин без params).</summary>
    IReadOnlyList<PluginParameter> GetParameters();

    /// <summary>Текущее значение (main thread; источник — плагин).</summary>
    double GetParameterValue(int id);

    /// <summary>Установить значение (клампится в [Min, Max]; доставка — в ближайший Process/flush).</summary>
    void SetParameterValue(int id, double value);

    /// <summary>Текстовое представление значения (value_to_text плагина; fallback — числовой формат).</summary>
    string FormatParameterValue(int id, double value);
}
