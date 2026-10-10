namespace Parrhesia.Audio.Processing;

/// <summary>
/// S-волна: снимок состояния узла-плагина для UI-инспектора — жив ли
/// процесс-исполнитель, сколько попыток ушло, насколько отстаёт аудио.
/// </summary>
public sealed record PluginNodeStatus(
    bool Loaded,
    bool Alive,
    int ProcessId,
    long Drops,
    long ProcessedBlocks,
    int SpawnAttempts,
    int LatencySamples,
    IReadOnlyList<string> Errors,

    /// <summary>Секунд до следующего ретрая спавна (≤0 — ретрай не запланирован).</summary>
    int SecondsToRetry);
