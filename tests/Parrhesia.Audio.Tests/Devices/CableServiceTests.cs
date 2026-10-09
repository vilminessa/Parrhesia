using Parrhesia.Audio.Devices;

namespace Parrhesia.Audio.Tests.Devices;

/// <summary>
/// Фактический состав кабелей (В1): читает реестр/PnP этой машины.
/// Без установленного драйвера (CI) — пусто и это валидно.
/// </summary>
public class CableServiceTests
{
    [Fact]
    public void GetInstalled_HwidsAreDriverRoots_AndUnique()
    {
        var cables = CableService.GetInstalled();
        if (cables.Count == 0)
        {
            return; // драйвера нет (CI)
        }

        Assert.All(cables, c => Assert.StartsWith(@"ROOT\", c.Hwid));
        Assert.Equal(
            cables.Count,
            cables.Select(c => c.Hwid).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void GetInstalled_EndpointPairsAreWellFormed()
    {
        var cables = CableService.GetInstalled();
        if (cables.Count == 0)
        {
            return; // драйвера нет (CI)
        }

        foreach (var c in cables)
        {
            if (c.InId.Length == 0 && c.OutId.Length == 0)
            {
                continue; // devnode без endpoints (окно пересоздания)
            }

            // Пара: render "{0.0.0…}" + capture "{0.0.1…}", имена прочитаны.
            Assert.StartsWith("{0.0.0.", c.InId, StringComparison.OrdinalIgnoreCase);
            Assert.StartsWith("{0.0.1.", c.OutId, StringComparison.OrdinalIgnoreCase);
            Assert.NotEmpty(c.InName);
            Assert.NotEmpty(c.OutName);
        }
    }

    [Fact]
    public void GetInstalled_IsStableAcrossCalls()
    {
        var first = CableService.GetInstalled();
        var second = CableService.GetInstalled();

        Assert.Equal(
            first.Select(c => (c.InstanceId, c.Hwid, c.InId, c.OutId)),
            second.Select(c => (c.InstanceId, c.Hwid, c.InId, c.OutId)));
    }
}
