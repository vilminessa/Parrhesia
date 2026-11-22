namespace Parrhesia.Plugins;

/// <summary>
/// Наблюдаемость процесса-исполнителя слота (I-волна): UI-статус карточки,
/// инспектор узла и отчёт диагностики читают её у живого экземпляра, не
/// привязываясь к конкретному классу моста — тесты подменяют фейками.
/// Поток: UI/фоновый тик (счётчики читаются без лока).
/// </summary>
public interface IPluginProcessInfo
{
    /// <summary>Ребёнок жив по heartbeat моста.</summary>
    bool ChildAlive { get; }

    /// <summary>Pid процесса-исполнителя (0 — не запущен).</summary>
    int ProcessId { get; }

    /// <summary>Пропущенные (dry) блоки — ребёнок не успевал/умер.</summary>
    long Drops { get; }

    /// <summary>Блоков реально обработано исполнителем.</summary>
    long ProcessedBlocks { get; }

    /// <summary>Задержка моста в кадрах.</summary>
    int LatencySamples { get; }

    /// <summary>Исполнитель умер/завис либо запрошен ручной рестарт.</summary>
    bool NeedsRestart { get; }
}
