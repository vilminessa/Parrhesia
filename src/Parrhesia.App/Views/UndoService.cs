using Parrhesia.Core.Graph;

namespace Parrhesia.App.Views;

/// <summary>
/// Undo последнего удаления (U2): узел (со слотами, свойствами и его
/// кабелями) либо одиночный кабель. Один буфер — «что удалили последним»;
/// Ctrl+Z возвращает. Точки удаления обязаны вызвать Store* ПЕРЕД удалением.
/// </summary>
internal static class UndoService
{
    private static readonly object Gate = new();
    private static NodeSnapshot? _deletedNode;
    private static RouteSnapshot? _deletedRoute;

    /// <summary>Что-нибудь есть для отмены (для хоткея/меню).</summary>
    public static bool HasBuffer
    {
        get
        {
            lock (Gate)
            {
                return _deletedNode is not null || _deletedRoute is not null;
            }
        }
    }

    /// <summary>Снимок узла с его кабелями — вызывать ДО RemoveNode.</summary>
    public static void StoreDeletedNode(AudioGraph graph, Guid nodeId)
    {
        if (graph.FindNode(nodeId) is not { } node)
        {
            return;
        }

        var routes = graph.Routes
            .Where(r => r.FromId == nodeId || r.ToId == nodeId)
            .Select(ToSnapshot)
            .ToList();

        // Группа живёт в модели графа (не в узле) — ищем по членству.
        var groupId = graph.Groups.FirstOrDefault(g => g.NodeIds.Contains(nodeId))?.Id;

        var snapshot = new NodeSnapshot(
            node.Id,
            node.Name,
            node.Kind,
            node.DeviceId,
            node.Gain,
            node.Mute,
            node.Solo,
            node.Bypassed,
            node.ChannelCount,
            [.. node.ChannelNames],
            node.X,
            node.Y,
            node.StripHeight,
            groupId,
            node.FxExpanded,
            node.FxEnabled.ToDictionary(static kv => kv.Key, static kv => kv.Value),
            [.. node.Slots.Select(ToSnapshot)],
            routes);

        lock (Gate)
        {
            _deletedNode = snapshot;
            _deletedRoute = null; // последнее удаление главнее
        }
    }

    /// <summary>Снимок одиночного кабеля — вызывать ДО RemoveRoute.</summary>
    public static void StoreDeletedRoute(AudioGraph graph, Guid fromId, Guid toId)
    {
        var route = graph.Routes.FirstOrDefault(r => r.FromId == fromId && r.ToId == toId);
        if (route is null)
        {
            return;
        }

        lock (Gate)
        {
            _deletedRoute = ToSnapshot(route);
            _deletedNode = null;
        }
    }

    /// <summary>Возвращает последнее удалённое. false — буфер пуст.</summary>
    public static bool TryUndo(AudioGraph graph)
    {
        NodeSnapshot? node;
        RouteSnapshot? route;
        lock (Gate)
        {
            node = _deletedNode;
            route = _deletedRoute;
            _deletedNode = null;
            _deletedRoute = null;
        }

        System.Diagnostics.Trace.WriteLine(
            $"[Parrhesia][Undo] TryUndo: узел={(node?.Name ?? "-")} кабель={(route is null ? "-" : $"{route.FromId:N}→{route.ToId:N}")}");

        if (node is not null)
        {
            RestoreNode(graph, node);
            return true;
        }

        if (route is not null)
        {
            RestoreRoute(graph, route);
            return true;
        }

        return false;
    }

    /// <summary>Человекочитаемое имя для тоста («Возвращено: X»).</summary>
    public static string? PeekDescription()
    {
        lock (Gate)
        {
            if (_deletedNode is { } node)
            {
                return node.Name;
            }

            return _deletedRoute is not null ? "кабель" : null;
        }
    }

    private static void RestoreNode(AudioGraph graph, NodeSnapshot snapshot)
    {
        if (graph.FindNode(snapshot.Id) is not null)
        {
            return; // уже существует — не дублируем
        }

        var node = graph.AddNode(snapshot.Name, snapshot.Kind, snapshot.Id);
        if (snapshot.DeviceId is not null)
        {
            graph.SetNodeDevice(node.Id, snapshot.DeviceId);
        }

        if (snapshot.ChannelCount != node.ChannelCount || snapshot.ChannelNames.Count != node.ChannelCount)
        {
            graph.SetNodeChannels(node.Id, snapshot.ChannelCount, snapshot.ChannelNames);
        }

        graph.SetNodeGain(node.Id, snapshot.Gain);
        graph.SetNodeMute(node.Id, snapshot.Mute);
        graph.SetNodeSolo(node.Id, snapshot.Solo);
        graph.SetNodeBypass(node.Id, snapshot.Bypassed);

        if (snapshot.X is not null && snapshot.Y is not null)
        {
            graph.SetNodePosition(node.Id, snapshot.X.Value, snapshot.Y.Value);
        }

        if (snapshot.StripHeight is { } height)
        {
            graph.SetNodeStripHeight(node.Id, height);
        }

        graph.SetNodeFxExpanded(node.Id, snapshot.FxExpanded ?? false);
        foreach (var (fx, on) in snapshot.Fx)
        {
            graph.SetNodeFx(node.Id, fx, on);
        }

        foreach (var slot in snapshot.Slots)
        {
            graph.AddSlot(node.Id, new PluginSlot
            {
                Format = slot.Format,
                Path = slot.Path,
                PluginId = slot.PluginId,
                Name = slot.Name,
                Enabled = slot.Enabled,
                State = slot.State?.ToArray(),
            });
        }

        // Кабели узла — после самого узла (концы уже существуют).
        foreach (var route in snapshot.Routes)
        {
            RestoreRoute(graph, route);
        }

        if (snapshot.GroupId is not null)
        {
            graph.SetNodeGroup(node.Id, snapshot.GroupId);
        }
    }

    private static void RestoreRoute(AudioGraph graph, RouteSnapshot snapshot)
    {
        if (graph.FindNode(snapshot.FromId) is null || graph.FindNode(snapshot.ToId) is null)
        {
            return; // концы не восстановлены — кабель не вернуть
        }

        if (graph.Routes.Any(r => r.FromId == snapshot.FromId && r.ToId == snapshot.ToId))
        {
            return; // уже существует
        }

        var error = graph.AddRoute(snapshot.FromId, snapshot.ToId, out _);
        System.Diagnostics.Trace.WriteLine(
            $"[Parrhesia][Undo] RestoreRoute AddRoute: {error} ({snapshot.FromId:N}→{snapshot.ToId:N})");
        if (error != RouteError.None)
        {
            return;
        }

        if (snapshot.Map is { } map)
        {
            graph.SetRouteMap(snapshot.FromId, snapshot.ToId, map);
        }

        graph.SetRouteGain(snapshot.FromId, snapshot.ToId, snapshot.Gain);
        graph.SetRouteEnabled(snapshot.FromId, snapshot.ToId, snapshot.Enabled);
    }

    private static RouteSnapshot ToSnapshot(Route route) => new(
        route.FromId,
        route.ToId,
        route.Gain,
        route.Enabled,
        route.Map);

    private static SlotSnapshot ToSnapshot(PluginSlot slot) => new(
        slot.Format,
        slot.Path,
        slot.PluginId,
        slot.Name,
        slot.Enabled,
        slot.State?.ToArray());

    /// <summary>Снимок узла со слотами и его кабелями.</summary>
    private sealed record NodeSnapshot(
        Guid Id,
        string Name,
        NodeKind Kind,
        string? DeviceId,
        float Gain,
        bool Mute,
        bool Solo,
        bool Bypassed,
        int ChannelCount,
        IReadOnlyList<string> ChannelNames,
        double? X,
        double? Y,
        int? StripHeight,
        string? GroupId,
        bool? FxExpanded,
        IReadOnlyDictionary<string, bool> Fx,
        IReadOnlyList<SlotSnapshot> Slots,
        IReadOnlyList<RouteSnapshot> Routes);

    private sealed record SlotSnapshot(
        PluginFormat Format,
        string Path,
        string PluginId,
        string Name,
        bool Enabled,
        byte[]? State);

    private sealed record RouteSnapshot(Guid FromId, Guid ToId, float Gain, bool Enabled, ChannelMap Map);
}
