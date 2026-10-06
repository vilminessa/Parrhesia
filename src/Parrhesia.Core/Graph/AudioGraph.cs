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

    /// <summary>Добавляет маршрут. Возвращает <see cref="RouteError.None"/> при успехе.
    /// Карта по умолчанию — диагональ по числу каналов узлов.</summary>
    public RouteError AddRoute(Guid fromId, Guid toId, out Route? route)
    {
        route = null;
        var error = ValidateRoute(fromId, toId);
        if (error != RouteError.None)
        {
            return error;
        }

        var map = ChannelMap.Diagonal(
            FindNode(fromId)!.ChannelCount,
            FindNode(toId)!.ChannelCount);
        route = new Route(fromId, toId, map);
        _routes.Add(route);
        Raise(GraphChangeKind.RouteAdded, route: route);
        return RouteError.None;
    }

    /// <summary>
    /// Добавляет маршрут с конкретной парой каналов. Если маршрут уже есть —
    /// пара добавляется в его карту (идемпотентно). Пара проверяется против
    /// числа каналов обоих узлов.
    /// </summary>
    public RouteError AddRoute(Guid fromId, int fromChannel, Guid toId, int toChannel, out Route? route)
    {
        route = null;
        var pairError = ValidateChannelPair(fromId, fromChannel, toId, toChannel);
        if (pairError != RouteError.None)
        {
            return pairError;
        }

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

        route = new Route(fromId, toId, ChannelMap.Pair(fromChannel, toChannel));
        _routes.Add(route);
        Raise(GraphChangeKind.RouteAdded, route: route);
        return RouteError.None;
    }

    /// <summary>Валидация пары каналов против ёмкости карты и числа каналов узлов.</summary>
    public RouteError ValidateChannelPair(Guid fromId, int fromChannel, Guid toId, int toChannel)
    {
        // Выход за физический предел карты — ошибка программиста.
        _ = ChannelMap.Pair(fromChannel, toChannel);

        var from = FindNode(fromId);
        var to = FindNode(toId);
        if (from is null || to is null)
        {
            return RouteError.NodeNotFound;
        }

        if (fromChannel >= from.ChannelCount || toChannel >= to.ChannelCount)
        {
            return RouteError.ChannelOutOfRange;
        }

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

        var pairError = ValidateChannelPair(fromId, fromChannel, toId, toChannel);
        if (pairError != RouteError.None)
        {
            throw new ArgumentException($"Недопустимая пара каналов: {pairError}.", nameof(toChannel));
        }

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

    /// <summary>
    /// Атомарно заменяет карту каналов маршрута (удобно инспектору: применить
    /// всё состояние чекбоксов разом, без промежуточного удаления маршрута).
    /// Пустая карта удаляет маршрут.
    /// </summary>
    public void SetRouteMap(Guid fromId, Guid toId, ChannelMap map)
    {
        var route = FindRoute(fromId, toId) ??
            throw new ArgumentException("Маршрут не найден.", nameof(toId));

        // Страховка: пары вне числа каналов узлов отбрасываются.
        var fromCount = FindNode(fromId)?.ChannelCount ?? 0;
        var toCount = FindNode(toId)?.ChannelCount ?? 0;
        map = map.Restrict(fromCount, toCount);

        if (map == route.Map)
        {
            return;
        }

        if (map.IsEmpty)
        {
            RemoveRoute(fromId, toId);
            return;
        }

        route.Map = map;
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

    public void SetNodeBypass(Guid id, bool bypassed) =>
        SetNodeFlag(id, bypassed, static (n, v) => n.Bypassed = v, n => n.Bypassed);

    /// <summary>
    /// Меняет число каналов узла (1..<see cref="AudioNode.MaxChannels"/>) и,
    /// опционально, имена каналов. Карты затронутых маршрутов обрезаются,
    /// опустевшие маршруты удаляются.
    /// </summary>
    public void SetNodeChannels(Guid id, int count, IReadOnlyList<string>? names = null)
    {
        if (count is < 1 or > AudioNode.MaxChannels)
        {
            throw new ArgumentOutOfRangeException(
                nameof(count),
                count,
                $"Число каналов: 1..{AudioNode.MaxChannels}.");
        }

        if (names is not null && names.Count != count)
        {
            throw new ArgumentException($"Должно быть имен каналов: {count}.", nameof(names));
        }

        var node = FindNode(id) ??
            throw new ArgumentException($"Узел {id:N} не найден.", nameof(id));

        node.ChannelCount = count;
        node.ChannelNames = BuildChannelNames(names, count, node.ChannelNames);

        // Обрезка карт маршрутов этого узла под новые счётчики.
        List<(Route Route, ChannelMap Map)>? resized = null;
        List<Route>? removed = null;
        foreach (var route in _routes)
        {
            if (route.FromId != id && route.ToId != id)
            {
                continue;
            }

            var fromCount = route.FromId == id ? count : FindNode(route.FromId)?.ChannelCount ?? 0;
            var toCount = route.ToId == id ? count : FindNode(route.ToId)?.ChannelCount ?? 0;
            var restricted = route.Map.Restrict(fromCount, toCount);
            if (restricted == route.Map)
            {
                continue;
            }

            if (restricted.IsEmpty)
            {
                (removed ??= []).Add(route);
            }
            else
            {
                (resized ??= []).Add((route, restricted));
            }
        }

        if (resized is not null)
        {
            foreach (var (route, map) in resized)
            {
                route.Map = map;
                Raise(GraphChangeKind.RouteChanged, route: route);
            }
        }

        if (removed is not null)
        {
            foreach (var route in removed)
            {
                _routes.Remove(route);
                Raise(GraphChangeKind.RouteRemoved, route: route);
            }
        }

        Raise(GraphChangeKind.NodeChanged, node: node);
    }

    /// <summary>Переименовывает канал узла (индексация с нуля).</summary>
    public void SetNodeChannelName(Guid id, int channel, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var node = FindNode(id) ??
            throw new ArgumentException($"Узел {id:N} не найден.", nameof(id));
        if (channel < 0 || channel >= node.ChannelCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(channel),
                channel,
                $"У канала {node.Name} каналов: {node.ChannelCount}.");
        }

        if (node.ChannelNames[channel] == name)
        {
            return;
        }

        var copy = (string[])node.ChannelNames.Clone();
        copy[channel] = name;
        node.ChannelNames = copy;
        Raise(GraphChangeKind.NodeChanged, node: node);
    }

    private static string[] BuildChannelNames(IReadOnlyList<string>? provided, int count, string[] existing)
    {
        if (provided is not null)
        {
            var exact = new string[count];
            for (var i = 0; i < count; i++)
            {
                exact[i] = provided[i];
            }

            return exact;
        }

        var result = new string[count];
        for (var i = 0; i < count; i++)
        {
            result[i] = i < existing.Length ? existing[i] : (i + 1).ToString();
        }

        return result;
    }

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

    /// <summary>Задаёт координаты узла на холсте схемы.</summary>
    public void SetNodePosition(Guid id, double x, double y)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y))
        {
            throw new ArgumentOutOfRangeException(nameof(x), "Координаты должны быть конечными числами.");
        }

        var node = FindNode(id) ?? throw new ArgumentException($"Узел {id:N} не найден.", nameof(id));
        if (node.X == x && node.Y == y)
        {
            return;
        }

        node.X = x;
        node.Y = y;
        Raise(GraphChangeKind.NodeChanged, node: node);
    }

    /// <summary>
    /// Атомарно заменяет содержимое графа копиями <paramref name="source"/>
    /// и поднимает одно событие <see cref="GraphChangeKind.Reset"/>.
    /// Идентичность графа сохраняется — подписчики (движок, UI) продолжают работать.
    /// </summary>
    public void ReplaceWith(AudioGraph source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (ReferenceEquals(source, this))
        {
            return;
        }

        _nodes.Clear();
        _routes.Clear();

        foreach (var node in source._nodes)
        {
            _nodes.Add(new AudioNode(node.Id, node.Name, node.Kind)
            {
                Gain = node.Gain,
                Mute = node.Mute,
                Solo = node.Solo,
                Bypassed = node.Bypassed,
                ChannelCount = node.ChannelCount,
                ChannelNames = (string[])node.ChannelNames.Clone(),
                DeviceId = node.DeviceId,
                X = node.X,
                Y = node.Y,
            });
        }

        foreach (var route in source._routes)
        {
            _routes.Add(new Route(route.FromId, route.ToId, route.Map)
            {
                Gain = route.Gain,
                Enabled = route.Enabled,
            });
        }

        Raise(GraphChangeKind.Reset);
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
