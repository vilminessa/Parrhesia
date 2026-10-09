using Parrhesia.Audio.Devices;

namespace Parrhesia.Audio.Tests.Devices;

/// <summary>
/// Перепривязка профиля по имени (E3): выбор кандидата должен быть
/// строгим — ровно одно совпадение, без чувствительности к регистру.
/// </summary>
public class DeviceNameCacheTests
{
    [Fact]
    public void PickUniqueByName_SingleMatch_IgnoresCase()
    {
        var candidates = new (string Id, string Name)[]
        {
            ("{0.0.0.00000000}.{aaa}", "Динамики"),
            ("{0.0.0.00000000}.{bbb}", "Parrhesia In1"),
        };

        Assert.Equal(
            "{0.0.0.00000000}.{bbb}",
            DeviceNameCache.PickUniqueByName(candidates, "parrhesia in1"));
    }

    [Fact]
    public void PickUniqueByName_NoMatch_ReturnsNull()
    {
        var candidates = new (string Id, string Name)[]
        {
            ("{0.0.0.00000000}.{aaa}", "Динамики"),
        };

        Assert.Null(DeviceNameCache.PickUniqueByName(candidates, "Микрофон"));
    }

    [Fact]
    public void PickUniqueByName_Ambiguous_ReturnsNull()
    {
        // Два «Динамики» — перепривязывать нельзя (не тот устройство).
        var candidates = new (string Id, string Name)[]
        {
            ("{0.0.0.00000000}.{aaa}", "Динамики"),
            ("{0.0.0.00000000}.{bbb}", "Динамики"),
        };

        Assert.Null(DeviceNameCache.PickUniqueByName(candidates, "Динамики"));
    }

    [Fact]
    public void PickUniqueByName_EmptyCandidates_ReturnsNull() =>
        Assert.Null(DeviceNameCache.PickUniqueByName([], "X"));
}
