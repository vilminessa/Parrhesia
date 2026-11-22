using Parrhesia.Audio.Devices;
using Parrhesia.Audio.Engine;
using Parrhesia.Core.Graph;

namespace Parrhesia.App.Views;

/// <summary>
/// Ф4: пункты выбора устройства узла с ланами драйвера. Входы приложений
/// («Parrhesia InN») — первыми и с понятной подписью (источник читает их
/// через loopback); виртуальный микрофон («Parrhesia OutN» / фид) —
/// первым назначением; вход приложений не предлагается снику (это не
/// назначение — петля на себя). Чистая функция (без PnP/COM) — тестируется.
/// </summary>
public static class NodeDeviceChoices
{
    /// <summary>Полный список пунктов для узла.</summary>
    public static List<DeviceChoice> Build(
        NodeKind kind,
        IReadOnlyList<AudioDeviceInfo> capture,
        IReadOnlyList<AudioDeviceInfo> render,
        IReadOnlyList<LaneEndpoint> lanes)
    {
        var choices = new List<DeviceChoice> { new(DeviceSelection.UnboundName, null) };
        var ownIn = OwnIds(lanes, DeviceFlow.Render);
        var ownOut = OwnIds(lanes, DeviceFlow.Capture);

        if (kind == NodeKind.Source)
        {
            choices.Add(new("По умолчанию (захват)", DeviceSpec.DefaultCapture));
            choices.Add(new("Loopback: по умолчанию", DeviceSpec.DefaultLoopback));

            // Ф4: входы приложений — первым блоком (источник слушает их через loopback).
            foreach (var lane in lanes)
            {
                if (lane.Flow == DeviceFlow.Render)
                {
                    choices.Add(new($"Вход приложений: {lane.Name}", "loopback:" + lane.Id));
                }
            }

            foreach (var device in capture)
            {
                // Наш Out = виртуальный микрофон (наш же микс): подпись вместо
                // сырого имени; значение то же — существующие привязки не трогаются.
                choices.Add(ownOut.Contains(device.Id)
                    ? new DeviceChoice($"Виртуальный микрофон: {device.Name}", device.Id)
                    : new DeviceChoice(device.Name, device.Id));
            }

            foreach (var device in render)
            {
                if (ownIn.Contains(device.Id))
                {
                    continue; // уже добавлен как «Вход приложений»
                }

                choices.Add(new("Loopback: " + device.Name, "loopback:" + device.Id));
            }
        }
        else
        {
            choices.Add(new("По умолчанию (вывод)", DeviceSpec.DefaultRender));

            // Ф4: виртуальный микрофон — первым назначением (драйвер установлен).
            var outLane = FirstLane(lanes, DeviceFlow.Capture);
            choices.Add(outLane is not null
                ? new DeviceChoice($"Виртуальный микрофон: {outLane.Name}", DeviceSpec.VirtualParrhesia)
                : new DeviceChoice("Parrhesia Out (виртуальный)", DeviceSpec.VirtualParrhesia));

            foreach (var device in render)
            {
                if (ownIn.Contains(device.Id))
                {
                    continue; // вход приложений — не назначение (петля на себя)
                }

                choices.Add(new(device.Name, device.Id));
            }
        }

        return choices;
    }

    /// <summary>
    /// Подсказка под полем привязки (Ф4): что сделать в Windows, чтобы звук
    /// пошёл через кабель. null — для этой привязки подсказки нет.
    /// </summary>
    public static string? Hint(string? deviceId, IReadOnlyList<LaneEndpoint> lanes)
    {
        if (string.IsNullOrEmpty(deviceId) || !DeviceSpec.TryParse(deviceId, out var spec))
        {
            return null;
        }

        if (spec.Loopback)
        {
            foreach (var lane in lanes)
            {
                if (lane.Flow == DeviceFlow.Render &&
                    string.Equals(lane.Id, spec.DeviceId, StringComparison.OrdinalIgnoreCase))
                {
                    return $"Назначьте приложению выход «{lane.Name}» в Windows " +
                           "(Параметры → Система → Звук) — его звук пойдёт в Parrhesia.";
                }
            }

            return null;
        }

        if (spec.Target == DeviceSpecTarget.Virtual)
        {
            var outLane = FirstLane(lanes, DeviceFlow.Capture);
            if (outLane is not null)
            {
                return $"Выберите «{outLane.Name}» микрофоном в приложении " +
                       "(Discord и т.п.) — туда пойдёт микс Parrhesia.";
            }
        }

        return null;
    }

    private static HashSet<string> OwnIds(IReadOnlyList<LaneEndpoint> lanes, DeviceFlow flow)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var lane in lanes)
        {
            if (lane.Flow == flow)
            {
                result.Add(lane.Id);
            }
        }

        return result;
    }

    private static LaneEndpoint? FirstLane(IReadOnlyList<LaneEndpoint> lanes, DeviceFlow flow)
    {
        foreach (var lane in lanes)
        {
            if (lane.Flow == flow)
            {
                return lane;
            }
        }

        return null;
    }
}
