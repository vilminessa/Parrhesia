using System.IO;
using System.Text.Json;

namespace Parrhesia.App.Presets;

/// <summary>Настройки приложения (%AppData%\Parrhesia\settings.json).</summary>
public sealed class AppSettings
{
    /// <summary>Имя пресета, который грузится при старте. null — грузится схема по умолчанию.</summary>
    public string? AutoLoadPreset { get; set; }

    private static string FilePath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Parrhesia",
            "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

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
            // Битые настройки не должны мешать старту — начнём с дефолтов.
        }

        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            var directory = Path.GetDirectoryName(FilePath)!;
            Directory.CreateDirectory(directory);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch (Exception)
        {
            // Настройки не критичны для работы — проглатываем ошибки записи.
        }
    }
}
