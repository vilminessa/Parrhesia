using System.Runtime.InteropServices;
using Parrhesia.Core.Graph;

namespace Parrhesia.Plugins.Clap;

/// <summary>
/// Загруженный CLAP-модуль: NativeLibrary + entry (init/deinit) + фабрика.
/// Один экземпляр = один файл .clap/.dll; жизненный цикл закрытый
/// (Load → перечисление/создание → Dispose освобождает библиотеку).
/// </summary>
internal sealed class ClapModule : IDisposable
{
    private readonly IntPtr _library;
    private readonly ClapDelegates.EntryDeinit _deinit;
    private readonly IntPtr _factory;
    private readonly ClapDelegates.FactoryCountFn _factoryCount;
    private readonly ClapDelegates.FactoryDescriptorFn _factoryDescriptor;
    private readonly ClapDelegates.FactoryCreateFn _factoryCreate;
    private bool _disposed;

    private ClapModule(
        IntPtr library,
        ClapDelegates.EntryDeinit deinit,
        IntPtr factory)
    {
        _library = library;
        _deinit = deinit;
        _factory = factory;
        _factoryCount = Marshal.GetDelegateForFunctionPointer<ClapDelegates.FactoryCountFn>(
            Marshal.ReadIntPtr(factory, 0));
        _factoryDescriptor = Marshal.GetDelegateForFunctionPointer<ClapDelegates.FactoryDescriptorFn>(
            Marshal.ReadIntPtr(factory, 8));
        _factoryCreate = Marshal.GetDelegateForFunctionPointer<ClapDelegates.FactoryCreateFn>(
            Marshal.ReadIntPtr(factory, 16));
    }

    public static ClapModule Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!NativeLibrary.TryLoad(path, out var library))
        {
            throw new PluginLoadException($"Не удалось загрузить модуль: {path} (ошибка {Marshal.GetLastWin32Error()})");
        }

        try
        {
            IntPtr entryPtr;
            try
            {
                entryPtr = NativeLibrary.GetExport(library, "clap_entry");
            }
            catch (Exception ex)
            {
                throw new PluginLoadException($"В модуле нет экспорта clap_entry: {path}", ex);
            }

            var entry = Marshal.PtrToStructure<ClapPluginEntry>(entryPtr);
            if (entry.Version.Major < 1)
            {
                throw new PluginLoadException(
                    $"Несовместимая версия CLAP {entry.Version.Major}.{entry.Version.Minor}.{entry.Version.Revision}: {path}");
            }

            var init = Marshal.GetDelegateForFunctionPointer<ClapDelegates.EntryInit>(entry.Init);
            var deinit = Marshal.GetDelegateForFunctionPointer<ClapDelegates.EntryDeinit>(entry.Deinit);
            var getFactory = Marshal.GetDelegateForFunctionPointer<ClapDelegates.GetFactoryFn>(entry.GetFactory);

            var pathUtf8 = ClapUtf8.Alloc(path);
            try
            {
                if (!init(pathUtf8))
                {
                    throw new PluginLoadException($"clap_entry.init вернул false: {path}");
                }
            }
            finally
            {
                ClapUtf8.Free(pathUtf8);
            }

            var factoryId = ClapUtf8.Alloc(Clap.PluginFactoryId);
            IntPtr factory;
            try
            {
                factory = getFactory(factoryId);
            }
            finally
            {
                ClapUtf8.Free(factoryId);
            }

            if (factory == IntPtr.Zero)
            {
                throw new PluginLoadException($"Модуль не предоставляет фабрику плагинов: {path}");
            }

            return new ClapModule(library, deinit, factory);
        }
        catch
        {
            NativeLibrary.Free(library);
            throw;
        }
    }

    /// <summary>Перечисляет дескрипторы плагинов модуля (без пути — его знает вызывающий).</summary>
    public IReadOnlyList<PluginDescriptor> Enumerate()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var result = new List<PluginDescriptor>();
        var count = _factoryCount(_factory);
        for (uint i = 0; i < count; i++)
        {
            var descriptorPtr = _factoryDescriptor(_factory, i);
            if (descriptorPtr == IntPtr.Zero)
            {
                continue;
            }

            var descriptor = Marshal.PtrToStructure<ClapPluginDescriptor>(descriptorPtr);
            var id = ClapUtf8.Read(descriptor.Id);
            var name = ClapUtf8.Read(descriptor.Name);
            if (string.IsNullOrEmpty(id))
            {
                continue;
            }

            result.Add(new PluginDescriptor(
                PluginFormat.Clap,
                string.Empty,
                id,
                string.IsNullOrEmpty(name) ? id : name));
        }

        return result;
    }

    /// <summary>Создаёт экземпляр плагина по id (без init — его делает ClapPlugin).</summary>
    public IntPtr CreatePlugin(IntPtr hostPtr, string pluginId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var idUtf8 = ClapUtf8.Alloc(pluginId);
        try
        {
            var plugin = _factoryCreate(_factory, hostPtr, idUtf8);
            if (plugin == IntPtr.Zero)
            {
                throw new PluginLoadException($"Плагин не найден в модуле: {pluginId}");
            }

            return plugin;
        }
        finally
        {
            ClapUtf8.Free(idUtf8);
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
            _deinit();
        }
        catch
        {
            // deinit плагина не должен ломать освобождение библиотеки.
        }

        NativeLibrary.Free(_library);
    }
}
