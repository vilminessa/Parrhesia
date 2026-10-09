using Parrhesia.Plugins.Bridge;

namespace Parrhesia.App;

/// <summary>
/// Режим-исполнитель плагина (S-волна): дочерний процесс крутит один
/// аудио-узел через мост <see cref="PluginBridge"/>. Крэш/зависание такого
/// процесса не касается приложения — узел просто умирает. Bench-бэкенд
/// (×2 со снапом) — замена реального VST3/CLAP-бэкенда (фаза S2).
/// </summary>
internal static class PluginHostMode
{
    /// <summary>
    /// Цикл: ждёт кик → вход → обработка → выход. Возвращает код выхода
    /// (0 — штатно; процесс убивает родитель).
    /// </summary>
    /// <param name="nodeIdHex">32-hex id узла (guid "N").</param>
    /// <param name="maxBlockFrames">Максимум кадров в блоке (как у хоста).</param>
    /// <param name="sleepMs">Искусственная задержка обработки (бенч «тяжёлого» плагина).</param>
    public static int Run(string nodeIdHex, int maxBlockFrames, int sleepMs)
    {
        if (!Guid.TryParseExact(nodeIdHex, "N", out var nodeId))
        {
            Console.Error.WriteLine("--plugin-host: некорректный id узла");
            return (2);
        }

        if (maxBlockFrames <= 0)
        {
            Console.Error.WriteLine("--plugin-host: некорректный размер блока");
            return (2);
        }

        using var child = new PluginBridge.ChildSide(nodeId, maxBlockFrames, TimeSpan.FromSeconds(5));

        var channels = PluginBridge.Channels;
        var input = new float[maxBlockFrames * channels];
        var output = new float[maxBlockFrames * channels];

        while (true)
        {
            // Дыхание50мс: молчание хоста не капканит процесс.
            if (!child.WaitForKick(TimeSpan.FromMilliseconds(50)))
            {
                continue;
            }

            if (!child.TryTakeInput(input))
            {
                continue;
            }

            if (sleepMs > 0)
            {
                Thread.Sleep(sleepMs); // имитация «тяжёлого» плагина (бенч)
            }

            // Bench-бэкенд: ×2 (S2 заменит на VST3/CLAP-загрузку узла).
            for (var i = 0; i < input.Length; i++)
            {
                output[i] = input[i] * 2f;
            }

            child.PublishOutput(output);
        }
    }
}
