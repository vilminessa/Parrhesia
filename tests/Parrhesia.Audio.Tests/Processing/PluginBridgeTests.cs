using System.Diagnostics;
using System.IO;
using Parrhesia.Plugins.Bridge;
using Xunit.Abstractions;

namespace Parrhesia.Audio.Tests.Processing;

/// <summary>
/// S0 (S-волна): аудио-мост «хост ↔ плагин-процесс» через shared memory.
/// Хост никогда не блокируется (RT-контракт); ребёнок — App.exe --plugin-host
/// с bench-бэкендом ×2. Пропускается, если App.exe не найден (вне репозитория).
/// </summary>
public class PluginBridgeTests
{
    private const int BlockFrames = 480; //10мс @48кГц

    private readonly ITestOutputHelper _output;

    public PluginBridgeTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void HostChild_RoundTrip_AppliesGain_NeverBlocks()
    {
        var exe = FindAppExe();
        if (exe is null)
        {
            _output.WriteLine("Parrhesia.App.exe не найден — проба моста пропущена.");
            return;
        }

        var nodeId = Guid.NewGuid();
        using var host = new PluginBridge.HostSide(nodeId, BlockFrames);
        var child = SpawnChild(exe, nodeId, sleepMs: 0);
        try
        {
            var block = new float[BlockFrames * PluginBridge.Channels];
            var output = new float[BlockFrames * PluginBridge.Channels];

            // Прогрев: ждём первый реально обработанный ребёнком блок (старт процесса).
            for (var sample = 0; sample < block.Length; sample++)
            {
                block[sample] = 0.5f;
            }

            var warmupDeadline = Environment.TickCount64 + 5000;
            while (host.ProcessedBlocks < 1 && Environment.TickCount64 < warmupDeadline)
            {
                host.PublishInput(block);
                host.TryReadOutput(output);
                Thread.Sleep(5);
            }

            Assert.True(host.ProcessedBlocks >= 1, "ребёнок не обработал ни одного блока за5 с");

            // Модель mailbox: выход — «последний обработанный»; между push и read
            // дожидаемся роста счётчика (свежий блок), замер скорость НЕ блокируясь.
            var stopwatch = Stopwatch.StartNew();
            const int iterations = 50;
            for (var i = 0; i < iterations; i++)
            {
                for (var sample = 0; sample < block.Length; sample++)
                {
                    block[sample] = 0.5f;
                }

                var processedBefore = host.ProcessedBlocks;
                host.PublishInput(block);

                var deadline = Environment.TickCount64 + 2000;
                while (host.ProcessedBlocks == processedBefore && Environment.TickCount64 < deadline)
                {
                    Thread.Sleep(1);
                }

                Assert.True(host.TryReadOutput(output));
                Assert.Equal(1.0f, output[0], 3); // ×2 бэкенд
            }

            stopwatch.Stop();

            var perBlock = stopwatch.Elapsed.TotalMilliseconds / iterations;
            _output.WriteLine(
                $"push+read: {perBlock:0.00} мс/блок; обработано ребёнком: {host.ProcessedBlocks}");
            Assert.True(host.ChildAlive(TimeSpan.FromSeconds(2)));
            Assert.True(perBlock < 50, $"хост ждал плагин: {perBlock:0.00} мс/блок");
        }
        finally
        {
            KillQuietly(child);
        }
    }

    [Fact]
    public void SlowChild_HostKeepsPumping_AndDeathIsVisible()
    {
        var exe = FindAppExe();
        if (exe is null)
        {
            _output.WriteLine("Parrhesia.App.exe не найден — проба моста пропущена.");
            return;
        }

        var nodeId = Guid.NewGuid();
        using var host = new PluginBridge.HostSide(nodeId, BlockFrames);
        var child = SpawnChild(exe, nodeId, sleepMs: 40); // «тяжёлый» плагин

        var block = new float[BlockFrames * PluginBridge.Channels];

        // Ребёнок в40мс на блок (+ его старт) — хост обязан прокачивать без ожиданий.
        var startupDeadline = Environment.TickCount64 + 8000;
        while (host.ProcessedBlocks < 1 && Environment.TickCount64 < startupDeadline)
        {
            host.PublishInput(block);
            Thread.Sleep(5);
        }

        Assert.True(host.ProcessedBlocks >= 1, "медленный ребёнок не стартовал за8 с");

        var stopwatch = Stopwatch.StartNew();
        for (var i = 0; i <100; i++)
        {
            host.PublishInput(block);
        }

        stopwatch.Stop();
        _output.WriteLine($"100 публикаций при медленном ребёнке: {stopwatch.ElapsedMilliseconds} мс");
        Assert.True(stopwatch.ElapsedMilliseconds < 500, "хост блокировался на медленном плагине");

        Assert.True(host.ChildAlive(TimeSpan.FromSeconds(2)));

        // Смерть ребёнка видна хосту по heartbeat.
        var before = host.ProcessedBlocks;
        try
        {
            child.Kill();
            Thread.Sleep(1500);
            _output.WriteLine(
                $"kill: hasExited={child.HasExited}; блоков до={before} после={host.ProcessedBlocks}; " +
                $"alive={host.ChildAlive(TimeSpan.FromMilliseconds(0))}");
            Assert.True(child.HasExited, "ребёнок пережил Kill()");
            Assert.False(host.ChildAlive(TimeSpan.FromSeconds(1)), "heartbeat продолжил идти после смерти");
        }
        finally
        {
            KillQuietly(child);
        }
    }

    private static void KillQuietly(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill();
            }
        }
        catch
        {
            // Уже завершился — некуда ждать.
        }
    }

    private static Process SpawnChild(string exe, Guid nodeId, int sleepMs)
    {
        var process = Process.Start(new ProcessStartInfo(
                exe,
                $"--plugin-host {nodeId:N} {BlockFrames} {sleepMs}")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            });
        return process ?? throw new InvalidOperationException("не удалось запустить plugin-host");
    }

    /// <summary>App.exe: env-переопределение → поиск вверх по дереву до src/…bin.</summary>
    private static string? FindAppExe()
    {
        var fromEnv = Environment.GetEnvironmentVariable("PARRHESIA_APP_EXE");
        if (!string.IsNullOrEmpty(fromEnv) && File.Exists(fromEnv))
        {
            return fromEnv;
        }

        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(
                dir.FullName, "src", "Parrhesia.App", "bin", "Debug", "net10.0-windows", "Parrhesia.App.exe");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
