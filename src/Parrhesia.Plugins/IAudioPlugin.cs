using Parrhesia.Core.Graph;

namespace Parrhesia.Plugins;

/// <summary>
/// Экземпляр аудио-плагина (CLAP/VST3) в обобщённом виде хоста.
/// Реализации живут в этом проекте; граф и движок видят только интерфейс.
/// Контракт потоков: Prepare/SetState/GetState/Dispose — вне аудио-потока;
/// Process вызывается только из аудио-потока и не должен блокироваться/аллоцировать.
/// </summary>
public interface IAudioPlugin : IDisposable
{
    /// <summary>Отображаемое имя плагина.</summary>
    string Name { get; }

    /// <summary>Задержка, которую плагин вносит (в кадрах — для компенсации в графе).</summary>
    int LatencySamples { get; }

    /// <summary>
    /// Подготовить/переподготовить экземпляр под формат движка.
    /// Вызывается вне аудио-потока; после — Process может работать с blocks до maxBlockFrames.
    /// </summary>
    void Prepare(int sampleRate, int maxBlockFrames, int channels);

    /// <summary>
    /// Обработать блок interleaved in-place (frames × channels сэмплов).
    /// Аудио-поток; аллокации и блокировки запрещены.
    /// </summary>
    void Process(Span<float> interleaved, int frames);

    /// <summary>Снять состояние плагина (null — плагин не умеет state).</summary>
    byte[]? GetState();

    /// <summary>Восстановить состояние (null — сброс к дефолту).</summary>
    void SetState(byte[]? state);
}

/// <summary>Описание плагина, найденное сканером.</summary>
public sealed record PluginDescriptor(
    PluginFormat Format,
    string Path,
    string PluginId,
    string Name);

/// <summary>Ошибка загрузки/инициализации плагина (плагин битой, нет модуля и т.п.).</summary>
public class PluginLoadException : Exception // не запечатан: T1-подклассы классификации отказов
{
    public PluginLoadException(string message)
        : base(message)
    {
    }

    public PluginLoadException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// S T1: исполнитель НЕ ПОДГОТОВИЛСЯ за таймаут (модуль завис при загрузке —
/// класс Clear). Ретраи по такой ошибке идут терпеливо (раз в минуты),
/// в отличие от быстрых падений.
/// </summary>
public sealed class PluginSpawnTimeoutException : PluginLoadException
{
    public PluginSpawnTimeoutException(string message)
        : base(message)
    {
    }
}
