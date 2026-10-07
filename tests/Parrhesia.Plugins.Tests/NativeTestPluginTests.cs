using System.Runtime.InteropServices;

namespace Parrhesia.Plugins.Tests;

public class NativeTestPluginTests
{
    /// <summary>Путь к собранному тест-плагину рядом с тестовой сборкой.</summary>
    public static string TestPluginPath =>
        Path.Combine(AppContext.BaseDirectory, "test-plugin.dll");

    [Fact]
    public void TestPluginDll_Exists()
    {
        Assert.True(
            File.Exists(TestPluginPath),
            "test-plugin.dll не найден — соберите tests/Parrhesia.Plugins.Native/build-test-plugin.bat");
    }

    [Fact]
    public void TestPluginDll_ExportsClapEntry()
    {
        Assert.True(File.Exists(TestPluginPath), "test-plugin.dll не собран");

        var handle = NativeLibrary.Load(TestPluginPath);
        try
        {
            var entry = NativeLibrary.GetExport(handle, "clap_entry");
            Assert.NotEqual(IntPtr.Zero, entry);
        }
        finally
        {
            NativeLibrary.Free(handle);
        }
    }
}
