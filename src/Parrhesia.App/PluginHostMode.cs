using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Parrhesia.Core.Graph;
using Parrhesia.Plugins;
using Parrhesia.Plugins.Bridge;
using Parrhesia.Plugins.Clap;
using Parrhesia.Plugins.Vst3;

namespace Parrhesia.App;

/// <summary>
/// Режим-исполнитель плагина (S-волна): дочерний процесс крутит один
/// аудио-узел через мост <see cref="PluginBridge"/>. Крэш/зависание такого
/// процесса не касается приложения — узел просто умирает.
/// Два режима: bench (аргумент-число — имитация ×2, для тестов моста) и
/// spec (путь к JSON — реальный VST3/CLAP, единый механизм для всех плагинов).
/// Управляющий канал (<see cref="NodeControlServer"/>) поднимается в обоих:
/// state/params-запросы хоста идут без остановки аудио-цикла.
/// </summary>
internal static class PluginHostMode
{
    /// <summary>
    /// Цикл: ждёт кик → вход → обработка → выход. Возвращает код выхода
    /// (0 — штатно; не 0 — хост увидит смерть по exit+heartbeat).
    /// </summary>
    /// <param name="nodeIdHex">32-hex id узла (guid "N").</param>
    /// <param name="maxBlockFrames">Максимум кадров в блоке (как у хоста).</param>
    /// <param name="modeArg">Число — bench-задержка мс; путь — spec-файл узла.</param>
    public static int Run(string nodeIdHex, int maxBlockFrames, string modeArg)
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

        // Доступ к плагину сериализуется: control-поток (state) ↔ аудио-цикл (process) —
        // VST3/CLAP-контракт запрещает getState параллельно с process.
        var gate = new object();
        IAudioPlugin? plugin = null;
        var sampleRate = 48000; // bench-дефолт; spec задаёт свою скорость
        try
        {
            if (int.TryParse(modeArg, out var sleepMs))
            {
                plugin = new BenchPlugin(sleepMs); // ×2 (тесты моста)
            }
            else
            {
                (plugin, sampleRate) = LoadSpec(modeArg);
            }

            plugin.Prepare(sampleRate, maxBlockFrames, PluginBridge.Channels);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"--plugin-host: загрузка не удалась: {ex.Message}");
            return (3);
        }

        using var control = new NodeControlServer(NodeControl.PipeName(nodeId), request =>
        {
            lock (gate)
            {
                return HandleControl(plugin!, request);
            }
        });
        control.Start();

        child.MarkReady();

        var channels = PluginBridge.Channels;
        var input = new float[maxBlockFrames * channels];
        var output = new float[maxBlockFrames * channels];

        while (true)
        {
            // Дыхание50мс: молчание хоста не капканит процесс; heartbeat —
            // «жив» независимо от того, гонит ли хост блоки.
            if (!child.WaitForKick(TimeSpan.FromMilliseconds(50)))
            {
                child.Heartbeat();
                continue;
            }

            if (!child.TryTakeInput(input))
            {
                continue;
            }

            lock (gate)
            {
                try
                {
                    plugin.Process(input, maxBlockFrames);
                    Array.Copy(input, output, input.Length);
                }
                catch (Exception)
                {
                    // Отказ обработки блока: публикуем тишину — узел молчит, но жив.
                    Array.Clear(output);
                }
            }

            child.PublishOutput(output);
        }
    }

    private static (IAudioPlugin Plugin, int SampleRate) LoadSpec(string specPath)
    {
        var spec = JsonSerializer.Deserialize<PluginNodeSpec>(
                File.ReadAllText(specPath), NodeControl.JsonOptions)
            ?? throw new PluginLoadException($"пустой spec-файл ({specPath})");

        File.Delete(specPath); // прочитан — не мусорим во временном каталоге

        IAudioPlugin plugin = spec.Format switch
        {
            nameof(PluginFormat.Vst3) => LoadVst3(spec),
            nameof(PluginFormat.Clap) => ClapLoader.Load(spec.Path, spec.PluginId),
            _ => throw new PluginLoadException($"неизвестный формат: {spec.Format}"),
        };

        return (plugin, spec.SampleRate);
    }

    /// <summary>
    /// VST3 в ребёнке: мусорный pluginId (в профиле сохранялся путь)
    /// заменяется CID из перечисления — как у легаси-фабрики шин, но БЕЗ
    /// песочницы: изоляция здесь — сам процесс.
    /// </summary>
    private static IAudioPlugin LoadVst3(PluginNodeSpec spec)
    {
        var classId = LooksLikeClassId(spec.PluginId)
            ? spec.PluginId
            : Vst3Loader.Enumerate(spec.Path).FirstOrDefault()?.PluginId
              ?? throw new PluginLoadException($"VST3: в модуле нет аудио-классов ({spec.Path})");

        return Vst3Loader.Load(spec.Path, classId);
    }

    private static bool LooksLikeClassId(string value)
    {
        if (value.Length != 32)
        {
            return false;
        }

        foreach (var character in value)
        {
            if (!Uri.IsHexDigit(character))
            {
                return false;
            }
        }

        return true;
    }

    private static NodeControlResponse HandleControl(IAudioPlugin plugin, NodeControlRequest request) => request.Op switch
    {
        "ping" => new NodeControlResponse { Ok = true },
        "getState" => new NodeControlResponse { Ok = true, Data = Encode(plugin.GetState()) },
        "setState" => ApplyState(plugin, request.Data),
        "latency" => new NodeControlResponse { Ok = true, Value = plugin.LatencySamples },
        _ => new NodeControlResponse { Ok = false, Error = $"неизвестная операция: {request.Op}" },
    };

    private static NodeControlResponse ApplyState(IAudioPlugin plugin, string? data)
    {
        try
        {
            plugin.SetState(data is null ? null : Convert.FromBase64String(data));
            return new NodeControlResponse { Ok = true };
        }
        catch (Exception ex)
        {
            return new NodeControlResponse { Ok = false, Error = ex.Message };
        }
    }

    private static string? Encode(byte[]? state) => state is null ? null : Convert.ToBase64String(state);

    /// <summary>Bench-бэкенд ×2: детерминированная обработка для тестов моста.</summary>
    private sealed class BenchPlugin(int sleepMs) : IAudioPlugin
    {
        public string Name => "bench";

        public int LatencySamples => 0;

        public void Prepare(int sampleRate, int maxBlockFrames, int channels)
        {
        }

        public void Process(Span<float> interleaved, int frames)
        {
            if (sleepMs > 0)
            {
                Thread.Sleep(sleepMs); // имитация «тяжёлого» плагина (бенч)
            }

            var samples = Math.Min(interleaved.Length, frames * PluginBridge.Channels);
            for (var i = 0; i < samples; i++)
            {
                interleaved[i] *= 2f;
            }
        }

        public byte[]? GetState() => [1, 2, 3];

        public void SetState(byte[]? state)
        {
        }

        public void Dispose()
        {
        }
    }
}
