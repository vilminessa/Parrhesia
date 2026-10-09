using Microsoft.Win32;
using Parrhesia.Audio.Engine;

namespace Parrhesia.Audio.Devices;

/// <summary>
/// Фактический состав кабелей (В1): установленные devnode'ы драйвера
/// (base + ROOT\ParrhesiaLane1..7) с их парами endpoints. Источники —
/// реестр (hwid, имена) и PnP-дети через VirtualEndpointResolver.
/// </summary>
public static class CableService
{
    private const string EnumPrefix = @"SYSTEM\CurrentControlSet\Enum\";

    // DEVPKEY_Device_FriendlyName-дубль в MMDevices (REG_SZ, как в
    // install-devices/спайках В1 — «Parrhesia In1» и т.п.).
    private const string FriendlyNameValue =
        "{a45c254e-df1c-4efd-8020-67d146a850e0},2";

    /// <summary>Установленные кабели в порядке ланов (сортировка instance-id).</summary>
    public static List<CableActual> GetInstalled()
    {
        var result = new List<CableActual>();
        try
        {
            foreach (var root in VirtualEndpointResolver.FindSortedDriverRootInstanceIds())
            {
                var cable = new CableActual
                {
                    InstanceId = root,
                    Hwid = ReadHwid(root) ?? string.Empty,
                };

                foreach (var child in VirtualEndpointResolver.GetChildren(root))
                {
                    // "SWD\MMDEVAPI\{0.0.X.00000000}.{guid}" → DeviceSpec-id.
                    const string prefix = @"SWD\MMDEVAPI\";
                    if (!child.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var id = child.Substring(prefix.Length);
                    if (id.StartsWith("{0.0.0.", StringComparison.OrdinalIgnoreCase))
                    {
                        cable.InId = id;
                        cable.InName = ReadEndpointName(id, render: true);
                    }
                    else if (id.StartsWith("{0.0.1.", StringComparison.OrdinalIgnoreCase))
                    {
                        cable.OutId = id;
                        cable.OutName = ReadEndpointName(id, render: false);
                    }
                }

                result.Add(cable);
            }
        }
        catch
        {
            // PnP/реестр недоступны — пустой список (UI покажет «нет кабелей»).
        }

        return result;
    }

    /// <summary>Первый HardwareID девноута ("ROOT\ParrhesiaLane3" и т.п.).</summary>
    private static string? ReadHwid(string instanceId)
    {
        using var key = Registry.LocalMachine.OpenSubKey(EnumPrefix + instanceId);
        if (key?.GetValue("HardwareID") is string[] ids && ids.Length > 0)
        {
            return ids[0];
        }

        if (key?.GetValue("HardwareID") is string single)
        {
            return single;
        }

        return null;
    }

    /// <summary>Имя endpoint'а по MMDeviceId: "{0.0.X.00000000}.{guid}" → friendly name.</summary>
    private static string ReadEndpointName(string deviceId, bool render)
    {
        // "{0.0.X.00000000}.{guid}" → guid.
        var brace = deviceId.LastIndexOf('{');
        if (brace < 0)
        {
            return string.Empty;
        }

        var guid = deviceId.Substring(brace);
        var flow = render ? "Render" : "Capture";
        var path = $@"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\{flow}\{guid}\Properties";
        using var key = Registry.LocalMachine.OpenSubKey(path);
        return key?.GetValue(FriendlyNameValue) as string ?? string.Empty;
    }
}
