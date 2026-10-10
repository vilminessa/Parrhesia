using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using Parrhesia.App.Views;
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
/// Два режима: bench (<see cref="RunBench"/> — имитация ×2, тесты моста,
/// процесс живёт синхронно) и spec (<see cref="RunExecutor"/> — реальный
/// VST3/CLAP; аудио-цикл в фоне, WPF-цикл остаётся для окон редактора —
/// S4b: GUI плагина живёт В ЭТОМ процессе).
/// Управляющий канал (<see cref="NodeControlServer"/>) поднимается в обоих:
/// state/params/openEditor идут без остановки аудио-цикла.
/// </summary>
internal static class PluginHostMode
{
    /// <summary>
    /// Bench-режим: синхронный цикл «кик → вход → ×2 → выход» (не возвращается;
    /// завершает процесс убийство родителем). Код0 — штатно (теоретически).
    /// </summary>
    public static int RunBench(string nodeIdHex, int maxBlockFrames, int sleepMs)
    {
        if (!TryParseNode(nodeIdHex, maxBlockFrames, out var nodeId))
        {
            return (2);
        }

        using var child = new PluginBridge.ChildSide(nodeId, maxBlockFrames, TimeSpan.FromSeconds(5));
        var gate = new object();
        var plugin = new BenchPlugin(sleepMs);

        StartControl(nodeId, plugin, gate);
        child.MarkReady();
        return ExecuteLoop(child, plugin, gate, maxBlockFrames);
    }

    /// <summary>
    /// Спец-режим (spec-файл): загрузка реального VST3/CLAP, ready-флаг,
    /// control-канал — в фоновой задаче; возвращает сразу. Приложение живёт
    /// дальше (без главного окна), чтобы WPF-цикл показывал окна редакторов.
    /// Фатальная ошибка до ready — код3 (хост увидит смерть).
    /// </summary>
    public static void RunExecutor(string nodeIdHex, int maxBlockFrames, string specPath)
    {
        if (!Guid.TryParseExact(nodeIdHex, "N", out var nodeId) || maxBlockFrames <= 0)
        {
            Console.Error.WriteLine("--plugin-host: некорректные аргументы");
            Environment.Exit(2);
            return;
        }

        _ = Task.Run(() =>
        {
            try
            {
                using var child = new PluginBridge.ChildSide(
                    nodeId, maxBlockFrames, TimeSpan.FromSeconds(5));
                var gate = new object();

                var (plugin, sampleRate) = LoadSpec(specPath);
                plugin.Prepare(sampleRate, maxBlockFrames, PluginBridge.Channels);

                StartControl(nodeId, plugin, gate);
                child.MarkReady();
                ExecuteLoop(child, plugin, gate, maxBlockFrames);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"--plugin-host: {ex.Message}");
                Environment.Exit(3);
            }
        });
    }

    private static bool TryParseNode(string nodeIdHex, int maxBlockFrames, out Guid nodeId)
    {
        nodeId = default;
        if (!Guid.TryParseExact(nodeIdHex, "N", out nodeId))
        {
            Console.Error.WriteLine("--plugin-host: некорректный id узла");
            return false;
        }

        if (maxBlockFrames <= 0)
        {
            Console.Error.WriteLine("--plugin-host: некорректный размер блока");
            return false;
        }

        return true;
    }

    /// <summary>Цикл: ждёт кик → вход → обработка (под gate) → выход. Не возвращается.</summary>
    private static int ExecuteLoop(
        PluginBridge.ChildSide child,
        IAudioPlugin plugin,
        object gate,
        int maxBlockFrames)
    {
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

    /// <summary>Поднимает управляющий канал (state/params/openEditor) и стартует сервер.</summary>
    private static void StartControl(Guid nodeId, IAudioPlugin plugin, object gate)
    {
        var server = new NodeControlServer(
            NodeControl.PipeName(nodeId),
            request => HandleControl(plugin, gate, request));
        server.Start();
    }

    private static NodeControlResponse HandleControl(
        IAudioPlugin plugin,
        object gate,
        NodeControlRequest request)
    {
        // openEditor — отдельно: показ окна НЕ под gate хост-хендлера
        // (иначе автосейв хоста ждал бы, пока юзер закроет редактор).
        if (request.Op == "openEditor")
        {
            return OpenEditor(plugin, gate);
        }

        lock (gate)
        {
            return request.Op switch
            {
                "ping" => new NodeControlResponse { Ok = true },
                "getState" => new NodeControlResponse { Ok = true, Data = Encode(plugin.GetState()) },
                "setState" => ApplyState(plugin, request.Data),
                "latency" => new NodeControlResponse { Ok = true, Value = plugin.LatencySamples },
                _ => new NodeControlResponse { Ok = false, Error = $"неизвестная операция: {request.Op}" },
            };
        }
    }

    /// <summary>
    /// Показывает редактор плагина окном В ЭТОМ процессе (S4b). Ответ сразу —
    /// окно асинхронно, иначе хост-запросы (автосейв) ждали бы закрытия окна.
    /// Open/Close плагина — под gate: не параллельно с process.
    /// </summary>
    private static NodeControlResponse OpenEditor(IAudioPlugin plugin, object gate)
    {
        if (plugin is not IPluginEditor { SupportsEditor: true })
        {
            return new NodeControlResponse { Ok = false, Error = "у плагина нет редактора" };
        }

        var editor = (IPluginEditor)plugin;
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            return new NodeControlResponse { Ok = false, Error = "нет UI-потока процесса" };
        }

        dispatcher.BeginInvoke(() =>
        {
            try
            {
                new PluginEditorWindow(editor, plugin.Name, gate).Show();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"--plugin-host: редактор не открылся: {ex.Message}");
            }
        });

        return new NodeControlResponse { Ok = true };
    }

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
        var classId = Vst3Loader.LooksLikeClassId(spec.PluginId)
            ? spec.PluginId
            : Vst3Loader.Enumerate(spec.Path).FirstOrDefault()?.PluginId
              ?? throw new PluginLoadException($"VST3: в модуле нет аудио-классов ({spec.Path})");

        return Vst3Loader.Load(spec.Path, classId);
    }

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
