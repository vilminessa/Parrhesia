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
    // devpkey.h: DEVPKEY_Device_Children {4340a6c5-93fa-4706-972c-7b648008a5a7},9.
    private static readonly Guid DevKeyChildren = new("4340a6c5-93fa-4706-972c-7b648008a5a7");
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

    /// <summary>Пошаговая диагностика (для логов/поддержки): корни → locate → children.</summary>
    internal static string Diagnose()
    {
        var report = string.Empty;
        try
        {
            // Этап реестра — пошагово.
            using (var ek = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum"))
            {
                if (ek is null)
                {
                    return "Enum key = null";
                }

                var classes = ek.GetSubKeyNames();
                report += $"classes={classes.Length} ";
                using (var root = ek.OpenSubKey("ROOT"))
                {
                    if (root is null)
                    {
                        return report + "ROOT=null";
                    }

                    var rootDevices = root.GetSubKeyNames();
                    report += $"ROOT=[{string.Join(",", rootDevices)}] ";
                    using (var media = root.OpenSubKey("MEDIA"))
                    {
                        if (media is null)
                        {
                            return report + "MEDIA=null";
                        }

                        foreach (var d in media.GetSubKeyNames())
                        {
                            using var dk = media.OpenSubKey(d);
                            var svc = dk?.GetValue("Service");
                            report += $"{d}:Service={svc ?? "<null>"}({svc?.GetType().Name ?? "-"}) ";
                        }
                    }
                }
            }

            var roots = FindDriverRootInstanceIds().ToList();
            report += " | roots=[" + string.Join(", ", roots) + "] ";

            // Тот же обход, что Find..., но со счётчиками — точка расхождения.
            {
                using var ek2 = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum");
                var total = 0;
                var matched = 0;
                foreach (var cls in ek2!.GetSubKeyNames())
                {
                    using var ck = ek2.OpenSubKey(cls);
                    if (ck is null)
                    {
                        continue;
                    }

                    foreach (var dev in ck.GetSubKeyNames())
                    {
                        total++;
                        using var dk = ck.OpenSubKey(dev);
                        var raw = dk?.GetValue("Service");
                        if (raw is string svc)
                        {
                            if (svc.IndexOf("VirtualAudioDriver", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                matched++;
                                report += $"MATCH({cls}\\{dev}=[{svc}]) ";
                            }
                        }
                    }
                }

                report += $"total={total} matched={matched} ";
            }
            foreach (var rootId in roots)
            {
                var locate = CM_Locate_DevNodeW(out var devInst, rootId, LocateDevNodeNormal);
                report += $"locate({rootId})={locate} inst={devInst} ";
                if (locate != CrSuccess)
                {
                    continue;
                }

                var key = new DevPropKey { Fmtid = DevKeyChildren, Pid = DevPidChildren };
                uint size = 0;
                var s1 = CM_Get_DevNode_PropertyW(devInst, ref key, out var type, IntPtr.Zero, ref size, 0);
                report += $"children#1={s1} type={type} size={size} ";
                if (size == 0)
                {
                    continue;
                }

                var buffer = Marshal.AllocHGlobal(checked((int)size));
                try
                {
                    var s2 = CM_Get_DevNode_PropertyW(devInst, ref key, out _, buffer, ref size, 0);
                    report += $"children#2={s2} ";
                    if (s2 == CrSuccess)
                    {
                        var offset = 0;
                        while (offset + 1 < size)
                        {
                            var child = Marshal.PtrToStringUni(buffer + offset);
                            if (string.IsNullOrEmpty(child))
                            {
                                break;
                            }

                            report += "child=" + child + "; ";
                            offset += (child.Length + 1) * 2;
                        }
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
        }
        catch (Exception ex)
        {
            report += "EX: " + ex.Message;
        }

        return report;
    }

    /// <summary>
    /// Instance-id root-devnode'ов драйвера: Enum\*\*\* с Service=VirtualAudioDriver.
    /// Ключи Enum многоуровневые (ROOT\MEDIA\0001) — обход рекурсивный.
    /// </summary>
    private static List<string> FindDriverRootInstanceIds()
    {
        var result = new List<string>();
        using var enumKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum");
        if (enumKey is null)
        {
            return result;
        }

        ScanForService(enumKey, string.Empty, result);
        return result;
    }

    private static void ScanForService(RegistryKey parent, string path, List<string> result)
    {
        string[] names;
        try
        {
            names = parent.GetSubKeyNames();
        }
        catch
        {
            return; // ключ недоступен — пропускаем ветку
        }

        foreach (var name in names)
        {
            RegistryKey? key;
            try
            {
                key = parent.OpenSubKey(name);
            }
            catch
            {
                continue; // ACL закрыл ключ
            }

            if (key is null)
            {
                continue;
            }

            using (key)
            {
                var full = path.Length == 0 ? name : path + @"\" + name;
                try
                {
                    if (key.GetValue("Service") is string service &&
                        service.Equals("VirtualAudioDriver", StringComparison.OrdinalIgnoreCase))
                    {
                        result.Add(full);
                        continue; // наш devnode — глубже не ищем
                    }

                    ScanForService(key, full, result);
                }
                catch
                {
                    // отказ чтения — пропускаем ветку
                }
            }
        }
    }

    /// <summary>
    /// Суффикс фида нашего драйвера (lanes, М2): instance-id первого devnode
    /// с endpoints, с '\' → '_' — ровно так же имя строит драйвер
    /// (GetInstanceIdSuffix в common.cpp). false — драйвера нет.
    /// </summary>
    public static bool TryGetFeedSuffix(out string suffix)
    {
        suffix = string.Empty;
        try
        {
            foreach (var rootId in FindDriverRootInstanceIds())
            {
                foreach (var child in GetChildren(rootId))
                {
                    if (child.StartsWith(EndpointPrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        suffix = rootId.Replace('\\', '_');
                        return suffix.Length > 0;
                    }
                }
            }
        }
        catch
        {
            // PnP-дерево недоступно — вызывающий уходит на legacy-путь.
        }

        return false;
    }

    /// <summary>Прямые дети devnode'а (DEVPKEY_Device_Children, STRING_LIST).</summary>
    private static List<string> GetChildren(string instanceId)
    {
        var result = new List<string>();
        if (CM_Locate_DevNodeW(out var devInst, instanceId, LocateDevNodeNormal) != CrSuccess)
        {
            return result;
        }

        var key = new DevPropKey { Fmtid = DevKeyChildren, Pid = DevPidChildren };
        uint size = 0;
        var status = CM_Get_DevNode_PropertyW(devInst, ref key, out _, IntPtr.Zero, ref size, 0);
        if (status != CrBufferSmall || size == 0)
        {
            return result;
        }

        var buffer = Marshal.AllocHGlobal(checked((int)size));
        try
        {
            status = CM_Get_DevNode_PropertyW(devInst, ref key, out _, buffer, ref size, 0);
            if (status != CrSuccess)
            {
                return result;
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

                result.Add(value);
                offset += (value.Length + 1) * 2;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        return result;
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
        out uint propertyType,
        IntPtr propertyData,
        ref uint dataSize,
        uint ulFlags);
}
