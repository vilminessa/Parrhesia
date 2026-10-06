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

    event EventHandler<EngineLogEntry>? Log;

    /// <summary>Поднимается при старте/остановке. Счётчики опрашиваются через <see cref="Status"/>.</summary>
    event EventHandler? StatusChanged;

    void Start();

    void Stop();
}
