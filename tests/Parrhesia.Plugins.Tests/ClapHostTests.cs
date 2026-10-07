using Parrhesia.Core.Graph;
using Parrhesia.Plugins.Clap;

namespace Parrhesia.Plugins.Tests;

public class ClapHostTests
{
    private const string GainId = "com.parrhesia.test.gain";
    private const string LatencyId = "com.parrhesia.test.latency";
    private const int SampleRate = 48000;
    private const int MaxBlock = 480;

    private static string Dll => NativeTestPluginTests.TestPluginPath;

    [Fact]
    public void Enumerate_ReturnsBothTestPlugins()
    {
        Assert.True(File.Exists(Dll), "test-plugin.dll не собран");

        var plugins = ClapLoader.Enumerate(Dll);

        Assert.Equal(2, plugins.Count);
        var gain = Assert.Single(plugins, p => p.PluginId == GainId);
        Assert.Equal("Parrhesia Test Gain", gain.Name);
        Assert.Equal(PluginFormat.Clap, gain.Format);
        Assert.Equal(Dll, gain.Path);
        Assert.Contains(plugins, p => p.PluginId == LatencyId);
    }

    [Fact]
    public void Load_MissingModule_Throws()
    {
        Assert.Throws<PluginLoadException>(() =>
            ClapLoader.Load(Path.Combine(AppContext.BaseDirectory, "нет-такого.dll"), GainId));
    }

    [Fact]
    public void Load_UnknownPluginId_Throws()
    {
        Assert.True(File.Exists(Dll));
        Assert.Throws<PluginLoadException>(() => ClapLoader.Load(Dll, "com.parrhesia.nope"));
    }

    [Fact]
    public void Gain_DoublesSignal()
    {
        using var plugin = ClapLoader.Load(Dll, GainId);
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
        using var plugin = ClapLoader.Load(Dll, GainId);
        plugin.Prepare(SampleRate, MaxBlock, 2);

        byte[]? state = plugin.GetState();
        Assert.NotNull(state);
        Assert.Equal(4, state.Length);

        var expected = new byte[] { 9, 8, 7, 6 };
        plugin.SetState(expected);
        Assert.Equal(expected, plugin.GetState());

        plugin.SetState(null);
        Assert.NotNull(plugin.GetState());
    }

    [Fact]
    public void Latency_Reports128()
    {
        using var plugin = ClapLoader.Load(Dll, LatencyId);
        Assert.Equal(0, plugin.LatencySamples); // до activate латентность неактуальна

        plugin.Prepare(SampleRate, MaxBlock, 2);
        Assert.Equal(128, plugin.LatencySamples);
    }

    [Fact]
    public void Latency_DelaysImpulseBy128Frames()
    {
        using var plugin = ClapLoader.Load(Dll, LatencyId);
        plugin.Prepare(SampleRate, MaxBlock, 2);

        const int frames = MaxBlock;
        var buffer = new float[frames * 2];
        buffer[0] = 1f; // импульс в кадре0 (L и R)
        buffer[1] = 1f;

        plugin.Process(buffer, frames);

        Assert.Equal(0f, buffer[0]);
        Assert.Equal(0f, buffer[2]); // кадр1 — тишина
        Assert.Equal(0f, buffer[254]); // кадр127 — тишина (254 =127*2)
        Assert.Equal(1f, buffer[256]); // кадр128 — импульс
        Assert.Equal(1f, buffer[257]);
        Assert.Equal(0f, buffer[258]); // кадр129 — тишина снова
    }

    [Fact]
    public void Process_BeforePrepare_YieldsSilence()
    {
        using var plugin = ClapLoader.Load(Dll, GainId);

        var buffer = new float[8];
        buffer.AsSpan().Fill(1f);
        plugin.Process(buffer, 4);

        Assert.All(buffer, value => Assert.Equal(0f, value));
    }
}
