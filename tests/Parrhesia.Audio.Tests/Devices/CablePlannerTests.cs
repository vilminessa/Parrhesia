using Parrhesia.Audio.Devices;

namespace Parrhesia.Audio.Tests.Devices;

/// <summary>
/// Менеджер кабелей (В1): чистая логика очереди правок, сверки с фактом
/// и построения строк списка.
/// </summary>
public class CablePlannerTests
{
    private static CableActual Cable(string hwid, string suffix, string inName, string outName) =>
        new()
        {
            InstanceId = $@"ROOT\MEDIA\{suffix}",
            Hwid = hwid,
            InId = "{0.0.0.00000000}." + "{in-" + suffix + "}",
            InName = inName,
            OutId = "{0.0.1.00000000}." + "{out-" + suffix + "}",
            OutName = outName,
        };

    [Fact]
    public void NextFreeHwid_FirstLaneThenNext_SkipsInstalled()
    {
        Assert.Equal(@"ROOT\ParrhesiaLane1", CablePlanner.NextFreeHwid([]));

        var installed = new[] { @"ROOT\VirtualAudioDriver", @"ROOT\ParrhesiaLane1" };
        Assert.Equal(@"ROOT\ParrhesiaLane2", CablePlanner.NextFreeHwid(installed));
    }

    [Fact]
    public void NextFreeHwid_PendingAddsOccupySlots()
    {
        var installed = new[] { @"ROOT\ParrhesiaLane1" };
        var pending = new[] { @"ROOT\ParrhesiaLane2" };

        Assert.Equal(
            @"ROOT\ParrhesiaLane3",
            CablePlanner.NextFreeHwid(installed, pending));
    }

    [Fact]
    public void NextFreeHwid_AllSlotsBusy_ReturnsNull()
    {
        var installed = Enumerable.Range(1, CablePlanner.MaxCables - 1)
            .Select(i => $@"ROOT\ParrhesiaLane{i}")
            .ToArray();

        Assert.Null(CablePlanner.NextFreeHwid(installed));
    }

    [Fact]
    public void Reconcile_AppliedOpsDropped_PendingKept()
    {
        var actual = new[]
        {
            Cable(@"ROOT\VirtualAudioDriver", "0000", "Parrhesia In1", "Parrhesia Out1"),
            Cable(@"ROOT\ParrhesiaLane1", "0001", "Parrhesia In2", "Parrhesia Out2"),
        };

        var ops = new List<CableOp>
        {
            // add уже установлен → вычищается
            new() { Op = CableOp.OpAdd, Hwid = @"ROOT\ParrhesiaLane1", Desc = "Parrhesia L1" },
            // remove устройства нет → вычищается
            new() { Op = CableOp.OpRemove, Hwid = @"ROOT\ParrhesiaLane7" },
            // rename совпал с фактом → вычищается
            new() { Op = CableOp.OpRename, EndpointId = actual[0].InId, NewName = "Parrhesia In1" },
            // rename НЕ совпал → остаётся
            new() { Op = CableOp.OpRename, EndpointId = actual[1].InId, NewName = "Мой кабель" },
        };

        var kept = CablePlanner.Reconcile(actual, ops);

        var op = Assert.Single(kept);
        Assert.Equal(CableOp.OpRename, op.Op);
        Assert.Equal("Мой кабель", op.NewName);
    }

    [Fact]
    public void BuildRows_NoOps_AllApplied()
    {
        var actual = new[]
        {
            Cable(@"ROOT\VirtualAudioDriver", "0000", "Parrhesia In1", "Parrhesia Out1"),
            Cable(@"ROOT\ParrhesiaLane1", "0001", "Parrhesia In2", "Parrhesia Out2"),
        };

        var rows = CablePlanner.BuildRows(actual, []);

        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal(CableRowState.Applied, r.State));
        Assert.Equal(1, rows[0].Lane);
        Assert.Equal(2, rows[1].Lane);
        Assert.Equal(string.Empty, rows[0].Badge);
    }

    [Fact]
    public void BuildRows_PendingStates_PrioritizeRemoveAndAppendAdd()
    {
        var actual = new[]
        {
            Cable(@"ROOT\VirtualAudioDriver", "0000", "Parrhesia In1", "Parrhesia Out1"),
            Cable(@"ROOT\ParrhesiaLane1", "0001", "Parrhesia In2", "Parrhesia Out2"),
        };

        var ops = new List<CableOp>
        {
            new() { Op = CableOp.OpRemove, Hwid = @"ROOT\ParrhesiaLane1" },
            // rename по кабелю, который удаляется — remove приоритетнее
            new() { Op = CableOp.OpRename, EndpointId = actual[1].InId, NewName = "Х" },
            new() { Op = CableOp.OpRename, EndpointId = actual[0].InId, NewName = "Тихий" },
            new() { Op = CableOp.OpAdd, Hwid = @"ROOT\ParrhesiaLane3", Desc = "Parrhesia L3" },
        };

        var rows = CablePlanner.BuildRows(actual, ops);

        Assert.Equal(3, rows.Count);

        // rename → оранжевый PendingRename с новым именем
        Assert.Equal(CableRowState.PendingRename, rows[0].State);
        Assert.Equal("Тихий", rows[0].InName);
        Assert.Equal("переименуется", rows[0].Badge);

        // remove приоритетнее rename: статус — удаление, имя фактическое
        Assert.Equal(CableRowState.PendingRemove, rows[1].State);
        Assert.Equal("Parrhesia In2", rows[1].InName);
        Assert.Equal("удалится", rows[1].Badge);

        // добавление — новая строка в конце
        Assert.Equal(CableRowState.PendingAdd, rows[2].State);
        Assert.Equal(3, rows[2].Lane);
        Assert.Equal("добавится", rows[2].Badge);
    }

    [Fact]
    public void BuildRows_RenameOnMissingEndpoint_IgnoredForRows()
    {
        var actual = new[]
        {
            Cable(@"ROOT\VirtualAudioDriver", "0000", "Parrhesia In1", "Parrhesia Out1"),
        };
        var ops = new List<CableOp>
        {
            new() { Op = CableOp.OpRename, EndpointId = "{0.0.0.00000000}.{ghost}", NewName = "Призрак" },
        };

        var rows = CablePlanner.BuildRows(actual, ops);

        var row = Assert.Single(rows);
        Assert.Equal(CableRowState.Applied, row.State);
        Assert.Equal("Parrhesia In1", row.InName);
    }
}
