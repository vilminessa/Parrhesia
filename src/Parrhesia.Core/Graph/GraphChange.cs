namespace Parrhesia.Core.Graph;

public enum GraphChangeKind
{
    NodeAdded,
    NodeRemoved,
    NodeChanged,
    RouteAdded,
    RouteRemoved,
    RouteChanged,

    /// <summary>Состав/имена ручных групп микшера: UI перерисовывает ленту,
    /// автосейв сохраняет профиль; движок НЕ перезапускается (звук не меняется).</summary>
    GroupsChanged,

    /// <summary>Косметика пультов микшера (высота карточки): MixerView применяет
    /// на месте без пересборки ленты; автосейв сохраняет; движок игнорирует.</summary>
    AppearanceChanged,

    /// <summary>Содержимое графа заменено целиком (загрузка пресета).</summary>
    Reset,
}

/// <summary>Изменение графа, о котором нужно узнать UI и движку.</summary>
public sealed record GraphChange(GraphChangeKind Kind, AudioNode? Node = null, Route? Route = null);
