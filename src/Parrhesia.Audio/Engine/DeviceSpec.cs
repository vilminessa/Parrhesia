namespace Parrhesia.Audio.Engine;

/// <summary>
/// Разбор DeviceId узла графа. Форматы:
/// "default:capture" / "default:render" — устройство по умолчанию;
/// "loopback:default" / "loopback:&lt;MMDeviceId&gt;" — захват того, что играет на устройстве вывода;
/// "virtual:parrhesia" — виртуальный вывод Parrhesia (драйвер \\.\ParrhesiaFeed);
/// "&lt;MMDeviceId&gt;" — конкретный endpoint.
/// </summary>
public readonly record struct DeviceSpec(bool Loopback, DeviceSpecTarget Target, string DeviceId)
{
    public const string DefaultCapture = "default:capture";
    public const string DefaultRender = "default:render";
    public const string DefaultLoopback = "loopback:default";
    public const string VirtualParrhesia = "virtual:parrhesia";

    public static bool TryParse(string? deviceId, out DeviceSpec spec)
    {
        spec = default;
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            return false;
        }

        if (deviceId == DefaultCapture)
        {
            spec = new DeviceSpec(false, DeviceSpecTarget.DefaultCapture, string.Empty);
            return true;
        }

        if (deviceId == DefaultRender)
        {
            spec = new DeviceSpec(false, DeviceSpecTarget.DefaultRender, string.Empty);
            return true;
        }

        if (deviceId == DefaultLoopback)
        {
            spec = new DeviceSpec(true, DeviceSpecTarget.DefaultRender, string.Empty);
            return true;
        }

        if (deviceId == VirtualParrhesia)
        {
            spec = new DeviceSpec(false, DeviceSpecTarget.Virtual, string.Empty);
            return true;
        }

        if (deviceId.StartsWith("loopback:", StringComparison.Ordinal))
        {
            var id = deviceId["loopback:".Length..];
            if (id.Length == 0)
            {
                return false;
            }

            spec = new DeviceSpec(true, DeviceSpecTarget.ById, id);
            return true;
        }

        spec = new DeviceSpec(false, DeviceSpecTarget.ById, deviceId);
        return true;
    }

    public override string ToString() => this switch
    {
        { Loopback: true, Target: DeviceSpecTarget.DefaultRender } => "loopback:default",
        { Loopback: true, Target: DeviceSpecTarget.ById } => "loopback:" + DeviceId,
        { Target: DeviceSpecTarget.DefaultCapture } => "default:capture",
        { Target: DeviceSpecTarget.DefaultRender } => "default:render",
        { Target: DeviceSpecTarget.Virtual } => VirtualParrhesia,
        _ => DeviceId,
    };
}

public enum DeviceSpecTarget
{
    DefaultCapture,
    DefaultRender,
    ById,
    Virtual,
}
