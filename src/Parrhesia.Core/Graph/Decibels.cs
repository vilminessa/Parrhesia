namespace Parrhesia.Core.Graph;

/// <summary>Пересчёт линейного гейна и децибел.</summary>
public static class Decibels
{
    /// <summary>Порог, ниже которого гейн считается нулём (тишина).</summary>
    public const float SilenceThreshold = 1e-7f;

    /// <summary>Нижняя граница фейдера, дБ.</summary>
    public const float MinDb = -60f;

    /// <summary>Линейный гейн в дБ (0 и меньше SilenceThreshold → −∞).</summary>
    public static float ToDb(float linear) =>
        linear <= SilenceThreshold ? float.NegativeInfinity : 20f * MathF.Log10(linear);

    /// <summary>ДБ в линейный гейн; −∞ и значения ниже <see cref="MinDb"/> → 0.</summary>
    public static float FromDb(float db) =>
        float.IsNegativeInfinity(db) || db < MinDb ? 0f : MathF.Pow(10f, db / 20f);
}
