using System.IO;
using System.Text.Json;
using Parrhesia.Audio.Devices;

namespace Parrhesia.App.Settings;

/// <summary>Настройки приложения (%AppData%\Parrhesia\settings.json).</summary>
public sealed class AppSettings
{
    /// <summary>Режим отображения схемы: "single" (бандл) или "mastering" (по-канальные порты).</summary>
    public string GraphViewMode { get; set; } = "single";

    /// <summary>
    /// Устройство мониторинга (MMDeviceId, DeviceSpec-формат или пусто — выкл.):
    /// реальный вывод, звучащий параллельно виртуальному снику.
    /// </summary>
    public string MonitorDeviceId { get; set; } = string.Empty;

    /// <summary>
    /// Частота движка: "auto" (по умолчанию) либо 44100/48000/88200/96000.
    /// Разбор — EngineFormat.ParseSetting; невалидное значение деградирует в "auto".
    /// </summary>
    public string EngineSampleRate { get; set; } = "auto";

    /// <summary>
    /// Неприменённые операции менеджера кабелей (В1): add/remove/rename
    /// ждут кнопки «Применить» — в UI помечены оранжевым. Очистка — после
    /// успешного применения или сверки с фактом (CablePlanner.Reconcile).
    /// </summary>
    public List<CableOp> CablePendingOps { get; set; } = [];

    private static string FilePath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Parrhesia",
            "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public bool IsExpandedView =>
        string.Equals(GraphViewMode, "mastering", StringComparison.OrdinalIgnoreCase);

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
            }
        }
        catch (Exception)
        {
            // Битые настройки не мешают старту.
        }

        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch (Exception)
        {
            // Настройки не критичны для работы.
        }
    }
}
