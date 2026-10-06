namespace Parrhesia.Audio.Processing;

/// <summary>Поставщик сэмплов для узла-источника (захват устройства или фейк в тестах).</summary>
public interface ISampleInput
{
    /// <summary>
    /// Читает ровно <c>destination.Length</c> сэмплов; недостающее — тишина.
    /// Возвращает число реально прочитанных (не заполненных тишиной) сэмплов.
    /// </summary>
    int Read(Span<float> destination);

    /// <summary>Сэмплов, выданных тишиной из-за нехватки данных (всего).</summary>
    long UnderrunSamples { get; }
}
