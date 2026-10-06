using NAudio.CoreAudioApi;

namespace Parrhesia.Audio.Devices;

/// <summary>
/// Сервис устройств на базе WASAPI (MMDevice API): перечисление активных
/// endpoint'ов, устройство по умолчанию и уведомления о горячей смене
/// с дебаунсом (система может слать несколько событий подряд).
/// </summary>
public sealed class WasapiDeviceService : IDeviceService
{
    private static readonly TimeSpan DebounceDelay = TimeSpan.FromMilliseconds(300);

    private readonly MMDeviceEnumerator _enumerator = new();
    private readonly MMDeviceNotificationClient _notifications;
    private readonly Timer _debounce;

    public WasapiDeviceService()
    {
        // useSynchronizationContext=true: события приходят в поток создания
        // сервиса (UI-поток приложения) — подписчики не требуют маршалирования.
        _notifications = _enumerator.CreateNotificationClient();
        _notifications.DeviceAdded += (_, _) => Poke();
        _notifications.DeviceRemoved += (_, _) => Poke();
        _notifications.DeviceStateChanged += (_, _) => Poke();
        _notifications.DefaultDeviceChanged += (_, _) => Poke();
        _debounce = new Timer(
            _ => DevicesChanged?.Invoke(this, EventArgs.Empty),
            state: null,
            dueTime: Timeout.Infinite,
            period: Timeout.Infinite);
    }

    public event EventHandler? DevicesChanged;

    public IReadOnlyList<AudioDeviceInfo> GetDevices(DeviceFlow flow)
    {
        var dataFlow = ToDataFlow(flow);
        var defaultId = TryGetDefaultId(dataFlow);
        var result = new List<AudioDeviceInfo>();

        using var devices = _enumerator.EnumerateAudioEndPoints(dataFlow, DeviceState.Active);
        for (var i = 0; i < devices.Count; i++)
        {
            var device = devices[i];
            using (device)
            {
                result.Add(new AudioDeviceInfo(
                    device.ID,
                    device.FriendlyName,
                    flow,
                    IsDefault: device.ID == defaultId));
            }
        }

        return result;
    }

    public AudioDeviceInfo? GetDefault(DeviceFlow flow)
    {
        var dataFlow = ToDataFlow(flow);
        if (!_enumerator.TryGetDefaultAudioEndpoint(dataFlow, Role.Multimedia, out var device))
        {
            return null;
        }

        using (device)
        {
            return new AudioDeviceInfo(device.ID, device.FriendlyName, flow, IsDefault: true);
        }
    }

    public void Dispose()
    {
        // Порядок важен: сначала отписываемся от COM-событий (иначе во время
        // dispose может прилететь колбэк в уже освобождённый таймер).
        _notifications.Dispose();
        _debounce.Dispose();
        _enumerator.Dispose();
    }

    private void Poke() => _debounce.Change(DebounceDelay, Timeout.InfiniteTimeSpan);

    private string? TryGetDefaultId(DataFlow flow) =>
        _enumerator.TryGetDefaultAudioEndpoint(flow, Role.Multimedia, out var device)
            ? UseAndReturnId(device)
            : null;

    private static string UseAndReturnId(MMDevice device)
    {
        using (device)
        {
            return device.ID;
        }
    }

    private static DataFlow ToDataFlow(DeviceFlow flow) => flow switch
    {
        DeviceFlow.Render => DataFlow.Render,
        DeviceFlow.Capture => DataFlow.Capture,
        _ => throw new ArgumentOutOfRangeException(nameof(flow)),
    };
}
