namespace Parrhesia.App.Views.Mixer;

/// <summary>
/// Стабильные id и подписи эффекторов пульта (K-волна: компановка и заглушки;
/// содержимое редакторов даёт волна эффектов — параллельные агенты).
/// Id — контракт с моделью (AudioNode.FxEnabled) и сериализацией.
/// </summary>
public static class FxInfo
{
    public const string Eq = "eq";
    public const string Comp = "comp";
    public const string Gate = "gate";
    public const string Denoise = "denoise";

    /// <summary>Порядок отображения в колонке: (id, подпись на чипе).</summary>
    public static readonly (string Id, string Chip)[] All =
    [
        (Eq, "EQ"),
        (Comp, "Comp"),
        (Gate, "Gate"),
        (Denoise, "Denoise"),
    ];

    /// <summary>Название эффектора для окна настроек и заголовков.</summary>
    public static string Title(string fxId) => fxId switch
    {
        Eq => "Эквалайзер",
        Comp => "Компрессор",
        Gate => "Гейт",
        Denoise => "Денойзер",
        _ => fxId,
    };

    /// <summary>Подсказка в окне настроек: какие параметры появятся в волне эффектов.</summary>
    public static string ParamsHint(string fxId) => fxId switch
    {
        Eq => "Полосы: частота, наклон/подъём, Q · пресеты",
        Comp => "Threshold, Ratio, Attack, Release, Knee, Make-up",
        Gate => "Threshold, Attack, Hold, Release",
        Denoise => "Strength, Sensitivity, профиль шума",
        _ => string.Empty,
    };
}
