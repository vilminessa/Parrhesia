using System.Runtime.InteropServices;
using Parrhesia.Core.Graph;

namespace Parrhesia.Plugins.Vst3;

/// <summary>
/// Фасад VST3: перечисление классов модуля (Audio Module Class) и загрузка
/// экземпляров через нативный шим. Идентификатор — CID в формате32 hex
/// (шим нормализует и дефисы — совместимо и с moduleinfo.json, и с toString).
/// </summary>
public static class Vst3Loader
{
    /// <summary>Перечисляет аудио-классы модуля. Ошибки → PluginLoadException.</summary>
    public static IReadOnlyList<PluginDescriptor> Enumerate(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var result = new List<PluginDescriptor>();

        // Колбэк вызывается симметрично внутри нативного вызова — строки
        // валидны только внутри, копируем сразу. Исключений через границу не бросаем.
        Vst3Native.EnumerateCallback callback = (_, classId, name, _) =>
        {
            var id = Marshal.PtrToStringUTF8(classId);
            if (string.IsNullOrEmpty(id))
            {
                return;
            }

            var displayName = Marshal.PtrToStringUTF8(name);
            result.Add(new PluginDescriptor(
                PluginFormat.Vst3,
                path,
                id,
                string.IsNullOrEmpty(displayName) ? id : displayName));
        };

        var count = Vst3Native.Enumerate(path, callback);
        if (count < 0)
        {
            throw new PluginLoadException($"VST3 enumerate ({path}): {Vst3Native.LastErrorMessage()}");
        }

        return result;
    }

    /// <summary>Загружает экземпляр по CID; Dispose освобождает модуль через шим.</summary>
    public static IAudioPlugin Load(string path, string pluginId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        return Vst3Plugin.Create(path, pluginId);
    }
}
