using Parrhesia.Core.Graph;
using Parrhesia.Plugins;

namespace Parrhesia.Audio.Engine;

/// <summary>
/// Аудио-движок: превращает граф маршрутизации в живой звуковой тракт
/// (захват устройств → микширование → вывод).
/// </summary>
public interface IAudioEngine : IDisposable
{
    AudioGraph Graph { get; }

    /// <summary>Текущее состояние (потокобезопасный снимок, xrun-счётчики включены).</summary>
    EngineStatus Status { get; }

    /// <summary>Пик (linear, |x|) последнего блока узла — для метров UI. Потокобезопасно.</summary>
    float GetPeak(Guid nodeId);

    event EventHandler<EngineLogEntry>? Log;

    /// <summary>Поднимается при старте/остановке. Счётчики опрашиваются через <see cref="Status"/>.</summary>
    event EventHandler? StatusChanged;

    void Start();

    void Stop();

    /// <summary>
    /// Снимает состояние плагинов (слоты шин) в модель графа — вызывать
    /// перед сохранением профиля, чтобы параметры пережили перезапуск.
    /// </summary>
    void CollectPluginStates();

    /// <summary>
    /// Устройство мониторинга: реальный вывод, звучащий параллельно виртуальному снику
    /// (каждый выход тянет свой блок из GraphProcessor — ядро не меняется).
    /// null/пусто — выкл. Хранение настройки — на стороне UI (AppSettings);
    /// вызов при работающем движке меняет только монитор, тракт не перезапускается.
    /// </summary>
    void SetMonitorDevice(string? deviceId);

    /// <summary>
    /// Явная частота движка (null — авто-выбор: настройка → 48k при виртуальном снике →
    /// mix реального выхода → 48k). Смена при работающем движке перезапускает тракт.
    /// </summary>
    void SetSampleRate(int? configuredRate);

    /// <summary>Живой экземпляр слота шины (null — не загружен). UI-поток: редактор.</summary>
    IAudioPlugin? GetSlotInstance(Guid nodeId, int slotIndex);
}
