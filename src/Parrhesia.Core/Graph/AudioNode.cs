namespace Parrhesia.Core.Graph;

/// <summary>Узел графа: источник, шина или назначение.</summary>
public sealed class AudioNode
{
    /// <summary>
    /// Допустимое число каналов узла в этой версии (формат движка двухканальный).
    /// Структура карт каналов рассчитана на <see cref="ChannelMap.MaxChannels"/> —
    /// диапазон поднимется, когда движок вырастет.
    /// </summary>
    public const int MaxChannels = 2;

    internal AudioNode(Guid id, string name, NodeKind kind)
    {
        Id = id;
        Name = name;
        Kind = kind;
    }

    public Guid Id { get; }

    public NodeKind Kind { get; }

    public string Name { get; internal set; }

    /// <summary>Линейный гейн канала (1 = 0 дБ). Изменяется через <see cref="AudioGraph"/>.</summary>
    public float Gain { get; internal set; } = 1f;

    public bool Mute { get; internal set; }

    public bool Solo { get; internal set; }

    /// <summary>
    /// Обход узла: сигнал идёт сквозь на единичном уровне — гейн, mute и
    /// solo игнорируются (нода ведёт себя как отсутствующая).
    /// </summary>
    public bool Bypassed { get; internal set; }

    /// <summary>Число каналов узла (1 = моно, 2 = стерео).</summary>
    public int ChannelCount { get; internal set; } = 2;

    /// <summary>Имена каналов; длина всегда == <see cref="ChannelCount"/>.</summary>
    public string[] ChannelNames { get; internal set; } = ["1", "2"];

    /// <summary>
    /// Привязка к аудио-устройству (формат: "default:capture", "default:render",
    /// "loopback:default", либо MMDevice ID). null — узел не привязан и не активен.
    /// </summary>
    public string? DeviceId { get; internal set; }

    /// <summary>
    /// Положение на холсте схемы. Хранится в модели (а не в UI), чтобы пресеты
    /// восстанавливали раскладку. null — узел ещё не расставлен, UI назначит позицию.
    /// </summary>
    public double? X { get; internal set; }

    public double? Y { get; internal set; }

    public bool HasInput => Kind is NodeKind.Bus or NodeKind.Sink;

    public bool HasOutput => Kind is NodeKind.Source or NodeKind.Bus;

    public override string ToString() => $"{Kind}:{Name}";
}
