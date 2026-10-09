namespace Parrhesia.Audio.Devices;

/// <summary>
/// Операция менеджера кабелей (В1). Правки копятся в settings.json как
/// «неприменённые» (оранжевые в UI) до кнопки «Применить»; после успешного
/// применения очередь очищается. add/remove выполняются elevated-хелпером
/// (installer\install-devices.ps1, один UAC на пакет), rename — in-process
/// через EndpointPolicy (non-admin).
/// </summary>
public sealed class CableOp
{
    public const string OpAdd = "add";
    public const string OpRemove = "remove";
    public const string OpRename = "rename";

    /// <summary>"add" | "remove" | "rename".</summary>
    public string Op { get; set; } = string.Empty;

    /// <summary>ROOT\VirtualAudioDriver | ROOT\ParrhesiaLane1..7 (add/remove).</summary>
    public string Hwid { get; set; } = string.Empty;

    /// <summary>Описание девноута при add («Parrhesia L5»).</summary>
    public string Desc { get; set; } = string.Empty;

    /// <summary>MMDeviceId эндпоинта (rename): "{0.0.0.00000000}.{guid}".</summary>
    public string EndpointId { get; set; } = string.Empty;

    /// <summary>Новое имя эндпоинта (rename).</summary>
    public string NewName { get; set; } = string.Empty;
}

/// <summary>Установленный кабель: devnode + его пара endpoints.</summary>
public sealed class CableActual
{
    public string InstanceId { get; set; } = string.Empty;

    public string Hwid { get; set; } = string.Empty;

    public string InId { get; set; } = string.Empty;

    public string InName { get; set; } = string.Empty;

    public string OutId { get; set; } = string.Empty;

    public string OutName { get; set; } = string.Empty;
}

/// <summary>Состояние строки кабеля в списке.</summary>
public enum CableRowState
{
    /// <summary>Применено — без маркера.</summary>
    Applied,

    /// <summary>Добавлен (не установлен) — оранжевый.</summary>
    PendingAdd,

    /// <summary>Будет удалён — оранжевый.</summary>
    PendingRemove,

    /// <summary>Переименование не применено — оранжевый.</summary>
    PendingRename,
}

/// <summary>Строка списка кабелей (для UI).</summary>
public sealed class CableRow
{
    public int Lane { get; init; }

    public string InstanceId { get; init; } = string.Empty;

    public string Hwid { get; init; } = string.Empty;

    public string InId { get; init; } = string.Empty;

    public string InName { get; init; } = string.Empty;

    public string OutId { get; init; } = string.Empty;

    public string OutName { get; init; } = string.Empty;

    public CableRowState State { get; init; }

    /// <summary>Строка-подсказка бейджа (пусто — Applied).</summary>
    public string Badge =>
        State switch
        {
            CableRowState.PendingAdd => "добавится",
            CableRowState.PendingRemove => "удалится",
            CableRowState.PendingRename => "переименуется",
            _ => string.Empty,
        };

    public bool IsPending => State != CableRowState.Applied;

    /// <summary>Переименование доступно только установленным endpoints.</summary>
    public bool CanRename => InId.Length > 0 || OutId.Length > 0;
}

/// <summary>
/// Чистая логика менеджера кабелей: постановка в очередь, сверка (reconcile)
/// и построение строк списка. Без I/O — unit-тестируемо.
/// </summary>
public static class CablePlanner
{
    /// <summary>Максимум кабелей: base + Lane1..7 (PFEED_MAX_INSTANCES=8).</summary>
    public const int MaxCables = 8;

    /// <summary>
    /// Первый свободный Lane-hwid с учётом уже установленных и ожидающих
    /// добавления. null — все слоты заняты (лимит MaxCables).
    /// Базовый кабель (ROOT\VirtualAudioDriver) слоты Lane не занимает.
    /// </summary>
    public static string? NextFreeHwid(
        IEnumerable<string> installedHwids,
        IEnumerable<string>? pendingAddHwids = null)
    {
        var busy = new HashSet<string>(installedHwids, StringComparer.OrdinalIgnoreCase);
        foreach (var hwid in pendingAddHwids ?? [])
        {
            busy.Add(hwid);
        }

        for (var i = 1; i <= MaxCables - 1; i++)
        {
            var hwid = $@"ROOT\ParrhesiaLane{i}";
            if (!busy.Contains(hwid))
            {
                return hwid;
            }
        }

        return null;
    }

    /// <summary>
    /// Сверка очереди с фактом: операции, которые уже отражены в системе,
    /// вычищаются (перезагрузка после apply, ручные правки вне приложения).
    /// </summary>
    public static List<CableOp> Reconcile(
        IReadOnlyList<CableActual> actual,
        IEnumerable<CableOp> ops)
    {
        var kept = new List<CableOp>();
        var installed = new HashSet<string>(
            actual.Select(a => a.Hwid), StringComparer.OrdinalIgnoreCase);

        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in actual)
        {
            if (a.InId.Length > 0)
            {
                names[a.InId] = a.InName;
            }

            if (a.OutId.Length > 0)
            {
                names[a.OutId] = a.OutName;
            }
        }

        foreach (var op in ops)
        {
            switch (op.Op)
            {
                case CableOp.OpAdd:
                    if (!installed.Contains(op.Hwid))
                    {
                        kept.Add(op);
                    }

                    break;

                case CableOp.OpRemove:
                    if (installed.Contains(op.Hwid))
                    {
                        kept.Add(op);
                    }

                    break;

                case CableOp.OpRename:
                    if (!names.TryGetValue(op.EndpointId, out var current) ||
                        !string.Equals(current.Trim(), op.NewName.Trim(), StringComparison.Ordinal))
                    {
                        kept.Add(op);
                    }

                    break;

                default:
                    kept.Add(op);
                    break;
            }
        }

        return kept;
    }

    /// <summary>
    /// Строки списка: установленные кабели в порядке ланов (+ пометки
    /// pending), затем ожидающие добавления. Приоритет пометки:
    /// remove &gt; rename.
    /// </summary>
    public static List<CableRow> BuildRows(
        IReadOnlyList<CableActual> actual,
        IReadOnlyList<CableOp> ops)
    {
        var removeSet = new HashSet<string>(
            ops.Where(o => o.Op == CableOp.OpRemove).Select(o => o.Hwid),
            StringComparer.OrdinalIgnoreCase);

        var renames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var op in ops)
        {
            if (op.Op == CableOp.OpRename && op.EndpointId.Length > 0)
            {
                renames[op.EndpointId] = op.NewName;
            }
        }

        var rows = new List<CableRow>();
        var lane = 0;
        foreach (var a in actual)
        {
            lane++;
            var state = CableRowState.Applied;
            if (removeSet.Contains(a.Hwid))
            {
                state = CableRowState.PendingRemove;
            }

            var inName = a.InName;
            var outName = a.OutName;
            var hasIn = renames.TryGetValue(a.InId, out var ri);
            var hasOut = renames.TryGetValue(a.OutId, out var ro);
            if (state == CableRowState.Applied && (hasIn || hasOut))
            {
                state = CableRowState.PendingRename;
                if (hasIn)
                {
                    inName = ri!;
                }

                if (hasOut)
                {
                    outName = ro!;
                }
            }

            rows.Add(new CableRow
            {
                Lane = lane,
                InstanceId = a.InstanceId,
                Hwid = a.Hwid,
                InId = a.InId,
                InName = inName,
                OutId = a.OutId,
                OutName = outName,
                State = state,
            });
        }

        var installedSet = new HashSet<string>(
            actual.Select(a => a.Hwid), StringComparer.OrdinalIgnoreCase);
        foreach (var op in ops)
        {
            if (op.Op != CableOp.OpAdd || installedSet.Contains(op.Hwid))
            {
                continue;
            }

            lane++;
            rows.Add(new CableRow
            {
                Lane = lane,
                Hwid = op.Hwid,
                InName = "—",
                OutName = "—",
                State = CableRowState.PendingAdd,
            });
        }

        return rows;
    }
}
