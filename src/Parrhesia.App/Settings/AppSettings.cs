using System.IO;
using System.Text.Json;

namespace Parrhesia.App.Settings;

/// <summary>Настройки приложения (%AppData%\Parrhesia\settings.json).</summary>
public sealed class AppSettings
{
    /// <summary>Режим отображения схемы: "single" (бандл) или "mastering" (по-канальные порты).</summary>
    public string GraphViewMode { get; set; } = "single";

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
