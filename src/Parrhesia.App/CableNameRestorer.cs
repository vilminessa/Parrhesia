using System.Diagnostics;
using Parrhesia.App.Settings;
using Parrhesia.Audio.Devices;

namespace Parrhesia.App;

/// <summary>
/// Ф4: восстановление кастомных имён кабелей. Имя endpoint'а живёт только в
/// нём самом — после rebind драйвера Windows создаёт endpoints заново с
/// системным именем (а E3-восстановление привязок по имени перестаёт
/// совпадать). Вызывается ДО старта движка: при инициализации и на
/// DevicesChanged раньше рестарта движка (подписка раньше, чем движок).
/// </summary>
public static class CableNameRestorer
{
    public static void Restore(AppSettings settings)
    {
        if (settings.CableNames.Count == 0)
        {
            return;
        }

        List<CableActual> installed;
        try
        {
            installed = CableService.GetInstalled();
        }
        catch
        {
            return; // PnP/реестр недоступны — не мешаем старту
        }

        var lane = 0;
        foreach (var cable in installed)
        {
            lane++;
            if (!settings.CableNames.TryGetValue(cable.Hwid, out var custom))
            {
                continue;
            }

            RestoreSide(cable.InId, cable.InName, lane, isIn: true, custom.In);
            RestoreSide(cable.OutId, cable.OutName, lane, isIn: false, custom.Out);
        }
    }

    private static void RestoreSide(string endpointId, string currentName, int lane, bool isIn, string customName)
    {
        if (endpointId.Length == 0 ||
            !CableNamePolicy.ShouldRestore(currentName, lane, isIn, customName))
        {
            return;
        }

        if (EndpointPolicy.TryRename(endpointId, customName))
        {
            Trace.WriteLine(
                $"[Parrhesia] Кабель L{lane}: имя восстановлено — «{customName}»");
        }
        else
        {
            Trace.WriteLine(
                $"[Parrhesia] Кабель L{lane}: не удалось восстановить «{customName}» ({EndpointPolicy.LastError})");
        }
    }
}
