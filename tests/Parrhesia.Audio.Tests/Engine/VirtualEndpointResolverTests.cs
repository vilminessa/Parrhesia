using Parrhesia.Audio.Engine;

namespace Parrhesia.Audio.Tests.Engine;

/// <summary>
/// Разрешение виртуальных эндпоинтов по InstanceId (М2): контракт —
/// не бросает исключений на любой машине и возвращает только MMDevice.ID.
/// На машине без установленного драйвера множество пусто (это валидно).
/// Одна коллекция с EndpointPolicyTests — тест видимости временно скрывает
/// endpoint (см. там).
/// </summary>
[Collection("AudioSystem")]
public class VirtualEndpointResolverTests
{
    [Fact]
    public void Resolve_DoesNotThrow_And_IdsLookLikeMmdevice()
    {
        var ids = VirtualEndpointResolver.ResolveVirtualEndpointIds();

        Assert.NotNull(ids);
        Assert.All(ids, id => Assert.StartsWith("{0.0.", id, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Resolve_IsStableAcrossCalls()
    {
        var first = VirtualEndpointResolver.ResolveVirtualEndpointIds();
        var second = VirtualEndpointResolver.ResolveVirtualEndpointIds();

        Assert.Equal(first, second);
    }

    [Fact]
    public void TryGetFeedSuffix_MatchesDriverDerivation()
    {
        var endpoints = VirtualEndpointResolver.ResolveVirtualEndpointIds();
        var found = VirtualEndpointResolver.TryGetFeedSuffix(out var suffix);

        if (endpoints.Count == 0)
        {
            Assert.False(found); // драйвера нет (CI) — суффикса тоже нет
            return;
        }

        Assert.True(found, "драйвер установлен, но суффикс фида не разобран");
        Assert.NotEmpty(suffix);
        Assert.DoesNotContain("\\", suffix); // санирован: '\' → '_'
        Assert.Contains("MEDIA", suffix);    // instance-id нашего devnode
    }

    [Fact]
    public void DriverFeed_PathFor_BuildsInstancePath()
    {
        Assert.Equal(
            "\\\\.\\ParrhesiaFeed_ROOT_MEDIA_0001",
            Parrhesia.Audio.Engine.DriverFeed.PathFor("ROOT_MEDIA_0001"));

        // Legacy-конструктор без суффикса — старое имя.
        Assert.Equal(
            "\\\\.\\ParrhesiaFeed",
            new Parrhesia.Audio.Engine.DriverFeed().Path);
    }

    [Fact]
    public void ResolveLanedEndpoints_LanesAreMonotonicAndIdsWellFormed()
    {
        var laned = VirtualEndpointResolver.ResolveLanedEndpoints();

        if (laned.Count == 0)
        {
            return; // драйвера нет (CI)
        }

        // Номера ланов не убывают (сортировка instance-id).
        var previous = 0;
        foreach (var (lane, id) in laned)
        {
            Assert.True(lane >= previous, "номера ланов обязаны идти по неубывающей");
            previous = lane;
            Assert.StartsWith("{0.0.", id, StringComparison.OrdinalIgnoreCase);
        }

        // Каждый лан содержит и рендер, и захват.
        var lanes = laned.Select(x => x.Lane).Distinct().ToList();
        foreach (var lane in lanes)
        {
            var ids = laned.Where(x => x.Lane == lane).Select(x => x.EndpointId).ToList();
            Assert.Contains(ids, id => id.StartsWith("{0.0.0.", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(ids, id => id.StartsWith("{0.0.1.", StringComparison.OrdinalIgnoreCase));
        }
    }
}
