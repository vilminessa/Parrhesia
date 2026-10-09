using System.Text.Json;
using System.Text.Json.Serialization;
using Parrhesia.Core.Graph;

namespace Parrhesia.Core.Serialization;

/// <summary>
/// Сериализация графа маршрутизации в версионированный JSON (файлы пресетов).
/// Формат: { version, nodes[], routes[], groups[] }. Неизвестные поля игнорируются,
/// неподдерживаемая версия — ошибка (пресеты пишутся машиной, читаем строго).
/// </summary>
public static class GraphSerializer
{
    /// <summary>v3: слоты-вставки эффектов в шинах (fields slots у узла).
    /// v1 читается с миграцией (старая раскладка карты 2×2), v2 — как есть
    /// (слоты отсутствуют → пустые цепочки).</summary>
    public const int CurrentVersion = 3;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        // Кириллица в файлах как есть — файлы читаются человеком.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Serialize(AudioGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);

        var document = new GraphDocument
        {
            Version = CurrentVersion,
            Nodes = graph.Nodes.Select(n => new NodeDocument
            {
                Id = n.Id,
                Name = n.Name,
                Kind = n.Kind,
                Gain = n.Gain,
                Mute = n.Mute,
                Solo = n.Solo,
                Bypassed = n.Bypassed,
                ChannelCount = n.ChannelCount,
                ChannelNames = n.ChannelNames,
                Device = n.DeviceId,
                X = n.X,
                Y = n.Y,
                StripHeight = n.StripHeight,
                Fx = n.FxEnabled.Count > 0 ? n.FxEnabled : null,
                Slots = n.Slots.Count > 0
                    ? n.Slots.Select(s => new SlotDocument
                    {
                        Format = s.Format,
                        Path = s.Path,
                        PluginId = s.PluginId,
                        Name = s.Name,
                        Enabled = s.Enabled,
                        State = s.State is null ? null : Convert.ToBase64String(s.State),
                    }).ToList()
                    : null,
            }).ToList(),
            Routes = graph.Routes.Select(r => new RouteDocument
            {
                From = r.FromId,
                To = r.ToId,
                Gain = r.Gain,
                Enabled = r.Enabled,
                Map = r.Map.Bits,
            }).ToList(),
            // null = «группы ещё не заводили» (стандартная пара создаётся UI).
            Groups = graph.GroupsForSerialize is { } groups
                ? groups.Select(g => new GroupDocument
                {
                    Id = g.Id,
                    Name = g.Name,
                    AutoFill = g.AutoFill,
                    NodeIds = g.NodeIds,
                }).ToList()
                : null,
        };

        return JsonSerializer.Serialize(document, Options);
    }

    public static AudioGraph Deserialize(string json)
    {
        if (TryDeserialize(json, out var graph, out var error))
        {
            return graph!;
        }

        throw new GraphSerializerException(error!);
    }

    public static bool TryDeserialize(string json, out AudioGraph? graph, out string? error)
    {
        ArgumentNullException.ThrowIfNull(json);
        graph = null;
        error = null;

        GraphDocument? document;
        try
        {
            document = JsonSerializer.Deserialize<GraphDocument>(json, Options);
        }
        catch (JsonException ex)
        {
            error = "Некорректный JSON: " + ex.Message;
            return false;
        }

        if (document is null)
        {
            error = "Пустой документ пресета.";
            return false;
        }

        if (document.Version is < 1 or > CurrentVersion)
        {
            error = $"Неподдерживаемая версия пресета {document.Version} (ожидается 1..{CurrentVersion}).";
            return false;
        }

        var built = new AudioGraph();
        var legacyMapLayout = document.Version == 1;
        var nodeDocuments = document.Nodes ?? [];
        if (nodeDocuments.Count == 0 && (document.Routes?.Count ?? 0) > 0)
        {
            error = "Есть маршруты, но нет узлов.";
            return false;
        }

        foreach (var nodeDocument in nodeDocuments)
        {
            var nodeError = ValidateNode(nodeDocument);
            if (nodeError is not null)
            {
                error = nodeError;
                return false;
            }

            if (built.FindNode(nodeDocument.Id) is not null)
            {
                error = $"Дублирующийся id узла: {nodeDocument.Id:N}.";
                return false;
            }

            var node = built.AddNode(nodeDocument.Name!, nodeDocument.Kind, nodeDocument.Id);
            node.Gain = nodeDocument.Gain;
            node.Mute = nodeDocument.Mute;
            node.Solo = nodeDocument.Solo;
            node.Bypassed = nodeDocument.Bypassed;
            node.ChannelCount = nodeDocument.ChannelCount;
            node.ChannelNames = nodeDocument.ChannelNames!;
            node.DeviceId = nodeDocument.Device;
            node.X = nodeDocument.X;
            node.Y = nodeDocument.Y;
            node.StripHeight = nodeDocument.StripHeight;
            node.FxEnabled = nodeDocument.Fx ?? [];

            if (nodeDocument.Slots is { Count: > 0 })
            {
                if (nodeDocument.Kind != NodeKind.Bus)
                {
                    error = $"Узел «{nodeDocument.Name}»: слоты эффектов возможны только у шин.";
                    return false;
                }

                foreach (var slotDocument in nodeDocument.Slots)
                {
                    var slotError = ValidateSlot(slotDocument, nodeDocument.Name!);
                    if (slotError is not null)
                    {
                        error = slotError;
                        return false;
                    }

                    byte[]? state = null;
                    if (!string.IsNullOrEmpty(slotDocument.State))
                    {
                        try
                        {
                            state = Convert.FromBase64String(slotDocument.State);
                        }
                        catch (FormatException)
                        {
                            error = $"Узел «{nodeDocument.Name}»: слот «{slotDocument.Name}»: состояние не является base64.";
                            return false;
                        }
                    }

                    node.SlotsInternal.Add(new PluginSlot
                    {
                        Format = slotDocument.Format,
                        Path = slotDocument.Path!,
                        PluginId = slotDocument.PluginId!,
                        Name = string.IsNullOrEmpty(slotDocument.Name) ? slotDocument.PluginId! : slotDocument.Name,
                        Enabled = slotDocument.Enabled,
                        State = state,
                    });
                }
            }
        }

        foreach (var routeDocument in document.Routes ?? [])
        {
            var routeError = ValidateRoute(routeDocument, built);
            if (routeError is not null)
            {
                error = routeError;
                return false;
            }

            var map = legacyMapLayout
                ? ChannelMap.FromLegacyBits(routeDocument.Map)
                : new ChannelMap(routeDocument.Map);

            var addError = built.AddRoute(routeDocument.From, routeDocument.To, out var route);
            if (addError != RouteError.None || route is null)
            {
                error = $"Маршрут {routeDocument.From:N} → {routeDocument.To:N}: {addError}.";
                return false;
            }

            route.Gain = routeDocument.Gain;
            route.Enabled = routeDocument.Enabled;
            route.Map = map;
            // Карта могла содержать пары, которых больше нет у узлов (ручная правка файла).
            var fromCount = built.FindNode(routeDocument.From)!.ChannelCount;
            var toCount = built.FindNode(routeDocument.To)!.ChannelCount;
            route.Map = route.Map.Restrict(fromCount, toCount);
            if (route.Map.IsEmpty)
            {
                error = $"Маршрут {routeDocument.From:N} → {routeDocument.To:N}: пустая карта каналов.";
                built.RemoveRoute(routeDocument.From, routeDocument.To);
                return false;
            }
        }

        // Группы микшера: мёртвые ссылки на узлы вычищаются внутри RestoreGroups;
        // отсутствие поля оставляет группы «не инициализированными».
        built.RestoreGroups(document.Groups is null
            ? null
            : document.Groups
                .Where(g => g is not null && !string.IsNullOrWhiteSpace(g.Name))
                .Select(g => new MixerGroup
                {
                    Id = string.IsNullOrWhiteSpace(g.Id) ? Guid.NewGuid().ToString("N") : g.Id,
                    Name = g.Name.Trim(),
                    AutoFill = g.AutoFill,
                    NodeIds = g.NodeIds ?? [],
                })
                .ToList());

        graph = built;
        return true;
    }

    private static string? ValidateNode(NodeDocument document)
    {
        if (document.Id == Guid.Empty)
        {
            return "Узел с пустым id.";
        }

        if (string.IsNullOrWhiteSpace(document.Name))
        {
            return $"Узел {document.Id:N} без имени.";
        }

        if (!Enum.IsDefined(document.Kind))
        {
            return $"Узел «{document.Name}»: неизвестный тип {document.Kind}.";
        }

        if (document.ChannelCount is < 1 or > AudioNode.MaxChannels)
        {
            return $"Узел «{document.Name}»: недопустимое число каналов {document.ChannelCount}.";
        }

        if (document.ChannelNames is null || document.ChannelNames.Length != document.ChannelCount)
        {
            return $"Узел «{document.Name}»: имен каналов должно быть {document.ChannelCount}.";
        }

        if (!float.IsFinite(document.Gain) || document.Gain < 0f)
        {
            return $"Узел «{document.Name}»: недопустимый гейн {document.Gain}.";
        }

        if (document.X is { } x && !double.IsFinite(x))
        {
            return $"Узел «{document.Name}»: недопустимая координата X.";
        }

        if (document.Y is { } y && !double.IsFinite(y))
        {
            return $"Узел «{document.Name}»: недопустимая координата Y.";
        }

        return null;
    }

    private static string? ValidateSlot(SlotDocument document, string nodeName)
    {
        if (!Enum.IsDefined(document.Format))
        {
            return $"Узел «{nodeName}»: слот с неизвестным форматом {document.Format}.";
        }

        if (string.IsNullOrWhiteSpace(document.Path))
        {
            return $"Узел «{nodeName}»: слот без пути к модулю плагина.";
        }

        if (string.IsNullOrWhiteSpace(document.PluginId))
        {
            return $"Узел «{nodeName}»: слот «{document.Path}» без id плагина.";
        }

        return null;
    }

    private static string? ValidateRoute(RouteDocument document, AudioGraph graph)
    {
        var from = graph.FindNode(document.From);
        var to = graph.FindNode(document.To);
        if (from is null || to is null)
        {
            return $"Маршрут {document.From:N} → {document.To:N} ссылается на неизвестный узел.";
        }

        if (document.Map == 0)
        {
            return $"Маршрут «{from.Name}» → «{to.Name}»: пустая карта каналов.";
        }

        if (!float.IsFinite(document.Gain) || document.Gain < 0f)
        {
            return $"Маршрут «{from.Name}» → «{to.Name}»: недопустимый гейн {document.Gain}.";
        }

        if (!from.HasOutput || !to.HasInput)
        {
            return $"Маршрут «{from.Name}» → «{to.Name}» противоречит типам узлов.";
        }

        return null;
    }

    private sealed class GraphDocument
    {
        public int Version { get; set; }

        public List<NodeDocument>? Nodes { get; set; }

        public List<RouteDocument>? Routes { get; set; }

        /// <summary>Ручные группы микшера; отсутствие поля — «не инициализировано».</summary>
        public List<GroupDocument>? Groups { get; set; }
    }

    private sealed class GroupDocument
    {
        public string Id { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        public bool AutoFill { get; set; }

        public List<Guid>? NodeIds { get; set; }
    }

    private sealed class NodeDocument
    {
        public Guid Id { get; set; }

        public string? Name { get; set; }

        public NodeKind Kind { get; set; }

        public float Gain { get; set; } = 1f;

        public bool Mute { get; set; }

        public bool Solo { get; set; }

        public bool Bypassed { get; set; }

        public int ChannelCount { get; set; } = 2;

        public string[] ChannelNames { get; set; } = ["1", "2"];

        public string? Device { get; set; }

        public double? X { get; set; }

        public double? Y { get; set; }

        /// <summary>Высота пульта в микшере (px); null — дефолт. Косметика.</summary>
        public int? StripHeight { get; set; }

        /// <summary>Состояния эффекторов пульта (id → включён); null — ничего не включено.</summary>
        public Dictionary<string, bool>? Fx { get; set; }

        /// <summary>Слоты-вставки эффектов (только у шин); null — слотов нет.</summary>
        public List<SlotDocument>? Slots { get; set; }
    }

    private sealed class SlotDocument
    {
        public PluginFormat Format { get; set; }

        public string? Path { get; set; }

        public string? PluginId { get; set; }

        public string? Name { get; set; }

        public bool Enabled { get; set; } = true;

        /// <summary>State-чанк плагина в base64.</summary>
        public string? State { get; set; }
    }

    private sealed class RouteDocument
    {
        public Guid From { get; set; }

        public Guid To { get; set; }

        public float Gain { get; set; } = 1f;

        public bool Enabled { get; set; } = true;

        /// <summary>Битовая карта каналов; раскладка зависит от версии документа.</summary>
        public ulong Map { get; set; }
    }
}
