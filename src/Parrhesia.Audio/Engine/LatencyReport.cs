namespace Parrhesia.Audio.Engine;

/// <summary>Уровень итоговой задержки: ok ≤70мс, warn ≤120мс, bad >120мс.</summary>
public enum LatencyLevel
{
    Ok,
    Warn,
    Bad,
}

/// <summary>Один этап тракта: название, вклад в задержку (мс), деталь.</summary>
public sealed record LatencyStage(string Name, double Ms, string Detail);

/// <summary>
/// Поэтапный отчёт задержки тракта (M-волна): захват → кольца → кабель →
/// микшер → выводы. Строится снимком вне RT-пути; UI и лог читают раз в секунду/5 секунд.
/// </summary>
public sealed record LatencyReport(
    IReadOnlyList<LatencyStage> Stages,
    double TotalMs,
    LatencyLevel Level,
    IReadOnlyList<string> Issues)
{
    /// <summary>Сумма этапов выше этой величины — «жёлтый».</summary>
    public const double WarnAboveMs = 70;

    /// <summary>Сумма этапов выше этой величины — «красный».</summary>
    public const double BadAboveMs = 120;

    /// <summary>Один этап выше этой величины — не ниже «жёлтого».</summary>
    public const double StageWarnAboveMs = 50;

    public LatencyStage? Slowest
    {
        get
        {
            LatencyStage? best = null;
            foreach (var stage in Stages)
            {
                if (best is null || stage.Ms > best.Ms)
                {
                    best = stage;
                }
            }

            return best;
        }
    }

    /// <summary>
    /// Собирает отчёт: total = сумма вкладов этапов; серьёзные события
    /// (потери данных) передаются флагом <paramref name="seriousIssues"/> → Bad.
    /// </summary>
    public static LatencyReport Build(
        IReadOnlyList<LatencyStage> stages,
        IReadOnlyList<string> issues,
        bool seriousIssues = false)
    {
        double total = 0;
        var stageWarn = false;
        foreach (var stage in stages)
        {
            var ms = Math.Max(0, stage.Ms);
            total += ms;
            if (ms > StageWarnAboveMs)
            {
                stageWarn = true;
            }
        }

        var level = total <= WarnAboveMs
            ? LatencyLevel.Ok
            : total <= BadAboveMs
                ? LatencyLevel.Warn
                : LatencyLevel.Bad;

        if (stageWarn && level == LatencyLevel.Ok)
        {
            level = LatencyLevel.Warn;
        }

        if (issues.Count > 0 && level == LatencyLevel.Ok)
        {
            level = LatencyLevel.Warn;
        }

        if (seriousIssues)
        {
            level = LatencyLevel.Bad;
        }

        return new LatencyReport(stages, total, level, issues);
    }
}
