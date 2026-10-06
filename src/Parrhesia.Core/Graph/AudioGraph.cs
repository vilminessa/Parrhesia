namespace Parrhesia.Core.Graph;

/// <summary>
/// Граф маршрутизации: узлы (источники, шины, назначения) и маршруты между ними.
/// Граф ациклический (DAG): циклы запрещены, звук течёт только вперёд.
/// Все мутации идут через методы класса и поднимают <see cref="Changed"/>.
/// </summary>
public sealed class AudioGraph
{
    private readonly List<AudioNode> _nodes = [];
    private readonly List<Route> _routes = [];

    /// <summary>Поднимается после любого изменения структуры или параметров.</summary>
    public event EventHandler<GraphChange>? Changed;

    public IReadOnlyList<AudioNode> Nodes => _nodes;

    public IReadOnlyList<Route> Routes => _routes;

    public bool HasSolo => _nodes.Exists(n => n.Solo);

    /// <summary>Источник звука либо точка суммирования.</summary>
    public AudioNode AddNode(string name, NodeKind kind, Guid? id = null)
    {
        ArgumentNullException.ThrowIfNull(name);

        var node = new AudioNode(id ?? Guid.NewGuid(), name, kind);
        if (_nodes.Exists(n => n.Id == node.Id))
        {
            throw new InvalidOperationException($"Узел с id {node.Id:N} уже существует.");
        }

        _nodes.Add(node);
        Raise(GraphChangeKind.NodeAdded, node: node);
        return node;
    }

    /// <summary>Удаляет узел вместе со всеми подключёнными маршрутами.</summary>
    public bool RemoveNode(Guid id)
    {
        var index = _nodes.FindIndex(n => n.Id == id);
        if (index < 0)
        {
            return false;
        }

        var node = _nodes[index];
        _nodes.RemoveAt(index);

        for (var i = _routes.Count - 1; i >= 0; i--)
        {
            var route = _routes[i];
            if (route.FromId == id || route.ToId == id)
            {
                _routes.RemoveAt(i);
                Raise(GraphChangeKind.RouteRemoved, route: route);
            }
        }

        Raise(GraphChangeKind.NodeRemoved, node: node);
        return true;
    }

    public AudioNode? FindNode(Guid id) => _nodes.Find(n => n.Id == id);

    public Route? FindRoute(Guid fromId, Guid toId) =>
        _routes.Find(r => r.FromId == fromId && r.ToId == toId);

    /// <summary>Проверяет, можно ли соединить узлы, и объясняет причину отказа.</summary>
    public RouteError ValidateRoute(Guid fromId, Guid toId)
    {
        if (fromId == toId)
        {
            return RouteError.SelfLoop;
        }

        var from = FindNode(fromId);
        var to = FindNode(toId);
        if (from is null || to is null)
        {
            return RouteError.NodeNotFound;
        }

        if (!from.HasOutput)
        {
            return RouteError.NoOutputPort;
        }

        if (!to.HasInput)
        {
            return RouteError.NoInputPort;
        }

        if (FindRoute(fromId, toId) is not null)
        {
            return RouteError.Duplicate;
        }

        // Цикл замкнётся, если from уже достижим из to (to → ... → плюс новое ребро from → to).
        if (HasPath(toId, fromId))
        {
            return RouteError.Cycle;
        }

        return RouteError.None;
    }

    /// <summary>Добавляет маршрут. Возвращает <see cref="RouteError.None"/> при успехе.</summary>
    public RouteError AddRoute(Guid fromId, Guid toId, out Route? route)
    {
        route = null;
        var error = ValidateRoute(fromId, toId);
        if (error != RouteError.None)
        {
            return error;
        }

        route = new Route(fromId, toId);
        _routes.Add(route);
        Raise(GraphChangeKind.RouteAdded, route: route);
        return RouteError.None;
    }

    /// <summary>
    /// Добавляет маршрут с конкретной парой каналов (0 = L, 1 = R).
    /// Если маршрут уже есть — пара добавляется в его карту (идемпотентно).
    /// </summary>
    public RouteError AddRoute(Guid fromId, int fromChannel, Guid toId, int toChannel, out Route? route)
    {
        // Валидация каналов раньше любых проверок: плохой индекс — ошибка программиста.
        _ = ChannelMap.Pair(fromChannel, toChannel);

        var existing = FindRoute(fromId, toId);
        if (existing is not null)
        {
            route = existing;
            var updated = existing.Map.With(fromChannel, toChannel, enabled: true);
            if (updated != existing.Map)
            {
                existing.Map = updated;
                Raise(GraphChangeKind.RouteChanged, route: existing);
            }

            return RouteError.None;
        }

        var error = ValidateRoute(fromId, toId);
        if (error != RouteError.None)
        {
            route = null;
            return error;
        }

        route = new Route(fromId, toId) { Map = ChannelMap.Pair(fromChannel, toChannel) };
        _routes.Add(route);
        Raise(GraphChangeKind.RouteAdded, route: route);
        return RouteError.None;
    }

    /// <summary>
    /// Включает/выключает пару каналов на маршруте. Выключение последней пары
    /// удаляет маршрут целиком (событие <see cref="GraphChangeKind.RouteRemoved"/>).
    /// </summary>
    public void SetRouteChannel(Guid fromId, Guid toId, int fromChannel, int toChannel, bool enabled)
    {
        var route = FindRoute(fromId, toId) ??
            throw new ArgumentException("Маршрут не найден.", nameof(toId));

        var updated = route.Map.With(fromChannel, toChannel, enabled);
        if (updated == route.Map)
        {
            return;
        }

        if (updated.IsEmpty)
        {
            RemoveRoute(fromId, toId);
            return;
        }

        route.Map = updated;
        Raise(GraphChangeKind.RouteChanged, route: route);
    }

    public bool RemoveRoute(Guid fromId, Guid toId)
    {
        var route = FindRoute(fromId, toId);
        if (route is null)
        {
            return false;
        }

        _routes.Remove(route);
        Raise(GraphChangeKind.RouteRemoved, route: route);
        return true;
    }

    /// <summary>Существует ли направленный путь from → ... → to (пустой путь: from == to).</summary>
    public bool HasPath(Guid fromId, Guid toId)
    {
        if (fromId == toId)
        {
            return true;
        }

        var visited = new HashSet<Guid> { fromId };
        var queue = new Queue<Guid>();
        queue.Enqueue(fromId);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            foreach (var route in _routes)
            {
                if (route.FromId != current || !visited.Add(route.ToId))
                {
                    continue;
                }

                if (route.ToId == toId)
                {
                    return true;
                }

                queue.Enqueue(route.ToId);
            }
        }

        return false;
    }

    public void SetNodeGain(Guid id, float gain)
    {
        var node = FindNode(id) ?? throw new ArgumentException($"Узел {id:N} не найден.", nameof(id));
        gain = Math.Max(0f, gain);
        if (node.Gain.Equals(gain))
        {
            return;
        }

        node.Gain = gain;
        Raise(GraphChangeKind.NodeChanged, node: node);
    }

    public void RenameNode(Guid id, string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var node = FindNode(id) ?? throw new ArgumentException($"Узел {id:N} не найден.", nameof(id));
        if (node.Name == name)
        {
            return;
        }

        node.Name = name;
        Raise(GraphChangeKind.NodeChanged, node: node);
    }

    public void SetNodeMute(Guid id, bool mute) => SetNodeFlag(id, mute, static (n, v) => n.Mute = v, n => n.Mute);

    public void SetNodeSolo(Guid id, bool solo) => SetNodeFlag(id, solo, static (n, v) => n.Solo = v, n => n.Solo);

    /// <summary>Привязывает узел к устройству (null — отвязывает).</summary>
    public void SetNodeDevice(Guid id, string? deviceId)
    {
        var node = FindNode(id) ?? throw new ArgumentException($"Узел {id:N} не найден.", nameof(id));
        if (node.DeviceId == deviceId)
        {
            return;
        }

        node.DeviceId = deviceId;
        Raise(GraphChangeKind.NodeChanged, node: node);
    }

    /// <summary>
    /// Узел фактически нем: выключен сам, либо (только для источников)
    /// включён чей-то соло и это не он. Шины и назначения соло не глушат —
    /// иначе солёный источник не дошёл бы до выхода.
    /// </summary>
    public bool IsEffectivelyMuted(AudioNode node)
    {
        ArgumentNullException.ThrowIfNull(node);

        if (node.Mute)
        {
            return true;
        }

        if (node.Kind != NodeKind.Source)
        {
            return false;
        }

        return HasSolo && !node.Solo;
    }

    public void SetRouteGain(Guid fromId, Guid toId, float gain)
    {
        var route = FindRoute(fromId, toId) ?? throw new ArgumentException("Маршрут не найден.", nameof(toId));
        gain = Math.Max(0f, gain);
        if (route.Gain.Equals(gain))
        {
            return;
        }

        route.Gain = gain;
        Raise(GraphChangeKind.RouteChanged, route: route);
    }

    public void SetRouteEnabled(Guid fromId, Guid toId, bool enabled)
    {
        var route = FindRoute(fromId, toId) ?? throw new ArgumentException("Маршрут не найден.", nameof(toId));
        if (route.Enabled == enabled)
        {
            return;
        }

        route.Enabled = enabled;
        Raise(GraphChangeKind.RouteChanged, route: route);
    }

    private void SetNodeFlag(Guid id, bool value, Action<AudioNode, bool> setter, Func<AudioNode, bool> getter)
    {
        var node = FindNode(id) ?? throw new ArgumentException($"Узел {id:N} не найден.", nameof(id));
        if (getter(node) == value)
        {
            return;
        }

        setter(node, value);
        Raise(GraphChangeKind.NodeChanged, node: node);
    }

    private void Raise(GraphChangeKind kind, AudioNode? node = null, Route? route = null) =>
        Changed?.Invoke(this, new GraphChange(kind, node, route));
}
