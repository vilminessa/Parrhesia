using Parrhesia.Core.Graph;
using Parrhesia.Plugins.Scanning;

namespace Parrhesia.Plugins.Tests.Scanning;

public class PluginScannerTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "parrhesia-scan-" + Guid.NewGuid().ToString("N"));

    private readonly string _cachePath;

    public PluginScannerTests()
    {
        Directory.CreateDirectory(_root);
        _cachePath = Path.Combine(_root, "cache.json");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Временный каталог — уборка не критична.
        }
    }

    [Fact]
    public void Scan_FindsClapPluginsInFolder()
    {
        var module = Path.Combine(_root, "test-plugin.clap");
        File.Copy(NativeTestPluginTests.TestPluginPath, module);

        var result = new PluginScanner(_cachePath).Scan([_root]);

        Assert.Equal(2, result.Plugins.Count);
        Assert.Contains(result.Plugins, p => p.PluginId == "com.parrhesia.test.gain");
        Assert.Contains(result.Plugins, p => p.PluginId == "com.parrhesia.test.latency");
        Assert.All(result.Plugins, p => Assert.Equal(PluginFormat.Clap, p.Format));
        Assert.Equal(1, result.Loaded);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void SecondScan_ServesFromCache()
    {
        var module = Path.Combine(_root, "test-plugin.clap");
        File.Copy(NativeTestPluginTests.TestPluginPath, module);
        var scanner = new PluginScanner(_cachePath);

        _ = scanner.Scan([_root]);
        var second = scanner.Scan([_root]);

        Assert.Equal(1, second.FromCache);
        Assert.Equal(0, second.Loaded);
        Assert.Equal(2, second.Plugins.Count);
    }

    [Fact]
    public void Cache_SurvivesNewScannerInstance()
    {
        var module = Path.Combine(_root, "test-plugin.clap");
        File.Copy(NativeTestPluginTests.TestPluginPath, module);

        _ = new PluginScanner(_cachePath).Scan([_root]);
        var second = new PluginScanner(_cachePath).Scan([_root]);

        Assert.Equal(1, second.FromCache);
        Assert.Equal(0, second.Loaded);
    }

    [Fact]
    public void ChangedFile_IsRescanned()
    {
        var module = Path.Combine(_root, "test-plugin.clap");
        File.Copy(NativeTestPluginTests.TestPluginPath, module);
        var scanner = new PluginScanner(_cachePath);
        _ = scanner.Scan([_root]);

        File.SetLastWriteTimeUtc(module, DateTime.UtcNow.AddHours(1));
        var second = scanner.Scan([_root]);

        Assert.Equal(1, second.Loaded);
        Assert.Equal(0, second.FromCache);
    }

    [Fact]
    public void ForceRescan_IgnoresCache()
    {
        var module = Path.Combine(_root, "test-plugin.clap");
        File.Copy(NativeTestPluginTests.TestPluginPath, module);
        var scanner = new PluginScanner(_cachePath);
        _ = scanner.Scan([_root]);

        var forced = scanner.Scan([_root], useCache: false);

        Assert.Equal(1, forced.Loaded);
        Assert.Equal(0, forced.FromCache);
    }

    [Fact]
    public void CorruptModule_ReportsError_AndErrorIsCached()
    {
        var broken = Path.Combine(_root, "broken.clap");
        File.WriteAllBytes(broken, [0x4D, 0x5A, 1, 2, 3, 4]); // не DLL
        var scanner = new PluginScanner(_cachePath);

        var first = scanner.Scan([_root]);
        var second = scanner.Scan([_root]);

        Assert.Single(first.Errors);
        Assert.Empty(first.Plugins);
        Assert.Equal(1, second.FromCache);
        Assert.Equal(0, second.Loaded); // битый файл не перечитываем
        Assert.Single(second.Errors);
    }

    [Fact]
    public void Vst3Bundle_ReadsIdentityWithoutLoading()
    {
        var bundle = Path.Combine(_root, "Fake.vst3");
        var module = Path.Combine(bundle, "Contents", "x86_64-win", "Fake.vst3");
        Directory.CreateDirectory(Path.GetDirectoryName(module)!);
        File.WriteAllBytes(module, []); // модуль пуст — его НЕ грузят
        Directory.CreateDirectory(Path.Combine(bundle, "Resources"));
        File.WriteAllText(
            Path.Combine(bundle, "Resources", "moduleinfo.json"),
            """
            {
              "Name": "Fake Synth",
              "Classes": [
                {
                  "CID": "1A2B3C4D-0000-0000-0000-000000000001",
                  "Category": "Audio Module Class",
                  "Name": "Fake Synth Instance"
                },
                {
                  "CID": "1A2B3C4D-0000-0000-0000-000000000002",
                  "Category": "Component Controller Class",
                  "Name": "Не аудио-класс"
                }
              ]
            }
            """);

        var result = new PluginScanner(_cachePath).Scan([_root]);

        var descriptor = Assert.Single(result.Plugins);
        Assert.Equal(PluginFormat.Vst3, descriptor.Format);
        Assert.Equal("Fake Synth Instance", descriptor.Name);
        Assert.Equal("1A2B3C4D-0000-0000-0000-000000000001", descriptor.PluginId);
        Assert.Equal(module, descriptor.Path);
    }

    [Fact]
    public void Vst3Bundle_WithoutModuleInfo_FallsBackToFileName()
    {
        var bundle = Path.Combine(_root, "Plain.vst3");
        var module = Path.Combine(bundle, "Contents", "x86_64-win", "Plain.vst3");
        Directory.CreateDirectory(Path.GetDirectoryName(module)!);
        File.WriteAllBytes(module, []);

        var result = new PluginScanner(_cachePath).Scan([_root]);

        var descriptor = Assert.Single(result.Plugins);
        Assert.Equal(PluginFormat.Vst3, descriptor.Format);
        Assert.Equal("Plain", descriptor.Name);
        Assert.Equal(module, descriptor.PluginId);
    }
}
