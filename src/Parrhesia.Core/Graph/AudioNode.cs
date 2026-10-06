namespace Parrhesia.Core.Graph;

/// <summary>Узел графа: источник, шина или назначение.</summary>
public sealed class AudioNode
{
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
    /// Привязка к аудио-устройству (формат: "default:capture", "default:render",
    /// "loopback:default", либо MMDevice ID). null — узел не привязан и не активен.
    /// </summary>
    public string? DeviceId { get; internal set; }

    public bool HasInput => Kind is NodeKind.Bus or NodeKind.Sink;

    public bool HasOutput => Kind is NodeKind.Source or NodeKind.Bus;

    public override string ToString() => $"{Kind}:{Name}";
}
