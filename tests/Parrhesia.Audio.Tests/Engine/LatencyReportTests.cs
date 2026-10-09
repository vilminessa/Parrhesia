using Parrhesia.Audio.Engine;

namespace Parrhesia.Audio.Tests.Engine;

/// <summary>Поэтапный отчёт задержки (M-волна): сумма, пороги, проблемы.</summary>
public class LatencyReportTests
{
    [Fact]
    public void Build_SumsStages_AndAppliesThresholds()
    {
        var ok = LatencyReport.Build(
            [new("A", 10, ""), new("B", 30, "")],
            []);
        Assert.Equal(40, ok.TotalMs, 3);
        Assert.Equal(LatencyLevel.Ok, ok.Level);

        var warn = LatencyReport.Build(
            [new("A", 60, ""), new("B", 30, "")],
            []);
        Assert.Equal(90, warn.TotalMs, 3);
        Assert.Equal(LatencyLevel.Warn, warn.Level);

        var bad = LatencyReport.Build(
            [new("A", 80, ""), new("B", 60, "")],
            []);
        Assert.Equal(LatencyLevel.Bad, bad.Level);
    }

    [Fact]
    public void Build_SingleStageAbove50_NotLowerThanWarn()
    {
        var report = LatencyReport.Build(
            [new("A", 55, ""), new("B", 5, "")], // сумма60 ≤70, но этап>50
            []);
        Assert.Equal(LatencyLevel.Warn, report.Level);
    }

    [Fact]
    public void Build_Issues_RaiseLevel_SeriousForcesBad()
    {
        var withIssue = LatencyReport.Build([new("A", 10, "")], ["была проблема"]);
        Assert.Equal(LatencyLevel.Warn, withIssue.Level);

        var serious = LatencyReport.Build([new("A", 10, "")], ["потеря"], seriousIssues: true);
        Assert.Equal(LatencyLevel.Bad, serious.Level);
    }

    [Fact]
    public void Slowest_ReportsMaxStage()
    {
        var report = LatencyReport.Build(
            [new("A", 5, ""), new("B", 42, ""), new("C", 11, "")],
            []);
        Assert.NotNull(report.Slowest);
        Assert.Equal("B", report.Slowest!.Name);
        Assert.Equal(42, report.Slowest.Ms, 3);
    }

    [Fact]
    public void Build_NegativeMs_ClampedToZero()
    {
        var report = LatencyReport.Build([new("A", -3, "")], []);
        Assert.Equal(0, report.TotalMs, 3);
        Assert.Equal(LatencyLevel.Ok, report.Level);
    }
}
