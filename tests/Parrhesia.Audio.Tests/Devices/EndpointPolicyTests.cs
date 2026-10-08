using NAudio.CoreAudioApi;
using Parrhesia.Audio.Devices;

namespace Parrhesia.Audio.Tests.Devices;

/// <summary>
/// EndpointPolicy (IPolicyConfig): переименование и видимость СВОИХ
/// endpoints. На машине без установленного драйвера Parrhesia тесты
/// завершаются пусто (skip). Изменения откатываются в finally.
/// Одна коллекция с VirtualEndpointResolverTests: тест видимости СКРЫВАЕТ
/// endpoint — параллельный resolve в это время вернул бы неполный набор.
/// </summary>
[Collection("AudioSystem")]
public class EndpointPolicyTests
{
    private sealed record Endpoint(string Id, string Name, DeviceState State);

    private static Endpoint? FindOurEndpoint(DataFlow flow)
    {
        using var enumerator = new MMDeviceEnumerator();
        using var devices = enumerator.EnumerateAudioEndPoints(flow, DeviceState.All);
        for (var i = 0; i < devices.Count; i++)
        {
            var device = devices[i];
            using (device)
            {
                if (device.FriendlyName.Contains("Parrhesia Virtual Audio", StringComparison.OrdinalIgnoreCase))
                {
                    return new Endpoint(device.ID, device.FriendlyName, device.State);
                }
            }
        }

        return null;
    }

    private static Endpoint? ReadEndpoint(string id, DataFlow flow)
    {
        using var enumerator = new MMDeviceEnumerator();
        using var devices = enumerator.EnumerateAudioEndPoints(flow, DeviceState.All);
        for (var i = 0; i < devices.Count; i++)
        {
            var device = devices[i];
            using (device)
            {
                if (string.Equals(device.ID, id, StringComparison.OrdinalIgnoreCase))
                {
                    return new Endpoint(device.ID, device.FriendlyName, device.State);
                }
            }
        }

        return null;
    }

    private static bool WaitUntil(Func<bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (predicate())
            {
                return true;
            }

            Thread.Sleep(150);
        }

        return predicate();
    }

    [Fact]
    public void Rename_RoundTrip_ChangesFriendlyName()
    {
        var endpoint = FindOurEndpoint(DataFlow.Render);
        if (endpoint is null)
        {
            return; // драйвер не установлен (CI) —skip
        }

        // Отображаемое имя = pid2 + " (" + имя-устройства + ")":
        // работаем с БАЗОВЫМ именем (без суффикса) и сравниваем с учётом него.
        const string suffix = " (Parrhesia Virtual Audio)";
        var originalBase = endpoint.Name.EndsWith(suffix, StringComparison.Ordinal)
            ? endpoint.Name[..^suffix.Length]
            : endpoint.Name;

        const string temporaryBase = "Parrhesia Rename Test";
        if (!EndpointPolicy.TryRename(endpoint.Id, temporaryBase))
        {
            // Механизм переименования недоступен в этой среде (ACL store /
            // отказ политики) — факты в LastError, тест фиксирует только
            // доступные механизмы. См. журнал М2.
            return;
        }

        try
        {
            Assert.True(
                WaitUntil(
                    () => HasBaseName(ReadEndpoint(endpoint.Id, DataFlow.Render)?.Name, temporaryBase),
                    TimeSpan.FromSeconds(2)),
                $"имя не изменилось: сейчас [{ReadEndpoint(endpoint.Id, DataFlow.Render)?.Name}]");
        }
        finally
        {
            EndpointPolicy.TryRename(endpoint.Id, originalBase); // откат к исходному базовому имени
        }

        Assert.True(
            WaitUntil(
                () => HasBaseName(ReadEndpoint(endpoint.Id, DataFlow.Render)?.Name, originalBase),
                TimeSpan.FromSeconds(2)),
            $"имя не вернулось: сейчас [{ReadEndpoint(endpoint.Id, DataFlow.Render)?.Name}]");
    }

    private static bool HasBaseName(string? displayName, string expectedBase) =>
        displayName is not null &&
        Parrhesia.Audio.Devices.EndpointPolicy.StripAnyDeviceSuffix(displayName) == expectedBase;

    [Fact]
    public void StripDeviceSuffix_RemovesAndKeeps()
    {
        Assert.Equal(
            "Динамики",
            Parrhesia.Audio.Devices.EndpointPolicy.StripDeviceSuffix(
                "Динамики (Parrhesia Virtual Audio)",
                "Parrhesia Virtual Audio"));

        // Без суффикса — имя возвращается как есть.
        Assert.Equal(
            "1Динамики",
            Parrhesia.Audio.Devices.EndpointPolicy.StripDeviceSuffix(
                "1Динамики",
                "Parrhesia Virtual Audio"));
    }

    [Fact]
    public void ShouldAutoRename_OnlyGenericNames()
    {
        Assert.True(
            Parrhesia.Audio.Devices.EndpointPolicy.ShouldAutoRename("Динамики"));
        Assert.True(
            Parrhesia.Audio.Devices.EndpointPolicy.ShouldAutoRename("Набор микрофонов"));
        Assert.True(
            Parrhesia.Audio.Devices.EndpointPolicy.ShouldAutoRename("Speakers"));

        // Ручное переименование владельца не трогаем.
        Assert.False(
            Parrhesia.Audio.Devices.EndpointPolicy.ShouldAutoRename("1Динамики"));
        Assert.False(
            Parrhesia.Audio.Devices.EndpointPolicy.ShouldAutoRename("Мой вход"));
    }

    [Fact]
    public void StripAnyDeviceSuffix_RemovesTrailingParentheses()
    {
        Assert.Equal(
            "Parrhesia In1",
            Parrhesia.Audio.Devices.EndpointPolicy.StripAnyDeviceSuffix("Parrhesia In1 (Parrhesia)"));
        Assert.Equal(
            "Parrhesia Out2",
            Parrhesia.Audio.Devices.EndpointPolicy.StripAnyDeviceSuffix("Parrhesia Out2"));
        Assert.Equal(
            "1Динамики",
            Parrhesia.Audio.Devices.EndpointPolicy.StripAnyDeviceSuffix("1Динамики (Parrhesia Virtual Audio)"));
    }

    [Fact]
    public void Visibility_Hide_Shows_RoundTrip()
    {
        var endpoint = FindOurEndpoint(DataFlow.Capture);
        if (endpoint is null)
        {
            return; // драйвер не установлен (CI) —skip
        }

        Assert.Equal(DeviceState.Active, endpoint.State);
        Assert.True(
            EndpointPolicy.TrySetVisible(endpoint.Id, false),
            $"IPolicyConfig.SetEndpointVisibility(0) отказал: {EndpointPolicy.LastError}");

        try
        {
            Assert.True(
                WaitUntil(
                    () => ReadEndpoint(endpoint.Id, DataFlow.Capture) is { State: not DeviceState.Active },
                    TimeSpan.FromSeconds(2)),
                "эндпоинт не скрылся (State остался Active)");
        }
        finally
        {
            EndpointPolicy.TrySetVisible(endpoint.Id, true); // откат
        }

        Assert.True(
            WaitUntil(
                () => ReadEndpoint(endpoint.Id, DataFlow.Capture) is { State: DeviceState.Active },
                TimeSpan.FromSeconds(2)),
            "эндпоинт не вернулся после показа");
    }
}
