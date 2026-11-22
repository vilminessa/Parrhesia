namespace Parrhesia.Audio.Processing;

/// <summary>
/// S-волна: снимок состояния узла-плагина для UI-инспектора — жив ли
/// процесс-исполнитель, сколько попыток ушло, насколько отстаёт аудио.
/// I-волна: агрегат дополнен по-слотовым снимком <see cref="Slots"/> —
/// узел может нести несколько процессов-исполнителей, и один pid/drops
/// на узел врал.
/// </summary>
/// <remarks>
/// Семантика агрегатов при нескольких слотах: <see cref="Loaded"/> — любой
/// слот загружен; <see cref="Alive"/> — все загруженные живы;
/// <see cref="ProcessId"/> — первый ненулевой; счётчики — суммы;
/// <see cref="LatencySamples"/> — худший (максимум);
/// <see cref="SecondsToRetry"/> — минимум из ожидающих ретрая.
/// </remarks>
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
    int SecondsToRetry,

    /// <summary>Состояние каждого слота узла (индекс = позиции в модели).</summary>
    IReadOnlyList<SlotStatus> Slots);

/// <summary>Состояние одного слота-вставки и его процесса-исполнителя.</summary>
public sealed record SlotStatus(
    int Index,
    string Name,
    bool Loaded,
    bool Alive,
    int ProcessId,
    long Drops,
    long ProcessedBlocks,
    int LatencySamples,
    int SpawnAttempts,
    int SecondsToRetry,

    /// <summary>Последняя ошибка загрузки/спавна этого слота (null — нет).</summary>
    string? Error);
