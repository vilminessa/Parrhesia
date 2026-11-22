namespace Parrhesia.Audio.Devices;

/// <summary>
/// Ф4: персистентность кастомных имён кабелей. Имя endpoint'а живёт только
/// в самом endpoint'е — после rebind драйвера Windows пересоздаёт endpoints
/// с системным именем, и владелец теряет переименование (а E3-восстановление
/// привязок по имени перестаёт совпадать). Политика: восстанавливаем имя из
/// settings.json, только если текущее имя — системное (generic) или авто-имя
/// лана («Parrhesia InN/OutN»); ручные переименования вне приложения не трогаем.
/// </summary>
public static class CableNamePolicy
{
    /// <summary>
    /// Восстанавливать ли custom-имя для этого endpoint'а:
    /// custom задан, отличается от текущего, а текущее — системное либо авто-имя лана.
    /// </summary>
    public static bool ShouldRestore(string currentBaseName, int lane, bool isIn, string? customName)
    {
        if (string.IsNullOrWhiteSpace(customName))
        {
            return false;
        }

        if (string.Equals(currentBaseName, customName, StringComparison.Ordinal))
        {
            return false; // уже носит custom-имя
        }

        // Системное имя («Динамики»/«Speakers»…) — владелец не переименовывал.
        if (EndpointPolicy.ShouldAutoRename(currentBaseName))
        {
            return true;
        }

        // Авто-имя лана, поставленное ApplyVirtualEndpointNames на старте.
        var autoName = isIn ? $"Parrhesia In{lane}" : $"Parrhesia Out{lane}";
        return string.Equals(currentBaseName, autoName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Совпадает ли имя с авто-именем лана (для диагностики/логов).</summary>
    public static bool IsAutoLaneName(string name, int lane, bool isIn) =>
        string.Equals(name, isIn ? $"Parrhesia In{lane}" : $"Parrhesia Out{lane}", StringComparison.OrdinalIgnoreCase);
}
