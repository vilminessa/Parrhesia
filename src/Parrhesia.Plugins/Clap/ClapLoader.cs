using Parrhesia.Core.Graph;

namespace Parrhesia.Plugins.Clap;

/// <summary>
/// Точка входа для работы с CLAP-плагинами: перечисление и загрузка.
/// Ошибки любого уровня (нет файла, нет экспорта, несовместимая версия,
/// нет плагина, init=false, не стерео) — <see cref="PluginLoadException"/>.
/// </summary>
public static class ClapLoader
{
    /// <summary>Перечисляет плагины модуля (путь прописывается в каждом дескрипторе).</summary>
    public static IReadOnlyList<PluginDescriptor> Enumerate(string path)
    {
        using var module = ClapModule.Load(path);
        return module.Enumerate()
            .Select(descriptor => descriptor with { Path = path })
            .ToList();
    }

    /// <summary>
    /// Загружает экземпляр плагина. Владение модулем переходит экземпляру:
    /// Dispose плагина освобождает и библиотеку.
    /// </summary>
    public static IAudioPlugin Load(string path, string pluginId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        return ClapPlugin.Create(path, pluginId);
    }
}
