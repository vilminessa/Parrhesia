namespace Parrhesia.Audio.Devices;

/// <summary>
/// Перечисление аудио-устройств и уведомления о горячей смене (подключение,
/// отключение, смена устройства по умолчанию).
/// </summary>
public interface IDeviceService : IDisposable
{
    /// <summary>Активные устройства направления <paramref name="flow"/>.</summary>
    IReadOnlyList<AudioDeviceInfo> GetDevices(DeviceFlow flow);

    /// <summary>Устройство по умолчанию (мультимедийная роль) или null.</summary>
    AudioDeviceInfo? GetDefault(DeviceFlow flow);

    /// <summary>Поднимается (с дебаунсом) при любом изменении списка устройств.</summary>
    event EventHandler? DevicesChanged;
}
