namespace Parrhesia.Core.Graph;

/// <summary>Тип узла в графе маршрутизации.</summary>
public enum NodeKind
{
    /// <summary>Источник звука: захват устройства, захват приложения. Имеет только выход.</summary>
    Source,

    /// <summary>Шина: точка суммирования. Имеет вход и выход.</summary>
    Bus,

    /// <summary>Назначение: устройство вывода, виртуальный микрофон. Имеет только вход.</summary>
    Sink,
}
