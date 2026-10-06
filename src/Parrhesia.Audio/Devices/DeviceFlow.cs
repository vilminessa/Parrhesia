namespace Parrhesia.Audio.Devices;

/// <summary>Направление аудио-устройства.</summary>
public enum DeviceFlow
{
    /// <summary>Устройство вывода (динамики, наушники).</summary>
    Render,

    /// <summary>Устройство ввода (микрофон).</summary>
    Capture,
}
