using Parrhesia.Core.Graph;
using Parrhesia.Plugins;
using Parrhesia.Plugins.Vst3;

namespace Parrhesia.Plugins.Tests;

/// <summary>
/// VST3-хост (шим + нативный тест-плагин) — зеркало CLAP-набора:
/// enumerate/создание/process ×2/state/latency=128/импульсная задержка.
/// </summary>
public class Vst3HostTests
{
    private const int SampleRate = 48000;
    private const int MaxBlock = 480;

    private static string Dll => Path.Combine(AppContext.BaseDirectory, "test-plugin-vst3.dll");

    [Fact]
    public void Vst3TestPluginDll_Exists()
    {
        Assert.True(
            File.Exists(Dll),
            "test-plugin-vst3.dll не найден — соберите tests/Parrhesia.Plugins.Native/vst3-test-plugin/build-vst3-test-plugin.bat");
    }

    [Fact]
    public void Enumerate_FindsBothAudioClasses()
    {
        Assert.True(File.Exists(Dll));

        var plugins = Vst3Loader.Enumerate(Dll);

        Assert.Equal(2, plugins.Count); // контроллер-классы отфильтрованы шимом
        Assert.Contains(plugins, p => p.Name == "Parrhesia Test Gain");
        Assert.Contains(plugins, p => p.Name == "Parrhesia Test Latency");
        Assert.All(plugins, p => Assert.Equal(PluginFormat.Vst3, p.Format));
        Assert.All(plugins, p => Assert.Equal(Dll, p.Path));
    }

    [Fact]
    public void Load_MissingModule_Throws()
    {
        Assert.Throws<PluginLoadException>(() =>
            Vst3Loader.Load(Path.Combine(AppContext.BaseDirectory, "нет-такого.vst3"), "507252014741494E434F4D5000000001"));
    }

    [Fact]
    public void Load_UnknownPluginId_Throws()
    {
        Assert.True(File.Exists(Dll));
        Assert.Throws<PluginLoadException>(() => Vst3Loader.Load(Dll, "не-uid"));
    }

    [Fact]
    public void Gain_DoublesSignal()
    {
        using var plugin = LoadByName("Parrhesia Test Gain");
        plugin.Prepare(SampleRate, MaxBlock, 2);

        const int frames = 4;
        var buffer = new float[frames * 2];
        new float[] { 0.25f, -0.5f, 0f, 1f, -1f, 0.75f, 0.125f, -0.25f }.AsSpan().CopyTo(buffer);

        plugin.Process(buffer, frames);

        Assert.Equal(0.5f, buffer[0]);
        Assert.Equal(-1f, buffer[1]);
        Assert.Equal(0f, buffer[2]);
        Assert.Equal(2f, buffer[3]);
        Assert.Equal(-2f, buffer[4]);
        Assert.Equal(1.5f, buffer[5]);
        Assert.Equal(0.25f, buffer[6]);
        Assert.Equal(-0.5f, buffer[7]);
    }

    [Fact]
    public void Gain_State_RoundTrip()
    {
        using var plugin = LoadByName("Parrhesia Test Gain");
        plugin.Prepare(SampleRate, MaxBlock, 2);

        var state = plugin.GetState();
        Assert.NotNull(state);

        // Схема шима: [u32 compLen][magic + normalized gain][u32 ctrlLen].
        Assert.True(state.Length >= 12, $"подозрительный размер state: {state.Length}");

        plugin.SetState(state);
        Assert.Equal(state, plugin.GetState());
    }

    [Fact]
    public void Latency_Reports128()
    {
        using var plugin = LoadByName("Parrhesia Test Latency");
        plugin.Prepare(SampleRate, MaxBlock, 2);

        Assert.Equal(128, plugin.LatencySamples);
    }

    [Fact]
    public void Latency_DelaysImpulseBy128Frames()
    {
        using var plugin = LoadByName("Parrhesia Test Latency");
        plugin.Prepare(SampleRate, MaxBlock, 2);

        var buffer = new float[MaxBlock * 2];
        buffer[0] = 1f;
        buffer[1] = 1f;

        plugin.Process(buffer, MaxBlock);

        Assert.Equal(0f, buffer[0]);
        Assert.Equal(0f, buffer[2]);    // кадр1 — тишина
        Assert.Equal(0f, buffer[254]);  // кадр127 — тишина
        Assert.Equal(1f, buffer[256]);  // кадр128 — импульс
        Assert.Equal(1f, buffer[257]);
        Assert.Equal(0f, buffer[258]);
    }

    [Fact]
    public void Process_BeforePrepare_YieldsSilence()
    {
        using var plugin = LoadByName("Parrhesia Test Gain");

        var buffer = new float[8];
        buffer.AsSpan().Fill(1f);
        plugin.Process(buffer, 4);

        Assert.All(buffer, value => Assert.Equal(0f, value));
    }

    private static IAudioPlugin LoadByName(string name)
    {
        var descriptor = Vst3Loader.Enumerate(Dll).Single(p => p.Name == name);
        return Vst3Loader.Load(descriptor.Path, descriptor.PluginId);
    }
}
