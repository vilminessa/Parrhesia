namespace Parrhesia.Core.Profiles;

/// <summary>
/// Именованный профиль: полный снимок схемы — как сцена в OBS или вкладка
/// в браузере. Активный профиль живёт в «живом» графе приложения,
/// остальные хранятся снимками (JSON) в памяти и на диске.
/// </summary>
public sealed class Profile
{
    public Profile(string name, string? snapshotJson = null)
    {
        Name = name;
        SnapshotJson = snapshotJson;
    }

    public string Name { get; internal set; }

    /// <summary>JSON-снимок схемы. null — профиль пуст и ещё не сохранялся.</summary>
    public string? SnapshotJson { get; internal set; }

    public override string ToString() => Name;
}
