namespace Parrhesia.App.Views;

/// <summary>Пункт выбора устройства узла: отображаемое имя + DeviceSpec-id (null = не привязан).</summary>
public sealed record DeviceChoice(string Name, string? Value);

/// <summary>
/// Логика выбора устройства инспектора (F1 — sticky-привязки в духе VoiceMeeter):
/// привязка принадлежит узлу, а не сессии устройства. Если устройство
/// отвалилось (BT уснул и пропало из ACTIVE-списка), старый фолбэк
/// «items[0]» = «(не привязан)» СТИРАЛ привязку через SetNodeDevice(null)+
/// автосейв. Теперь для несуществующего id возвращается фантом с ТЕМ ЖЕ
/// Value — обработчик выбора видит «уже выбрано» и ничего не перезаписывает.
/// </summary>
public static class DeviceSelection
{
    public const string UnboundName = "(не привязан)";
    public const string UnavailablePrefix = "⚠ недоступно: ";

    /// <summary>
    /// Выбор для узла: найден → пункт списка; пустой id → «(не привязан)»;
    /// id есть, устройства нет → фантом с тем же id (привязка не трогается);
    /// пустой список → null (не трогаем выбор вообще).
    /// </summary>
    public static DeviceChoice? Resolve(string? nodeDeviceId, IReadOnlyList<DeviceChoice> items)
    {
        if (items.Count == 0)
        {
            return null;
        }

        if (string.IsNullOrEmpty(nodeDeviceId))
        {
            return Find(items, null) ?? items[0];
        }

        return Find(items, nodeDeviceId)
               ?? new DeviceChoice(UnavailablePrefix + ShortLabel(nodeDeviceId), nodeDeviceId);
    }

    private static DeviceChoice? Find(IReadOnlyList<DeviceChoice> items, string? value)
    {
        foreach (var item in items)
        {
            if (item.Value == value)
            {
                return item;
            }
        }

        return null;
    }

    /// <summary>Короткая подпись id для фантома: имя из кэша или хвост guid.</summary>
    public static string ShortLabel(string deviceId)
    {
        var bare = deviceId.StartsWith("loopback:", StringComparison.OrdinalIgnoreCase)
            ? deviceId["loopback:".Length..]
            : deviceId;

        var cached = Audio.Devices.DeviceNameCache.GetName(bare);
        if (!string.IsNullOrWhiteSpace(cached))
        {
            return cached;
        }

        var brace = bare.LastIndexOf('{');
        if (brace >= 0 && bare.Length > brace + 10)
        {
            return "…" + bare[^9..];
        }

        return deviceId;
    }
}
