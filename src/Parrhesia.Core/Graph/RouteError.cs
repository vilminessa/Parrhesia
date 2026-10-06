namespace Parrhesia.Core.Graph;

/// <summary>Результат добавления маршрута.</summary>
public enum RouteError
{
    /// <summary>Маршрут добавлен.</summary>
    None,

    /// <summary>Один из узлов не найден.</summary>
    NodeNotFound,

    /// <summary>Маршрут уже существует.</summary>
    Duplicate,

    /// <summary>Узел нельзя соединить сам с собой.</summary>
    SelfLoop,

    /// <summary>У узла-источника нет выхода (это Sink).</summary>
    NoOutputPort,

    /// <summary>У узла-назначения нет входа (это Source).</summary>
    NoInputPort,

    /// <summary>Соединение замкнуло бы цикл.</summary>
    Cycle,

    /// <summary>Канал выходит за число каналов узла.</summary>
    ChannelOutOfRange,
}
