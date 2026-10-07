using System.Runtime.InteropServices;

namespace Parrhesia.Plugins.Vst3;

/// <summary>
/// P/Invoke к parr_vst3_shim.dll (см. native/vst3-shim). Шим грузится один
/// раз на процесс; экспорты — cdecl (см. parr_vst3.h). Поля *Ptr — кэшированные
/// делегаты экспортов (имена не конфликтуют с обёртками).
/// </summary>
internal static unsafe class Vst3Native
{
    private static readonly EnumerateFn EnumeratePtr;
    private static readonly LastErrorFn LastErrorPtr;
    private static readonly CreateFn CreatePtr;
    private static readonly DestroyFn DestroyPtr;
    private static readonly PrepareFn PreparePtr;
    private static readonly ProcessFn ProcessPtr;
    private static readonly LatencyFn LatencyPtr;
    private static readonly GetStateFn GetStatePtr;
    private static readonly SetStateFn SetStatePtr;

    static Vst3Native()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "parr_vst3_shim.dll");
        if (!NativeLibrary.TryLoad(path, out var module))
        {
            throw new PluginLoadException(
                $"Не удалось загрузить VST3-шим: {path} " +
                "(соберите src/Parrhesia.Plugins/native/vst3-shim/build-vst3-shim.bat)");
        }

        EnumeratePtr = Marshal.GetDelegateForFunctionPointer<EnumerateFn>(Export(module, "Pv3Enumerate"));
        LastErrorPtr = Marshal.GetDelegateForFunctionPointer<LastErrorFn>(Export(module, "Pv3LastError"));
        CreatePtr = Marshal.GetDelegateForFunctionPointer<CreateFn>(Export(module, "Pv3Create"));
        DestroyPtr = Marshal.GetDelegateForFunctionPointer<DestroyFn>(Export(module, "Pv3Destroy"));
        PreparePtr = Marshal.GetDelegateForFunctionPointer<PrepareFn>(Export(module, "Pv3Prepare"));
        ProcessPtr = Marshal.GetDelegateForFunctionPointer<ProcessFn>(Export(module, "Pv3Process"));
        LatencyPtr = Marshal.GetDelegateForFunctionPointer<LatencyFn>(Export(module, "Pv3GetLatency"));
        GetStatePtr = Marshal.GetDelegateForFunctionPointer<GetStateFn>(Export(module, "Pv3GetState"));
        SetStatePtr = Marshal.GetDelegateForFunctionPointer<SetStateFn>(Export(module, "Pv3SetState"));
    }

    private static IntPtr Export(IntPtr module, string name) =>
        NativeLibrary.GetExport(module, name);

    // ===== Делегаты экспортов (cdecl) =====

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void EnumerateCallback(IntPtr context, IntPtr classId, IntPtr name, IntPtr vendor);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int EnumerateFn(IntPtr modulePath, EnumerateCallback callback, IntPtr context);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate IntPtr LastErrorFn();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate IntPtr CreateFn(IntPtr modulePath, IntPtr classId);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void DestroyFn(IntPtr instance);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int PrepareFn(IntPtr instance, double sampleRate, int maxBlockFrames, int channels);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int ProcessFn(IntPtr instance, float* interleaved, int frames);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int LatencyFn(IntPtr instance);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int GetStateFn(IntPtr instance, IntPtr buffer, int capacity);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int SetStateFn(IntPtr instance, IntPtr buffer, int length);

    // ===== Обёртки с маршировкой =====

    public static int Enumerate(string modulePath, EnumerateCallback callback)
    {
        var pathPtr = Marshal.StringToCoTaskMemUTF8(modulePath);
        try
        {
            return EnumeratePtr(pathPtr, callback, IntPtr.Zero);
        }
        finally
        {
            Marshal.FreeCoTaskMem(pathPtr);
        }
    }

    public static string? LastErrorMessage()
    {
        var ptr = LastErrorPtr();
        return ptr == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(ptr);
    }

    public static IntPtr CreateInstance(string modulePath, string classId)
    {
        var pathPtr = Marshal.StringToCoTaskMemUTF8(modulePath);
        var idPtr = Marshal.StringToCoTaskMemUTF8(classId);
        try
        {
            return CreatePtr(pathPtr, idPtr);
        }
        finally
        {
            Marshal.FreeCoTaskMem(pathPtr);
            Marshal.FreeCoTaskMem(idPtr);
        }
    }

    public static int PrepareInstance(IntPtr instance, double sampleRate, int maxBlockFrames, int channels) =>
        PreparePtr(instance, sampleRate, maxBlockFrames, channels);

    public static int ProcessBlock(IntPtr instance, Span<float> interleaved, int frames)
    {
        fixed (float* pointer = interleaved)
        {
            return ProcessPtr(instance, pointer, frames);
        }
    }

    public static int GetLatency(IntPtr instance) => LatencyPtr(instance);

    public static void DestroyInstance(IntPtr instance) => DestroyPtr(instance);

    /// <summary>Запрашивает размер (buffer=null) или пишет состояние; возвращает размер/&lt;0.</summary>
    public static int GetStateSize(IntPtr instance) => GetStatePtr(instance, IntPtr.Zero, 0);

    public static int CopyState(IntPtr instance, IntPtr buffer, int capacity) =>
        GetStatePtr(instance, buffer, capacity);

    public static int SetStateBytes(IntPtr instance, byte[] state)
    {
        if (state.Length == 0)
        {
            return SetStatePtr(instance, IntPtr.Zero, 0);
        }

        var handle = GCHandle.Alloc(state, GCHandleType.Pinned);
        try
        {
            return SetStatePtr(instance, handle.AddrOfPinnedObject(), state.Length);
        }
        finally
        {
            handle.Free();
        }
    }
}
