using Parrhesia.Core.Graph;

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
}
