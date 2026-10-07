using System.Runtime.InteropServices;
using Parrhesia.Plugins;
using Parrhesia.Plugins.Clap;
using Parrhesia.Plugins.Vst3;

namespace Parrhesia.Plugins.Tests;

/// <summary>
/// Окно редактора (IPluginEditor): встраивание GUI обоих тест-плагинов
/// в контейнер хоста, PreferredSize, идемпотентность Close.
/// </summary>
public class EditorTests
{
    [DllImport("user32.dll")]
    private static extern IntPtr GetDesktopWindow();

    private static string ClapDll => Path.Combine(AppContext.BaseDirectory, "test-plugin.dll");

    private static string Vst3Dll => Path.Combine(AppContext.BaseDirectory, "test-plugin-vst3.dll");

    [Fact]
    public void ClapEditor_OpensGetSizeCloses()
    {
        Assert.True(File.Exists(ClapDll));
        using var plugin = ClapLoader.Load(ClapDll, "com.parrhesia.test.gain");

        var editor = Assert.IsAssignableFrom<IPluginEditor>(plugin);
        Assert.True(editor.SupportsEditor);
        Assert.False(editor.IsOpen);
        Assert.Equal((0, 0), editor.PreferredSize); // до Open — нет размера

        Assert.True(editor.Open(GetDesktopWindow()));
        Assert.True(editor.IsOpen);
        Assert.Equal((400, 180), editor.PreferredSize);

        editor.Close();
        Assert.False(editor.IsOpen);
        editor.Close(); // идемпотентно
    }

    [Fact]
    public void Vst3Editor_OpensGetSizeCloses()
    {
        Assert.True(File.Exists(Vst3Dll));
        var descriptor = Vst3Loader.Enumerate(Vst3Dll).Single(p => p.Name == "Parrhesia Test Gain");
        using var plugin = Vst3Loader.Load(descriptor.Path, descriptor.PluginId);

        var editor = Assert.IsAssignableFrom<IPluginEditor>(plugin);
        Assert.True(editor.SupportsEditor);
        Assert.False(editor.IsOpen);

        Assert.True(editor.Open(GetDesktopWindow()));
        Assert.True(editor.IsOpen);
        Assert.Equal((400, 180), editor.PreferredSize);

        editor.Close();
        Assert.False(editor.IsOpen);
        editor.Close();
    }

    [Fact]
    public void Open_WithZeroHandle_Refuses()
    {
        using var plugin = ClapLoader.Load(ClapDll, "com.parrhesia.test.gain");
        var editor = Assert.IsAssignableFrom<IPluginEditor>(plugin);

        Assert.False(editor.Open(IntPtr.Zero));
        Assert.False(editor.IsOpen);
    }

    [Fact]
    public void Editor_ClosedBeforeOpen_IsSafe()
    {
        using var plugin = ClapLoader.Load(ClapDll, "com.parrhesia.test.latency");
        var editor = Assert.IsAssignableFrom<IPluginEditor>(plugin);

        editor.Close(); // без Open — no-op
        Assert.False(editor.IsOpen);
        Assert.Equal((0, 0), editor.PreferredSize);
    }
}
