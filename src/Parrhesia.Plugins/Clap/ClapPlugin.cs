using System.Runtime.InteropServices;

namespace Parrhesia.Plugins.Clap;

/// <summary>
/// Экземпляр CLAP-плагина поверх <see cref="IAudioPlugin"/>.
/// Контракт: конструктор/Prepare/SetState/GetState/Dispose — вне аудио-потока;
/// Process — только аудио-поток (первый вызов отмечает аудио-поток для
/// clap.thread-check). Буферы планарные, аллоцируются в Prepare — в Process
/// аллокаций нет.
/// </summary>
internal sealed unsafe class ClapPlugin : IAudioPlugin, IPluginEditor
{
    private readonly ClapModule _module;
    private readonly string _path;
    private readonly IntPtr _plugin;

    // Host-блок в неуправляемой памяти + корни делегатов/строк.
    private readonly IntPtr _hostPtr;
    private readonly GCHandle _selfHandle;
    private readonly IntPtr _namePtr;
    private readonly IntPtr _vendorPtr;
    private readonly IntPtr _urlPtr;
    private readonly IntPtr _hostVersionPtr;
    private readonly ClapDelegates.HostGetExtension _hostGetExtension;
    private readonly ClapDelegates.HostVoid _hostRequestRestart;
    private readonly ClapDelegates.HostVoid _hostRequestProcess;
    private readonly ClapDelegates.HostVoid _hostRequestCallback;
    private readonly ClapDelegates.HostBool _hostIsMainThread;
    private readonly ClapDelegates.HostBool _hostIsAudioThread;
    private readonly IntPtr _threadCheckPtr;
    private readonly IntPtr _hostLatencyPtr;
    private readonly ClapDelegates.HostVoid _hostLatencyChanged;

    // Корни делегатов плагина.
    private readonly ClapDelegates.PluginInit _init;
    private readonly ClapDelegates.PluginDestroy _destroy;
    private readonly ClapDelegates.PluginActivate _activate;
    private readonly ClapDelegates.PluginVoid _deactivate;
    private readonly ClapDelegates.PluginStartProcessing _startProcessing;
    private readonly ClapDelegates.PluginVoid _stopProcessing;
    private readonly ClapDelegates.PluginVoid _reset;
    private readonly ClapDelegates.PluginProcess _process;
    private readonly ClapDelegates.PluginGetExtension _getExtension;

    // Расширения плагина.
    private readonly IntPtr _extAudioPorts;
    private readonly IntPtr _extState;
    private readonly IntPtr _extLatency;
    private readonly IntPtr _extGui;

    // GUI-состояние редактора.
    private IntPtr _guiApiString;
    private bool _editorOpen;

    // Аудио-буферы (Prepare).
    private int _channels;
    private int _maxFrames;
    private float[][] _inChannels = [];
    private float[][] _outChannels = [];
    private GCHandle[] _channelHandles = [];
    private GCHandle _inPtrsHandle;
    private GCHandle _outPtrsHandle;
    private GCHandle _inBuffersHandle;
    private GCHandle _outBuffersHandle;
    private GCHandle _inEventsHandle;
    private GCHandle _outEventsHandle;
    private IntPtr _inBuffersPtr;
    private IntPtr _outBuffersPtr;
    private IntPtr _inEventsPtr;
    private IntPtr _outEventsPtr;
    private readonly ClapDelegates.InputEventsSize _inputEventsSize;
    private readonly ClapDelegates.InputEventsGet _inputEventsGet;
    private readonly ClapDelegates.OutputEventsTryPush _outputEventsTryPush;

    // Stream-делегаты для state (корни).
    private readonly ClapDelegates.StreamWrite _streamWrite;
    private readonly ClapDelegates.StreamRead _streamRead;

    private readonly int _mainThreadId = Environment.CurrentManagedThreadId;
    private int _audioThreadId;

    private long _steadyTime;
    private bool _active;
    private bool _processing;
    private int _sampleRate;
    private int _cachedLatency;
    private bool _disposed;

    /// <summary>Хост запросил рестарт плагина (потребуется деактивация/активация).</summary>
    public bool RestartRequested { get; private set; }

    /// <summary>Хост запросил обработку (для пробуждения плагина).</summary>
    public bool ProcessRequested { get; private set; }

    /// <summary>Число ошибок Process (CLAP_PROCESS_ERROR).</summary>
    public long ProcessErrors { get; private set; }
    public string Name { get; }

    public int LatencySamples
    {
        get
        {
            if (_extLatency != IntPtr.Zero && _active)
            {
                var fn = Marshal.GetDelegateForFunctionPointer<ClapDelegates.LatencyGet>(
                    Marshal.ReadIntPtr(_extLatency, 0));
                _cachedLatency = unchecked((int)fn(_plugin));
            }

            return _cachedLatency;
        }
    }

    private ClapPlugin(ClapModule module, string path, string pluginId)
    {
        _module = module;
        _path = path;
        Name = pluginId;

        _selfHandle = GCHandle.Alloc(this);

        // Делегаты host-колбэков (корни на всё время жизни).
        _hostGetExtension = HostGetExtension;
        _hostRequestRestart = _ => RestartRequested = true;
        _hostRequestProcess = _ => ProcessRequested = true;
        _hostRequestCallback = _ => { };
        _hostIsMainThread = _ => Environment.CurrentManagedThreadId == _mainThreadId;
        _hostIsAudioThread = _ => _audioThreadId != 0 && Environment.CurrentManagedThreadId == _audioThreadId;
        _hostLatencyChanged = _ => { };

        _namePtr = ClapUtf8.Alloc("Parrhesia");
        _vendorPtr = ClapUtf8.Alloc("Parrhesia");
        _urlPtr = ClapUtf8.Alloc("https://parrhesia.local");
        _hostVersionPtr = ClapUtf8.Alloc("1.0.0");

        var threadCheck = new ClapHostThreadCheck
        {
            IsMainThread = Marshal.GetFunctionPointerForDelegate(_hostIsMainThread),
            IsAudioThread = Marshal.GetFunctionPointerForDelegate(_hostIsAudioThread),
        };
        _threadCheckPtr = Marshal.AllocHGlobal(Marshal.SizeOf<ClapHostThreadCheck>());
        Marshal.StructureToPtr(threadCheck, _threadCheckPtr, false);

        var hostLatency = new ClapHostLatency
        {
            Changed = Marshal.GetFunctionPointerForDelegate(_hostLatencyChanged),
        };
        _hostLatencyPtr = Marshal.AllocHGlobal(Marshal.SizeOf<ClapHostLatency>());
        Marshal.StructureToPtr(hostLatency, _hostLatencyPtr, false);

        var host = new ClapHost
        {
            Version = new ClapVersion
            {
                Major = Clap.VersionMajor,
                Minor = Clap.VersionMinor,
                Revision = Clap.VersionRevision,
            },
            HostData = GCHandle.ToIntPtr(_selfHandle),
            Name = _namePtr,
            Vendor = _vendorPtr,
            Url = _urlPtr,
            HostVersion = _hostVersionPtr,
            GetExtension = Marshal.GetFunctionPointerForDelegate(_hostGetExtension),
            RequestRestart = Marshal.GetFunctionPointerForDelegate(_hostRequestRestart),
            RequestProcess = Marshal.GetFunctionPointerForDelegate(_hostRequestProcess),
            RequestCallback = Marshal.GetFunctionPointerForDelegate(_hostRequestCallback),
        };

        _hostPtr = Marshal.AllocHGlobal(Marshal.SizeOf<ClapHost>());
        Marshal.StructureToPtr(host, _hostPtr, false);

        try
        {
            _plugin = module.CreatePlugin(_hostPtr, pluginId);

            var plugin = Marshal.PtrToStructure<ClapPluginNative>(_plugin);
            _init = Marshal.GetDelegateForFunctionPointer<ClapDelegates.PluginInit>(plugin.Init);
            _destroy = Marshal.GetDelegateForFunctionPointer<ClapDelegates.PluginDestroy>(plugin.Destroy);
            _activate = Marshal.GetDelegateForFunctionPointer<ClapDelegates.PluginActivate>(plugin.Activate);
            _deactivate = Marshal.GetDelegateForFunctionPointer<ClapDelegates.PluginVoid>(plugin.Deactivate);
            _startProcessing = Marshal.GetDelegateForFunctionPointer<ClapDelegates.PluginStartProcessing>(plugin.StartProcessing);
            _stopProcessing = Marshal.GetDelegateForFunctionPointer<ClapDelegates.PluginVoid>(plugin.StopProcessing);
            _reset = Marshal.GetDelegateForFunctionPointer<ClapDelegates.PluginVoid>(plugin.Reset);
            _process = Marshal.GetDelegateForFunctionPointer<ClapDelegates.PluginProcess>(plugin.Process);
            _getExtension = Marshal.GetDelegateForFunctionPointer<ClapDelegates.PluginGetExtension>(plugin.GetExtension);

            if (!_init(_plugin))
            {
                throw new PluginLoadException($"plugin.init вернул false: {pluginId} ({path})");
            }

            _extAudioPorts = GetPluginExtension(Clap.ExtAudioPorts);
            _extState = GetPluginExtension(Clap.ExtState);
            _extLatency = GetPluginExtension(Clap.ExtLatency);
            _extGui = GetPluginExtension(Clap.ExtGui);

            _channels = ValidateStereoPorts();

            _inputEventsSize = _ => 0;
            _inputEventsGet = (_, _) => IntPtr.Zero;
            _outputEventsTryPush = (_, _) => true;
            _streamWrite = StreamWrite;
            _streamRead = StreamRead;
        }
        catch
        {
            // Плагин создан, но init не прошёл — освобождаем его (destroy на смещении24).
            if (_plugin != IntPtr.Zero)
            {
                try
                {
                    var destroyPtr = Marshal.ReadIntPtr(_plugin, 24);
                    if (destroyPtr != IntPtr.Zero)
                    {
                        Marshal.GetDelegateForFunctionPointer<ClapDelegates.PluginDestroy>(destroyPtr)(_plugin);
                    }
                }
                catch
                {
                    // Ошибка деструктора не должна маскировать исходное исключение.
                }
            }

            FreeHost();
            throw;
        }
    }

    /// <summary>Создаёт экземпляр: модуль, фабрика, init, проверка стерео-портов.</summary>
    public static ClapPlugin Create(string path, string pluginId)
    {
        var module = ClapModule.Load(path);
        try
        {
            return new ClapPlugin(module, path, pluginId);
        }
        catch
        {
            module.Dispose();
            throw;
        }
    }

    public void Prepare(int sampleRate, int maxBlockFrames, int channels)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxBlockFrames, 1);

        if (channels != _channels)
        {
            throw new ArgumentException(
                $"Плагин объявляет {_channels} канал(ов), граф передаёт {channels}.", nameof(channels));
        }

        if (_active)
        {
            DeactivateInternal();
        }

        AllocateBuffers(maxBlockFrames);

        if (!_activate(_plugin, sampleRate, 1, (uint)maxBlockFrames))
        {
            throw new PluginLoadException($"plugin.activate вернул false при {sampleRate} Гц: {Name} ({_path})");
        }

        _active = true;
        _processing = false;
        _sampleRate = sampleRate;
        _maxFrames = maxBlockFrames;
        _steadyTime = 0;
    }

    public void Process(Span<float> interleaved, int frames)
    {
        if (!_active || frames <= 0)
        {
            interleaved.Clear();
            return;
        }

        if (frames > _maxFrames)
        {
            // Защита RT-потока: блок больше подготовленного — без исключений.
            frames = _maxFrames;
        }

        if (!_processing)
        {
            _audioThreadId = Environment.CurrentManagedThreadId;
            if (!_startProcessing(_plugin))
            {
                ProcessErrors++;
                interleaved.Clear();
                return;
            }

            _processing = true;
        }

        var channels = _channels;

        // Interleaved → planar (вход) + обнуление выхода.
        for (var frame = 0; frame < frames; frame++)
        {
            var sourceIndex = frame * channels;
            for (var channel = 0; channel < channels; channel++)
            {
                _inChannels[channel][frame] = interleaved[sourceIndex + channel];
                _outChannels[channel][frame] = 0f;
            }
        }

        var process = new ClapProcess
        {
            SteadyTime = _steadyTime,
            FramesCount = (uint)frames,
            Transport = IntPtr.Zero,
            AudioInputs = _inBuffersPtr,
            AudioOutputs = _outBuffersPtr,
            AudioInputsCount = 1,
            AudioOutputsCount = 1,
            InEvents = _inEventsPtr,
            OutEvents = _outEventsPtr,
        };

        var status = _process(_plugin, (IntPtr)(&process));
        if (status == Clap.ProcessError)
        {
            ProcessErrors++;
            interleaved.Clear();
            return;
        }

        // Planar → interleaved (выход).
        for (var frame = 0; frame < frames; frame++)
        {
            var targetIndex = frame * channels;
            for (var channel = 0; channel < channels; channel++)
            {
                interleaved[targetIndex + channel] = _outChannels[channel][frame];
            }
        }

        _steadyTime += frames;
    }

    public byte[]? GetState()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_extState == IntPtr.Zero)
        {
            return null;
        }

        var writer = new StateBuffer();
        var handle = GCHandle.Alloc(writer);
        var stream = new ClapOStream
        {
            Ctx = GCHandle.ToIntPtr(handle),
            Write = Marshal.GetFunctionPointerForDelegate(_streamWrite),
        };

        var streamPtr = Marshal.AllocHGlobal(Marshal.SizeOf<ClapOStream>());
        try
        {
            Marshal.StructureToPtr(stream, streamPtr, false);
            var save = Marshal.GetDelegateForFunctionPointer<ClapDelegates.StateSave>(
                Marshal.ReadIntPtr(_extState, 0));
            return save(_plugin, streamPtr) ? writer.ToArray() : null;
        }
        finally
        {
            Marshal.FreeHGlobal(streamPtr);
            handle.Free();
        }
    }

    public void SetState(byte[]? state)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_extState == IntPtr.Zero || state is null)
        {
            return;
        }

        var reader = new StateBuffer(state);
        var handle = GCHandle.Alloc(reader);
        var stream = new ClapIStream
        {
            Ctx = GCHandle.ToIntPtr(handle),
            Read = Marshal.GetFunctionPointerForDelegate(_streamRead),
        };

        var streamPtr = Marshal.AllocHGlobal(Marshal.SizeOf<ClapIStream>());
        try
        {
            Marshal.StructureToPtr(stream, streamPtr, false);

            var wasActive = _active;
            if (wasActive)
            {
                DeactivateInternal();
            }

            var load = Marshal.GetDelegateForFunctionPointer<ClapDelegates.StateLoad>(
                Marshal.ReadIntPtr(_extState, 8));
            if (!load(_plugin, streamPtr))
            {
                throw new PluginLoadException($"plugin.state.load вернул false: {Name} ({_path})");
            }

            if (wasActive)
            {
                ReactivateInternal();
            }
        }
        finally
        {
            Marshal.FreeHGlobal(streamPtr);
            handle.Free();
        }
    }

    // ===== IPluginEditor (clap.gui; main-thread контракт) =====

    public bool SupportsEditor => _extGui != IntPtr.Zero;

    public bool IsOpen => _editorOpen;

    private static T GuiFn<T>(IntPtr extension, int offset) =>
        Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(extension, offset));

    public bool Open(IntPtr hostWindow)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_editorOpen)
        {
            return true;
        }

        if (_extGui == IntPtr.Zero || hostWindow == IntPtr.Zero)
        {
            return false;
        }

        if (!GuiFn<ClapDelegates.GuiIsApiSupported>(_extGui, 0)(_plugin, Clap.WindowApiWin32, false))
        {
            return false;
        }

        if (!GuiFn<ClapDelegates.GuiCreate>(_extGui, 16)(_plugin, Clap.WindowApiWin32, false))
        {
            return false;
        }

        // Строка api живёт до Close: плагин может сравнивать указатель в т.ч. позже.
        _guiApiString = Marshal.StringToCoTaskMemUTF8(Clap.WindowApiWin32);
        var window = new ClapDelegates.ClapWindowStruct
        {
            Api = _guiApiString,
            Win32 = hostWindow,
        };

        if (!GuiFn<ClapDelegates.GuiSetParent>(_extGui, 80)(_plugin, ref window))
        {
            GuiFn<ClapDelegates.GuiDestroy>(_extGui, 24)(_plugin);
            Marshal.FreeCoTaskMem(_guiApiString);
            _guiApiString = IntPtr.Zero;
            return false;
        }

        GuiFn<ClapDelegates.GuiShow>(_extGui, 104)(_plugin);
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
        try
        {
            GuiFn<ClapDelegates.GuiDestroy>(_extGui, 24)(_plugin);
        }
        catch
        {
            // GUI-освобождение не должно ломать закрытие.
        }

        if (_guiApiString != IntPtr.Zero)
        {
            Marshal.FreeCoTaskMem(_guiApiString);
            _guiApiString = IntPtr.Zero;
        }
    }

    public (int Width, int Height) PreferredSize
    {
        get
        {
            if (!_editorOpen || _extGui == IntPtr.Zero)
            {
                return (0, 0);
            }

            GuiFn<ClapDelegates.GuiGetSize>(_extGui, 40)(_plugin, out var width, out var height);
            return ((int)width, (int)height);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            Close(); // GUI должен уйти до destroy плагина
            if (_processing)
            {
                _stopProcessing(_plugin);
                _processing = false;
            }

            if (_active)
            {
                _deactivate(_plugin);
                _active = false;
            }

            _destroy(_plugin);
        }
        catch
        {
            // Деструктор плагина не должен блокировать освобождение ресурсов.
        }

        FreeBuffers();
        FreeHost();
        _module.Dispose();
    }

    // ===== Внутреннее =====

    private IntPtr GetPluginExtension(string id)
    {
        var idUtf8 = ClapUtf8.Alloc(id);
        try
        {
            return _getExtension(_plugin, idUtf8);
        }
        finally
        {
            ClapUtf8.Free(idUtf8);
        }
    }

    private int ValidateStereoPorts()
    {
        if (_extAudioPorts == IntPtr.Zero)
        {
            throw new PluginLoadException($"Плагин не предоставляет clap.audio-ports: {Name} ({_path})");
        }

        var count = Marshal.GetDelegateForFunctionPointer<ClapDelegates.AudioPortsCount>(
            Marshal.ReadIntPtr(_extAudioPorts, 0));
        var get = Marshal.GetDelegateForFunctionPointer<ClapDelegates.AudioPortsGet>(
            Marshal.ReadIntPtr(_extAudioPorts, 8));

        if (count(_plugin, true) < 1 || count(_plugin, false) < 1)
        {
            throw new PluginLoadException($"У плагина нет входа или выхода: {Name} ({_path})");
        }

        var infoPtr = Marshal.AllocHGlobal(Marshal.SizeOf<ClapAudioPortInfo>());
        try
        {
            foreach (var isInput in new[] { true, false })
            {
                if (!get(_plugin, 0, isInput, infoPtr))
                {
                    throw new PluginLoadException($"Не удалось прочитать описание порта: {Name} ({_path})");
                }

                var info = Marshal.PtrToStructure<ClapAudioPortInfo>(infoPtr);
                if (info.ChannelCount != 2)
                {
                    throw new PluginLoadException(
                        $"Поддерживаются только стерео-плагины (порт входа/выхода: {info.ChannelCount} к): {Name} ({_path})");
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(infoPtr);
        }

        return 2;
    }

    private void AllocateBuffers(int maxBlockFrames)
    {
        FreeBuffers();

        _channels = 2;
        _inChannels = new float[_channels][];
        _outChannels = new float[_channels][];
        _channelHandles = new GCHandle[_channels];
        for (var channel = 0; channel < _channels; channel++)
        {
            _inChannels[channel] = new float[maxBlockFrames];
            _outChannels[channel] = new float[maxBlockFrames];
            _channelHandles[channel] = GCHandle.Alloc(_inChannels[channel], GCHandleType.Pinned);
        }

        // Ссылатели data32 для входа: указатели на наши закреплённые массивы.
        var inPointers = new IntPtr[_channels];
        var outHandles = new GCHandle[_channels];
        for (var channel = 0; channel < _channels; channel++)
        {
            inPointers[channel] = _channelHandles[channel].AddrOfPinnedObject();
            outHandles[channel] = GCHandle.Alloc(_outChannels[channel], GCHandleType.Pinned);
        }

        // out-дескрипторы каналов храним в _channelHandles-хвосте через отдельный массив:
        // перераспределяем _channelHandles как [in0, in1, out0, out1].
        var allHandles = new GCHandle[_channels * 2];
        for (var channel = 0; channel < _channels; channel++)
        {
            allHandles[channel] = _channelHandles[channel];
            allHandles[_channels + channel] = outHandles[channel];
        }

        _channelHandles = allHandles;

        var outPointers = new IntPtr[_channels];
        for (var channel = 0; channel < _channels; channel++)
        {
            outPointers[channel] = outHandles[channel].AddrOfPinnedObject();
        }

        _inPtrsHandle = GCHandle.Alloc(inPointers, GCHandleType.Pinned);
        _outPtrsHandle = GCHandle.Alloc(outPointers, GCHandleType.Pinned);

        var inBuffers = new[]
        {
            new ClapAudioBuffer
            {
                Data32 = _inPtrsHandle.AddrOfPinnedObject(),
                ChannelCount = (uint)_channels,
            },
        };
        var outBuffers = new[]
        {
            new ClapAudioBuffer
            {
                Data32 = _outPtrsHandle.AddrOfPinnedObject(),
                ChannelCount = (uint)_channels,
            },
        };

        _inBuffersHandle = GCHandle.Alloc(inBuffers, GCHandleType.Pinned);
        _outBuffersHandle = GCHandle.Alloc(outBuffers, GCHandleType.Pinned);
        _inBuffersPtr = _inBuffersHandle.AddrOfPinnedObject();
        _outBuffersPtr = _outBuffersHandle.AddrOfPinnedObject();

        var inEvents = new[]
        {
            new ClapInputEvents
            {
                Ctx = IntPtr.Zero,
                Size = Marshal.GetFunctionPointerForDelegate(_inputEventsSize),
                Get = Marshal.GetFunctionPointerForDelegate(_inputEventsGet),
            },
        };
        var outEvents = new[]
        {
            new ClapOutputEvents
            {
                Ctx = IntPtr.Zero,
                TryPush = Marshal.GetFunctionPointerForDelegate(_outputEventsTryPush),
            },
        };

        _inEventsHandle = GCHandle.Alloc(inEvents, GCHandleType.Pinned);
        _outEventsHandle = GCHandle.Alloc(outEvents, GCHandleType.Pinned);
        _inEventsPtr = _inEventsHandle.AddrOfPinnedObject();
        _outEventsPtr = _outEventsHandle.AddrOfPinnedObject();
    }

    private void FreeBuffers()
    {
        if (_inPtrsHandle.IsAllocated)
        {
            _inPtrsHandle.Free();
        }

        if (_outPtrsHandle.IsAllocated)
        {
            _outPtrsHandle.Free();
        }

        if (_inBuffersHandle.IsAllocated)
        {
            _inBuffersHandle.Free();
        }

        if (_outBuffersHandle.IsAllocated)
        {
            _outBuffersHandle.Free();
        }

        if (_inEventsHandle.IsAllocated)
        {
            _inEventsHandle.Free();
        }

        if (_outEventsHandle.IsAllocated)
        {
            _outEventsHandle.Free();
        }

        foreach (var handle in _channelHandles)
        {
            if (handle.IsAllocated)
            {
                handle.Free();
            }
        }

        _inChannels = [];
        _outChannels = [];
        _channelHandles = [];
    }

    private void DeactivateInternal()
    {
        if (_processing)
        {
            _stopProcessing(_plugin);
            _processing = false;
        }

        if (_active)
        {
            _deactivate(_plugin);
            _active = false;
        }
    }

    private void ReactivateInternal()
    {
        if (!_activate(_plugin, _sampleRate, 1, (uint)_maxFrames))
        {
            throw new PluginLoadException($"plugin.activate вернул false после reload state: {Name} ({_path})");
        }

        _active = true;
        _processing = false;
    }

    private void FreeHost()
    {
        if (_guiApiString != IntPtr.Zero)
        {
            Marshal.FreeCoTaskMem(_guiApiString);
            _guiApiString = IntPtr.Zero;
        }

        if (_threadCheckPtr != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_threadCheckPtr);
        }

        if (_hostLatencyPtr != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_hostLatencyPtr);
        }

        if (_hostPtr != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_hostPtr);
        }

        ClapUtf8.Free(_namePtr);
        ClapUtf8.Free(_vendorPtr);
        ClapUtf8.Free(_urlPtr);
        ClapUtf8.Free(_hostVersionPtr);

        if (_selfHandle.IsAllocated)
        {
            _selfHandle.Free();
        }
    }

    private IntPtr HostGetExtension(IntPtr host, IntPtr idUtf8)
    {
        var id = ClapUtf8.Read(idUtf8);
        if (id == Clap.ExtThreadCheck)
        {
            return _threadCheckPtr;
        }

        if (id == Clap.ExtHostLatency)
        {
            return _hostLatencyPtr;
        }

        return IntPtr.Zero;
    }

    private long StreamWrite(IntPtr stream, IntPtr buffer, ulong size)
    {
        if (size > int.MaxValue)
        {
            return -1;
        }

        var writer = (StateBuffer?)GCHandle.FromIntPtr(Marshal.ReadIntPtr(stream, 0)).Target;
        if (writer is null)
        {
            return -1;
        }

        return writer.Write(buffer, (int)size);
    }

    private long StreamRead(IntPtr stream, IntPtr buffer, ulong size)
    {
        if (size > int.MaxValue)
        {
            return -1;
        }

        var reader = (StateBuffer?)GCHandle.FromIntPtr(Marshal.ReadIntPtr(stream, 0)).Target;
        if (reader is null)
        {
            return -1;
        }

        return reader.Read(buffer, (int)size);
    }

    /// <summary>Кольцевой буфер для state-строк: ctx стрима указывает сюда.</summary>
    private sealed class StateBuffer
    {
        private readonly byte[]? _source;
        private readonly List<byte> _sink = [];
        private int _readPosition;

        public StateBuffer()
        {
        }

        public StateBuffer(byte[] source) => _source = source;

        public long Write(IntPtr buffer, int size)
        {
            var chunk = new byte[size];
            Marshal.Copy(buffer, chunk, 0, size);
            _sink.AddRange(chunk);
            return size;
        }

        public long Read(IntPtr buffer, int size)
        {
            if (_source is null)
            {
                return 0;
            }

            var available = Math.Min(size, _source.Length - _readPosition);
            if (available <= 0)
            {
                return 0;
            }

            Marshal.Copy(_source, _readPosition, buffer, available);
            _readPosition += available;
            return available;
        }

        public byte[] ToArray() => [.. _sink];
    }
}
