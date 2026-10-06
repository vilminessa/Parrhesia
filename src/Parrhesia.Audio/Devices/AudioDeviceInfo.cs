namespace Parrhesia.Audio.Devices;

/// <summary>Сведения об аудио-устройстве WASAPI (снимок, безопасен для UI).</summary>
public sealed record AudioDeviceInfo(string Id, string Name, DeviceFlow Flow, bool IsDefault);
