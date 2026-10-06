namespace Parrhesia.Core.Graph;

/// <summary>Маршрут: направленное соединение «выход → вход» с картой каналов и гейном.</summary>
public sealed class Route
{
    internal Route(Guid fromId, Guid toId, ChannelMap map)
    {
        FromId = fromId;
        ToId = toId;
        Map = map;
    }

    public Guid FromId { get; }

    public Guid ToId { get; }

    /// <summary>Линейный гейн маршрута (1 = 0 дБ). Изменяется через <see cref="AudioGraph"/>.</summary>
    public float Gain { get; internal set; } = 1f;

    public bool Enabled { get; internal set; } = true;

    /// <summary>Какие пары каналов соединены.</summary>
    public ChannelMap Map { get; internal set; }

    public override string ToString() => $"{FromId:N} -> {ToId:N} [{Map}] (gain {Gain:0.###})";
}
