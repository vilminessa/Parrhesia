using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Parrhesia.Audio.Engine;

/// <summary>
/// Разрешение «своих» виртуальных эндпоинтов по PnP-instancе, а не по имени.
/// Связка: root-devnode драйвера (Enum\...\Service = VirtualAudioDriver) →
/// его дочерние устройства (DEVPKEY_Device_Children) → аудио-эндпоинты
/// (SWD\MMDEVAPI\{MMDevice.ID}). Идентификация устойчива к переименованию
/// endpoints и локализации (см. TODO(М2) в WasapiAudioEngine).
/// </summary>
internal static class VirtualEndpointResolver
{
    // devpkey.h: DEVPKEY_Device_Children {4340a6c5-93fa-4706-97-2c-7b648008a5a7},9.
    private static readonly Guid DevKeyChildren = new("4340a6c5-93fa-4706-97-2c-7b648008a5a7");
    private const uint DevPidChildren = 9;

    private const string EndpointPrefix = @"SWD\MMDEVAPI\";

    private const int CrSuccess = 0;
    private const int CrBufferSmall = 0x1A;
    private const uint LocateDevNodeNormal = 0;

    /// <summary>
    /// MMDevice.ID эндпоинтов, чьим непосредственным предком является устройство
    /// нашего драйвера. Пусто — драйвер не установлен или PnP-дерево недоступно
    /// (тогда вызывающий использует имя как фолбэк).
    /// </summary>
    public static HashSet<string> ResolveVirtualEndpointIds()
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var rootId in FindDriverRootInstanceIds())
            {
                foreach (var child in GetChildren(rootId))
                {
                    if (child.StartsWith(EndpointPrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        result.Add(child.Substring(EndpointPrefix.Length));
                    }
                }
            }
        }
        catch
        {
            // PnP-дерево недоступно — пустое множество, вызывающий решает по имени.
        }

        return result;
    }

    /// <summary>Instance-id root-devnode'ов драйвера: Enum\ROOT\*\* с Service=VirtualAudioDriver.</summary>
    private static IEnumerable<string> FindDriverRootInstanceIds()
    {
        using var enumKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum");
        if (enumKey is null)
        {
            yield break;
        }

        foreach (var className in enumKey.GetSubKeyNames())
        {
            using var classKey = enumKey.OpenSubKey(className);
            if (classKey is null)
            {
                continue;
            }

            foreach (var deviceName in classKey.GetSubKeyNames())
            {
                using var deviceKey = classKey.OpenSubKey(deviceName);
                if (deviceKey?.GetValue("Service") is string service &&
                    service.Equals("VirtualAudioDriver", StringComparison.OrdinalIgnoreCase))
                {
                    yield return $@"{className}\{deviceName}";
                }
            }
        }
    }

    /// <summary>Прямые дети devnode'а (DEVPKEY_Device_Children, STRING_LIST).</summary>
    private static IEnumerable<string> GetChildren(string instanceId)
    {
        if (CM_Locate_DevNodeW(out var devInst, instanceId, LocateDevNodeNormal) != CrSuccess)
        {
            yield break;
        }

        var key = new DevPropKey { Fmtid = DevKeyChildren, Pid = DevPidChildren };
        uint size = 0;
        var status = CM_Get_DevNode_PropertyW(devInst, ref key, IntPtr.Zero, ref size, 0);
        if (status != CrBufferSmall || size == 0)
        {
            yield break;
        }

        var buffer = Marshal.AllocHGlobal(checked((int)size));
        try
        {
            status = CM_Get_DevNode_PropertyW(devInst, ref key, buffer, ref size, 0);
            if (status != CrSuccess)
            {
                yield break;
            }

            // STRING_LIST: wide-строки, разделённые двойным нулём.
            var offset = 0;
            while (offset + 1 < size)
            {
                var value = Marshal.PtrToStringUni(buffer + offset);
                if (string.IsNullOrEmpty(value))
                {
                    break;
                }

                yield return value;
                offset += (value.Length + 1) * 2;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DevPropKey
    {
        public Guid Fmtid;
        public uint Pid;
    }

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Locate_DevNodeW(
        out uint pdwDevInst,
        [MarshalAs(UnmanagedType.LPWStr)] string pDeviceID,
        uint ulFlags);

    [DllImport("cfgmgr32.dll")]
    private static extern int CM_Get_DevNode_PropertyW(
        uint dnDevInst,
        ref DevPropKey propertyKey,
        IntPtr propertyData,
        ref uint dataSize,
        uint ulFlags);
}
