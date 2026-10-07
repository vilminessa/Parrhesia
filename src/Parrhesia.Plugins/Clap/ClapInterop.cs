using System.Runtime.InteropServices;

namespace Parrhesia.Plugins.Clap;

/// <summary>Константы CLAP (версия хоста и идентификаторы расширений).</summary>
internal static class Clap
{
    public const uint VersionMajor = 1;
    public const uint VersionMinor = 2;
    public const uint VersionRevision = 10;

    public const string PluginFactoryId = "clap.plugin-factory";
    public const string ExtAudioPorts = "clap.audio-ports";
    public const string ExtState = "clap.state";
    public const string ExtLatency = "clap.latency";
    public const string ExtThreadCheck = "clap.thread-check";
    public const string ExtHostLatency = "clap.latency";

    public const uint AudioPortIsMain = 1 << 0;

    public const int ProcessError = 0;
    public const int ProcessContinue = 1;

    public const int NameSize = 256;
}

// ===== Структуры (байт-в-байт по заголовкам CLAP1.2.10) =====

[StructLayout(LayoutKind.Sequential)]
internal struct ClapVersion
{
    public uint Major;
    public uint Minor;
    public uint Revision;
}

[StructLayout(LayoutKind.Sequential)]
internal struct ClapPluginEntry
{
    public ClapVersion Version;
    public IntPtr Init;
    public IntPtr Deinit;
    public IntPtr GetFactory;
}

[StructLayout(LayoutKind.Sequential)]
internal struct ClapHost
{
    public ClapVersion Version;
    public IntPtr HostData;
    public IntPtr Name;
    public IntPtr Vendor;
    public IntPtr Url;
    public IntPtr HostVersion;
    public IntPtr GetExtension;
    public IntPtr RequestRestart;
    public IntPtr RequestProcess;
    public IntPtr RequestCallback;
}

[StructLayout(LayoutKind.Sequential)]
internal struct ClapPluginDescriptor
{
    public ClapVersion ClapVersion;
    public IntPtr Id;
    public IntPtr Name;
    public IntPtr Vendor;
    public IntPtr Url;
    public IntPtr ManualUrl;
    public IntPtr SupportUrl;
    public IntPtr VersionString;
    public IntPtr Description;
    public IntPtr Features;
}

[StructLayout(LayoutKind.Sequential)]
internal struct ClapPluginNative
{
    public IntPtr Desc;
    public IntPtr PluginData;
    public IntPtr Init;
    public IntPtr Destroy;
    public IntPtr Activate;
    public IntPtr Deactivate;
    public IntPtr StartProcessing;
    public IntPtr StopProcessing;
    public IntPtr Reset;
    public IntPtr Process;
    public IntPtr GetExtension;
    public IntPtr OnMainThread;
}

[StructLayout(LayoutKind.Sequential)]
internal struct ClapPluginFactory
{
    public IntPtr GetPluginCount;
    public IntPtr GetPluginDescriptor;
    public IntPtr CreatePlugin;
}

[StructLayout(LayoutKind.Sequential)]
internal struct ClapProcess
{
    public long SteadyTime;
    public uint FramesCount;
    public IntPtr Transport;
    public IntPtr AudioInputs;
    public IntPtr AudioOutputs;
    public uint AudioInputsCount;
    public uint AudioOutputsCount;
    public IntPtr InEvents;
    public IntPtr OutEvents;
}

[StructLayout(LayoutKind.Sequential)]
internal struct ClapAudioBuffer
{
    public IntPtr Data32;
    public IntPtr Data64;
    public uint ChannelCount;
    public uint BufferLatency;
    public ulong ConstantMask;
}

[StructLayout(LayoutKind.Sequential)]
internal struct ClapInputEvents
{
    public IntPtr Ctx;
    public IntPtr Size;
    public IntPtr Get;
}

[StructLayout(LayoutKind.Sequential)]
internal struct ClapOutputEvents
{
    public IntPtr Ctx;
    public IntPtr TryPush;
}

[StructLayout(LayoutKind.Sequential)]
internal struct ClapIStream
{
    public IntPtr Ctx;
    public IntPtr Read;
}

[StructLayout(LayoutKind.Sequential)]
internal struct ClapOStream
{
    public IntPtr Ctx;
    public IntPtr Write;
}

[StructLayout(LayoutKind.Sequential)]
internal struct ClapPluginAudioPorts
{
    public IntPtr Count;
    public IntPtr Get;
}

[StructLayout(LayoutKind.Sequential)]
internal struct ClapPluginState
{
    public IntPtr Save;
    public IntPtr Load;
}

[StructLayout(LayoutKind.Sequential)]
internal struct ClapPluginLatency
{
    public IntPtr Get;
}

[StructLayout(LayoutKind.Sequential)]
internal struct ClapHostThreadCheck
{
    public IntPtr IsMainThread;
    public IntPtr IsAudioThread;
}

[StructLayout(LayoutKind.Sequential)]
internal struct ClapHostLatency
{
    public IntPtr Changed;
}

/// <summary>clap_audio_port_info_t: id, char name[256], flags, channel_count, port_type, in_place_pair.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct ClapAudioPortInfo
{
    public uint Id;
    public fixed byte Name[Clap.NameSize];
    public uint Flags;
    public uint ChannelCount;
    public IntPtr PortType;
    public uint InPlacePair;
}

// ===== Делегаты (все cdecl; bool — I1, это байтовый C bool) =====

internal static class ClapDelegates
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    public delegate bool EntryInit(IntPtr pluginPathUtf8);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void EntryDeinit();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate IntPtr GetFactoryFn(IntPtr factoryIdUtf8);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate uint FactoryCountFn(IntPtr factory);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate IntPtr FactoryDescriptorFn(IntPtr factory, uint index);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate IntPtr FactoryCreateFn(IntPtr factory, IntPtr host, IntPtr pluginIdUtf8);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    public delegate bool PluginInit(IntPtr plugin);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void PluginDestroy(IntPtr plugin);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    public delegate bool PluginActivate(IntPtr plugin, double sampleRate, uint minFrames, uint maxFrames);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void PluginVoid(IntPtr plugin);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    public delegate bool PluginStartProcessing(IntPtr plugin);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate int PluginProcess(IntPtr plugin, IntPtr process);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate IntPtr PluginGetExtension(IntPtr plugin, IntPtr idUtf8);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate IntPtr HostGetExtension(IntPtr host, IntPtr idUtf8);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void HostVoid(IntPtr host);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    public delegate bool HostBool(IntPtr host);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate uint AudioPortsCount(IntPtr plugin, [MarshalAs(UnmanagedType.I1)] bool isInput);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    public delegate bool AudioPortsGet(IntPtr plugin, uint index, [MarshalAs(UnmanagedType.I1)] bool isInput, IntPtr info);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate uint LatencyGet(IntPtr plugin);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    public delegate bool StateSave(IntPtr plugin, IntPtr stream);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    public delegate bool StateLoad(IntPtr plugin, IntPtr stream);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate long StreamRead(IntPtr stream, IntPtr buffer, ulong size);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate long StreamWrite(IntPtr stream, IntPtr buffer, ulong size);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate uint InputEventsSize(IntPtr list);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate IntPtr InputEventsGet(IntPtr list, uint index);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    public delegate bool OutputEventsTryPush(IntPtr list, IntPtr evt);
}

/// <summary>Разбор UTF8-строк из непамяти.</summary>
internal static class ClapUtf8
{
    public static string? Read(IntPtr ptr) =>
        ptr == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(ptr);

    /// <summary>Копирует строку в неуправляемую память (освобождать через Free).</summary>
    public static IntPtr Alloc(string value) => Marshal.StringToCoTaskMemUTF8(value);

    public static void Free(IntPtr ptr)
    {
        if (ptr != IntPtr.Zero)
        {
            Marshal.FreeCoTaskMem(ptr);
        }
    }
}
