using Parrhesia.Audio.Engine;
using Parrhesia.Core.Graph;

namespace Parrhesia.Audio.Devices;

/// <summary>Свой (лановый) endpoint: номер лана, MMDevice-id, поток и отображаемое имя.</summary>
/// <param name="Lane">1..N по отсортированным instance-id devnode'ов.</param>
/// <param name="Id">MMDevice-id в формате DeviceSpec ("{0.0.X.00000000}.{guid}").</param>
/// <param name="Flow">Render = «Parrhesia InN» (вход приложений), Capture = «Parrhesia OutN» (виртуальный микрофон).</param>
/// <param name="Name">Фактическое имя (может быть переименовано владельцем).</param>
public sealed record LaneEndpoint(int Lane, string Id, DeviceFlow Flow, string Name);

/// <summary>
/// Ф4: каталог своих endpoint'ов (кабелей) для выбора устройства узла.
/// Источник — PnP-дети root-devnode'ов драйвера (как в ApplyVirtualEndpointNames);
/// имена берутся из активного перечисления, с фолбэком на авто-имя лана.
/// </summary>
public static class LaneCatalog
{
    /// <summary>Свои endpoints в порядке ланов; пусто — драйвера нет.</summary>
    public static List<LaneEndpoint> Get(
        IReadOnlyList<AudioDeviceInfo> capture,
        IReadOnlyList<AudioDeviceInfo> render)
    {
        try
        {
            return From(VirtualEndpointResolver.ResolveLanedEndpoints(), capture, render);
        }
        catch
        {
            // PnP недоступно — пусто (пункты ланов в UI просто не появятся).
            return [];
        }
    }

    /// <summary>Чистая сборка из готовых lane-id (для тестов без PnP).</summary>
    public static List<LaneEndpoint> From(
        IReadOnlyList<(int Lane, string Id)> lanes,
        IReadOnlyList<AudioDeviceInfo> capture,
        IReadOnlyList<AudioDeviceInfo> render)
    {
        var result = new List<LaneEndpoint>(lanes.Count);
        foreach (var (lane, id) in lanes)
        {
            // MMDevice.ID: {0.0.0.…} — рендер («вход приложений»), {0.0.1.…} — захват.
            var isIn = id.StartsWith("{0.0.0.", StringComparison.OrdinalIgnoreCase);
            var pool = isIn ? render : capture;

            string? name = null;
            foreach (var device in pool)
            {
                if (string.Equals(device.Id, id, StringComparison.OrdinalIgnoreCase))
                {
                    name = device.Name;
                    break;
                }
            }

            name ??= isIn ? $"Parrhesia In{lane}" : $"Parrhesia Out{lane}";
            result.Add(new LaneEndpoint(lane, id, isIn ? DeviceFlow.Render : DeviceFlow.Capture, name));
        }

        return result;
    }
}

/// <summary>
/// Ф4: авто-биндинг стартовых узлов на кабели (драйвер установлен →
/// «Браузер» читает вход приложений, «Discord (вирт.)» пишет виртуальный
/// микрофон). Без драйвера — null, узел остаётся без привязки.
/// </summary>
public static class LaneAutoBind
{
    /// <summary>Источник → loopback первого входа приложений (null — входа нет).</summary>
    public static string? SourceDeviceId(IReadOnlyList<LaneEndpoint> lanes)
    {
        foreach (var lane in lanes)
        {
            if (lane.Flow == DeviceFlow.Render)
            {
                return "loopback:" + lane.Id;
            }
        }

        return null;
    }

    /// <summary>Сник → виртуальный микрофон (фид); null — захватных ланов нет.</summary>
    public static string? SinkDeviceId(IReadOnlyList<LaneEndpoint> lanes)
    {
        foreach (var lane in lanes)
        {
            if (lane.Flow == DeviceFlow.Capture)
            {
                return DeviceSpec.VirtualParrhesia;
            }
        }

        return null;
    }

    /// <summary>
    /// Привязывает стартовые узлы БЕЗ привязки по имени-контракту
    /// («Браузер» → вход приложений, «Discord (вирт.)» → вирт. микрофон).
    /// Уже привязанные и переименованные юзером узлы не трогаются.
    /// Возвращает число привязок.
    /// </summary>
    public static int BindUnbound(AudioGraph graph, IReadOnlyList<LaneEndpoint> lanes)
    {
        var source = SourceDeviceId(lanes);
        var sink = SinkDeviceId(lanes);
        if (source is null && sink is null)
        {
            return 0; // драйвера нет
        }

        var bound = 0;
        foreach (var node in graph.Nodes)
        {
            if (!string.IsNullOrEmpty(node.DeviceId))
            {
                continue;
            }

            if (node.Kind == NodeKind.Source && node.Name == "Браузер" && source is not null)
            {
                graph.SetNodeDevice(node.Id, source);
                bound++;
            }
            else if (node.Kind == NodeKind.Sink && node.Name == "Discord (вирт.)" && sink is not null)
            {
                graph.SetNodeDevice(node.Id, sink);
                bound++;
            }
        }

        return bound;
    }
}
