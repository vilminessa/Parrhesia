using Parrhesia.Audio.Engine;
using Parrhesia.Core.Graph;

namespace Parrhesia.App.Views.Mixer;

/// <summary>Авто-зона микшера: где пульт «по происхождению». Не хранится —
/// всегда вычисляется из узла, потому остаётся верной при любых мутациях.</summary>
public enum MixerZone
{
    /// <summary>Источник на реальном устройстве: default:capture, чужой endpoint, loopback.</summary>
    Inputs,

    /// <summary>Источник из наших виртуальных кабелей: virtual:parrhesia либо наш endpoint-id.</summary>
    VirtualCables,

    /// <summary>Источник без привязки устройства (или с неразбираемым DeviceId).</summary>
    Unbound,

    /// <summary>Шины и назначения — не «пульты входов», но в микшере нужны.</summary>
    Outputs,
}

/// <summary>Правила классификации узлов в <see cref="MixerZone"/> (чистая
/// функция — покрывается unit-тестами).</summary>
public static class MixerZoneInfo
{
    /// <param name="node">Узел графа.</param>
    /// <param name="ownEndpointIds">MMDevice.ID «своих» endpoints
    /// (<c>CableService.GetOwnEndpointIds()</c>); пусто — драйвер не установлен.</param>
    public static MixerZone Of(AudioNode node, IReadOnlySet<string> ownEndpointIds)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(ownEndpointIds);

        if (node.Kind is NodeKind.Bus or NodeKind.Sink)
        {
            return MixerZone.Outputs;
        }

        if (!DeviceSpec.TryParse(node.DeviceId, out var spec))
        {
            return MixerZone.Unbound;
        }

        if (spec.Target == DeviceSpecTarget.Virtual)
        {
            return MixerZone.VirtualCables;
        }

        // loopback:<наш id> либо привязка <наш id> — звук приходит из кабеля.
        if (spec.DeviceId.Length > 0 && ownEndpointIds.Contains(spec.DeviceId))
        {
            return MixerZone.VirtualCables;
        }

        // default:capture, loopback:default, чужие capture/render-id.
        return MixerZone.Inputs;
    }

    /// <summary>Заголовок секции в ленте (капсом — как в VoiceMeeter).</summary>
    public static string Title(MixerZone zone) => zone switch
    {
        MixerZone.Inputs => "УСТРОЙСТВА ВВОДА",
        MixerZone.VirtualCables => "ВИРТУАЛЬНЫЕ КАБЕЛИ",
        MixerZone.Unbound => "ПРОЧЕЕ",
        _ => "ШИНЫ И ВЫВОДЫ",
    };

    /// <summary>Подпись зоны для чипа-фильтра (обычный регистр).</summary>
    public static string Label(MixerZone zone) => zone switch
    {
        MixerZone.Inputs => "Устройства ввода",
        MixerZone.VirtualCables => "Виртуальные кабели",
        MixerZone.Unbound => "Прочее",
        _ => "Шины и выводы",
    };
}
