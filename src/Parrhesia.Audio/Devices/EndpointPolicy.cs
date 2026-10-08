using System.Runtime.InteropServices;

namespace Parrhesia.Audio.Devices;

/// <summary>
/// Управление своими аудио-эндпоинтами:
///  • переименование — официальный путь MMDevice API
///    (IMMDevice::OpenPropertyStore → IPropertyStore::SetValue → Commit),
///    это же делает mmsys.cpl; изменение персистентно до пересоздания
///    эндпоинта;
///  • видимость — COM-класс политики аудио (на Windows1126100 класс
///    PolicyConfigClient …99a заменён на …99c в AudioSes.dll), метод
///    SetEndpointVisibility.
/// Контракт: вызывать вне аудио-потока; best-effort (false — нет устройства
/// или отказ COM), исключения не бросает; детали — в <see cref="LastError"/>.
/// </summary>
public static class EndpointPolicy
{
    /// <summary>Продуктовое имя устройства из INF (DeviceDesc) — суффикс отображения.</summary>
    internal const string DeviceProductName = "Parrhesia Virtual Audio";

    // Системно-дефолтные базовые имена (локализованные и английские) —
    // признак того, что владелец ещё не переименовывал эндпоинт.
    private static readonly string[] GenericBaseNames =
    [
        "Динамики",
        "Набор микрофонов",
        "Микрофон",
        "Speakers",
        "Microphone Array",
        "Microphone",
    ];
    // Класс политики конфигурации аудио (AudioSes.dll; замена PolicyConfigClient).
    private static readonly Guid PolicyConfigClientClsid = new("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9");

    // PKEY_Device_DeviceDesc {a45c254e-df1c-4efd-8020-67d146a850e0},2 —
    // «базовое имя» эндпоинта: ровно то, что переписывает mmsys.cpl
    // (проверено вручную: переименование пишет pid2, отображение =
    //  pid2 + " (" + имя-устройства + ")").
    private static readonly PropertyKey FriendlyNameKey = new()
    {
        Fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"),
        Pid = 2,
    };

    private const ushort VtLpWStr = 31;
    private const uint StgmReadWrite = 1;

    /// <summary>Диагностика последнего отказа (для тестов/логов).</summary>
    internal static string? LastError { get; private set; }

    /// <summary>Снимает суффикс « (…)» с отображаемого имени (любое имя
    /// устройства — конфигурация драйвера может менять суффикс).</summary>
    internal static string StripAnyDeviceSuffix(string displayName)
    {
        var open = displayName.LastIndexOf(" (", StringComparison.Ordinal);
        if (open > 0 && displayName.EndsWith(')'))
        {
            return displayName[..open];
        }

        return displayName;
    }

    /// <summary>Снимает суффикс « (имя-устройства)» с отображаемого имени.</summary>
    internal static string StripDeviceSuffix(string displayName, string productName)
    {
        var suffix = " (" + productName + ")";
        return displayName.EndsWith(suffix, StringComparison.Ordinal)
            ? displayName[..^suffix.Length]
            : displayName;
    }

    /// <summary>Авто-переименование уместно, только если имя системное.</summary>
    internal static bool ShouldAutoRename(string baseName) =>
        Array.Exists(GenericBaseNames, g => g.Equals(baseName, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Переименовать эндпоинт (отображаемое имя, например «Parrhesia In»).
    /// Путь: официальный IPropertyStore (SetValue + Commit). Прямая запись
    /// в MMDevices-реестр закрыта даже для админа (ACL TrustedInstaller),
    /// IPolicyConfig.SetPropertyValue здесь отказывает (журнал М2) —
    /// при отказе false + <see cref="LastError"/>.
    /// </summary>
    public static bool TryRename(string deviceId, string displayName)
    {
        if (string.IsNullOrWhiteSpace(deviceId) || string.IsNullOrWhiteSpace(displayName))
        {
            return false;
        }

        return TryRenameViaPropertyStore(deviceId, displayName);
    }

    private static bool TryRenameViaPropertyStore(string deviceId, string displayName)
    {
        LastError = null;
        try
        {
            var enumeratorType = Type.GetTypeFromCLSID(
                new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"), throwOnError: false);
            if (enumeratorType is null)
            {
                LastError = "MMDeviceEnumerator не зарегистрирован";
                return false;
            }

            var rawEnumerator = Activator.CreateInstance(enumeratorType);
            if (rawEnumerator is not IMMDeviceEnumerator enumerator)
            {
                LastError = "MMDeviceEnumerator: cast to IMMDeviceEnumerator failed";
                return false;
            }

            try
            {
                var hr = enumerator.GetDevice(deviceId, out var devicePtr);
                if (hr < 0 || devicePtr == IntPtr.Zero)
                {
                    LastError = $"GetDevice=0x{hr:X8}";
                    return false;
                }

                var device = (IMMDevice)Marshal.GetObjectForIUnknown(devicePtr);
                Marshal.Release(devicePtr);
                try
                {
                    hr = device.OpenPropertyStore(StgmReadWrite, out var storePtr);
                    if (hr < 0 || storePtr == IntPtr.Zero)
                    {
                        LastError = $"OpenPropertyStore=0x{hr:X8}";
                        return false;
                    }

                    var store = (IPropertyStore)Marshal.GetObjectForIUnknown(storePtr);
                    Marshal.Release(storePtr);
                    try
                    {
                        var key = FriendlyNameKey;
                        var valuePtr = Marshal.StringToCoTaskMemUni(displayName);
                        try
                        {
                            var value = new PropVariant { Vt = VtLpWStr, PointerValue = valuePtr };
                            hr = store.SetValue(ref key, ref value);
                            if (hr < 0)
                            {
                                LastError = $"SetValue=0x{hr:X8}";
                                return false;
                            }
                        }
                        finally
                        {
                            Marshal.FreeCoTaskMem(valuePtr);
                        }

                        hr = store.Commit();
                        if (hr < 0)
                        {
                            LastError = $"Commit=0x{hr:X8}";
                            return false;
                        }

                        return true;
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(store);
                    }
                }
                finally
                {
                    Marshal.ReleaseComObject(device);
                }
            }
            finally
            {
                Marshal.ReleaseComObject(enumerator);
            }
        }
        catch (Exception ex)
        {
            LastError = ex.GetType().Name + ": " + ex.Message;
            return false;
        }
    }

    /// <summary>Показать/скрыть эндпоинт из списков системных устройств.</summary>
    public static bool TrySetVisible(string deviceId, bool visible)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            return false;
        }

        LastError = null;
        object? policy = null;
        try
        {
            var type = Type.GetTypeFromCLSID(PolicyConfigClientClsid, throwOnError: false);
            if (type is null)
            {
                LastError = "CLSID политики не зарегистрирован";
                return false;
            }

            policy = Activator.CreateInstance(type);
            if (policy is not IPolicyConfig config)
            {
                LastError = "не удалось привести к IPolicyConfig (IID не поддержан)";
                return false;
            }

            var hr = config.SetEndpointVisibility(deviceId, visible ? 1 : 0);
            if (hr < 0)
            {
                LastError = $"SetEndpointVisibility=0x{hr:X8}";
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            LastError = ex.GetType().Name + ": " + ex.Message;
            return false;
        }
        finally
        {
            if (policy is not null && Marshal.IsComObject(policy))
            {
                Marshal.ReleaseComObject(policy);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey
    {
        public Guid Fmtid;
        public uint Pid;
    }

    /// <summary>
    /// PROPVARIANT: vt@0, резерв@2, union-указатель@8 (x64; нативный размер —
    /// 24 байта: union включает DECIMAL).
    /// </summary>
    [StructLayout(LayoutKind.Explicit)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort Vt;
        [FieldOffset(8)] public IntPtr PointerValue;
        [FieldOffset(16)] public IntPtr UnionTail;
    }

    // ===== MMDevice API (официальные IID; порядок методов = vtable) =====

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig]
        int EnumAudioEndpoints(int dataFlow, uint stateMask, out IntPtr devices);

        [PreserveSig]
        int GetDefaultAudioEndpoint(int dataFlow, int role, out IntPtr device);

        [PreserveSig]
        int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IntPtr device);

        [PreserveSig]
        int RegisterEndpointNotificationCallback(IntPtr client);

        [PreserveSig]
        int UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig]
        int Activate(ref Guid iid, uint clsCtx, IntPtr activationParams, out IntPtr iface);

        [PreserveSig]
        int OpenPropertyStore(uint access, out IntPtr store);

        [PreserveSig]
        int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);

        [PreserveSig]
        int GetState(out uint state);
    }

    [ComImport]
    [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig]
        int GetCount(out uint count);

        [PreserveSig]
        int GetAt(uint index, out PropertyKey key);

        [PreserveSig]
        int GetValue(ref PropertyKey key, out PropVariant value);

        [PreserveSig]
        int SetValue(ref PropertyKey key, ref PropVariant value);

        [PreserveSig]
        int Commit();
    }

    // ===== Политика аудио (недокументированный, но стабильный IPolicyConfig) =====

    /// <summary>
    /// audiopolicy IPolicyConfig: порядок методов обязан совпадать с vtable
    /// (PreserveSig — HRESULT как int). Корректность слотов подтверждена
    /// тестом Visibility (SetEndpointVisibility — последний метод).
    /// </summary>
    [ComImport]
    [Guid("f8679f50-850a-41cf-9c72-430f290290c8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        [PreserveSig]
        int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, out IntPtr format);

        [PreserveSig]
        int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int defaultFormat, out IntPtr format);

        [PreserveSig]
        int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId);

        [PreserveSig]
        int SetDeviceFormat(
            [MarshalAs(UnmanagedType.LPWStr)] string deviceId,
            IntPtr endpointFormat,
            IntPtr mixFormat);

        [PreserveSig]
        int GetProcessingPeriod(
            [MarshalAs(UnmanagedType.LPWStr)] string deviceId,
            int defaultPeriod,
            out long defaultPeriodOut,
            out long minimumPeriod);

        [PreserveSig]
        int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string deviceId, ref long period);

        [PreserveSig]
        int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr mode);

        [PreserveSig]
        int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr mode);

        [PreserveSig]
        int GetPropertyValue(
            [MarshalAs(UnmanagedType.LPWStr)] string deviceId,
            ref PropertyKey key,
            IntPtr value);

        [PreserveSig]
        int SetPropertyValue(
            [MarshalAs(UnmanagedType.LPWStr)] string deviceId,
            ref PropertyKey key,
            ref PropVariant value);

        [PreserveSig]
        int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int role);

        [PreserveSig]
        int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int visible);
    }
}
