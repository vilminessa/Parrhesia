using System.Runtime.InteropServices;

namespace Parrhesia.Plugins.Vst3;

/// <summary>
/// Экземпляр VST3-плагина через нативный шим (см. native/vst3-shim).
/// Контракт потоков — как у <see cref="IAudioPlugin"/>: Prepare/GetState/
/// SetState/Dispose — вне аудио-потока; Process — только аудио-поток.
/// </summary>
internal sealed class Vst3Plugin : IAudioPlugin, IPluginEditor
{
    private IntPtr _instance;
    private bool _editorOpen;

    private Vst3Plugin(string classId)
    {
        Name = classId;
    }

    public string Name { get; }

    /// <summary>Число ошибок Process (симметрия ClapPlugin).</summary>
    public long ProcessErrors { get; private set; }

    /// <summary>VST3 не имеет «расширения отсутствия» — возможность выясняется в Open.</summary>
    public bool SupportsEditor => _instance != IntPtr.Zero;

    public bool IsOpen => _editorOpen;

    public int LatencySamples =>
        _instance == IntPtr.Zero ? 0 : Math.Max(0, Vst3Native.GetLatency(_instance));

    /// <summary>Создаёт экземпляр; ошибки шима → <see cref="PluginLoadException"/>.</summary>
    public static Vst3Plugin Create(string path, string classId)
    {
        var instance = Vst3Native.CreateInstance(path, classId);
        if (instance == IntPtr.Zero)
        {
            throw new PluginLoadException(
                $"VST3: не удалось создать {classId} ({path}): {Vst3Native.LastErrorMessage()}");
        }

        return new Vst3Plugin(classId) { _instance = instance };
    }

    public void Prepare(int sampleRate, int maxBlockFrames, int channels)
    {
        ObjectDisposedException.ThrowIf(_instance == IntPtr.Zero, this);
        if (Vst3Native.PrepareInstance(_instance, sampleRate, maxBlockFrames, channels) != 0)
        {
            throw new PluginLoadException($"VST3 prepare: {Vst3Native.LastErrorMessage()}");
        }
    }

    public void Process(Span<float> interleaved, int frames)
    {
        if (_instance == IntPtr.Zero || frames <= 0)
        {
            interleaved.Clear();
            return;
        }

        if (Vst3Native.ProcessBlock(_instance, interleaved, frames) != 0)
        {
            ProcessErrors++;
            interleaved.Clear();
        }
    }

    public byte[]? GetState()
    {
        ObjectDisposedException.ThrowIf(_instance == IntPtr.Zero, this);
        var size = Vst3Native.GetStateSize(_instance);
        if (size < 0)
        {
            throw new PluginLoadException($"VST3 getState: {Vst3Native.LastErrorMessage()}");
        }

        if (size == 0)
        {
            return null;
        }

        var buffer = new byte[size];
        var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            var written = Vst3Native.CopyState(_instance, handle.AddrOfPinnedObject(), buffer.Length);
            if (written < 0)
            {
                throw new PluginLoadException($"VST3 getState: {Vst3Native.LastErrorMessage()}");
            }

            if (written < buffer.Length)
            {
                Array.Resize(ref buffer, written);
            }

            return buffer;
        }
        finally
        {
            handle.Free();
        }
    }

    public void SetState(byte[]? state)
    {
        ObjectDisposedException.ThrowIf(_instance == IntPtr.Zero, this);
        if (state is null)
        {
            return;
        }

        if (Vst3Native.SetStateBytes(_instance, state) != 0)
        {
            throw new PluginLoadException($"VST3 setState: {Vst3Native.LastErrorMessage()}");
        }
    }

    public void Dispose()
    {
        if (_instance == IntPtr.Zero)
        {
            return;
        }

        Close(); // view не должен пережить плагина (шим закрывает и в Destroy — двойной Close идемпотентен)
        Vst3Native.DestroyInstance(_instance);
        _instance = IntPtr.Zero;
    }

    // ===== IPluginEditor (IPlugView; main-thread) =====

    public bool Open(IntPtr hostWindow)
    {
        if (_instance == IntPtr.Zero || hostWindow == IntPtr.Zero)
        {
            return false;
        }

        if (_editorOpen)
        {
            return true;
        }

        if (Vst3Native.EditorOpen(_instance, hostWindow) != 0)
        {
            return false;
        }

        _editorOpen = true;
        return true;
    }

    public void Close()
    {
        if (!_editorOpen)
        {
            return;
        }

        _editorOpen = false;
        if (_instance != IntPtr.Zero)
        {
            Vst3Native.EditorClose(_instance);
        }
    }

    public (int Width, int Height) PreferredSize
    {
        get
        {
            if (!_editorOpen || _instance == IntPtr.Zero)
            {
                return (0, 0);
            }

            if (Vst3Native.EditorGetSize(_instance, out var width, out var height) != 0)
            {
                return (0, 0);
            }

            return (width, height);
        }
    }
}
