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
}
