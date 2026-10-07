namespace Parrhesia.Audio.Engine;

public enum EngineLogLevel
{
    Info,
    Warning,
    Error,
}

public sealed record EngineLogEntry(DateTime Timestamp, EngineLogLevel Level, string Message);

/// <summary>Снимок состояния движка (собирается по запросу, потокобезопасно).</summary>
public sealed record EngineStatus(
    bool IsRunning,
    int SampleRate,
    int Channels,
    string? SinkName,
    int ActiveSources,
    long UnderrunSamples,
    long OverflowSamples)
{
    public static readonly EngineStatus Stopped = new(false, 0, 0, null, 0, 0, 0);

    /// <summary>Имя устройства мониторинга; null — мониторинг выключен или задан, но не запущен.</summary>
    public string? MonitorName { get; init; }

    /// <summary>Монитор-плеер сейчас играет параллельно основному выходу.</summary>
    public bool MonitorActive { get; init; }

    /// <summary>Все открытые выходы (мультисинки); пусто — движок остановлен.</summary>
    public IReadOnlyList<string> SinkNames { get; init; } = [];
}
