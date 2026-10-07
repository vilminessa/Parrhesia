using Parrhesia.Plugins.Vst3;

namespace Parrhesia.Plugins.Tests;

/// <summary>
/// IPluginParameters (VST3): нормированный диапазон, мгновенное чтение после
/// setParamNormalized, доставка в аудио-поток через inputParameterChanges.
/// </summary>
public class Vst3ParameterTests
{
    private const int SampleRate = 48000;
    private const int MaxBlock = 480;
    private const int GainParamId = 1;

    private static string Dll => Path.Combine(AppContext.BaseDirectory, "test-plugin-vst3.dll");

    private static IAudioPlugin LoadByName(string name)
    {
        var descriptor = Vst3Loader.Enumerate(Dll).Single(p => p.Name == name);
        return Vst3Loader.Load(descriptor.Path, descriptor.PluginId);
    }

    [Fact]
    public void Gain_ListsNormalizedParameter()
    {
        using var plugin = LoadByName("Parrhesia Test Gain");
        var parameters = Assert.IsAssignableFrom<IPluginParameters>(plugin);

        var gain = Assert.Single(parameters.GetParameters());
        Assert.Equal(GainParamId, gain.Id);
        Assert.Equal("Gain", gain.Name);
        Assert.Equal(0, gain.Min);
        Assert.Equal(1, gain.Max);
        Assert.Equal(0.5, gain.Default);
    }

    [Fact]
    public void Latency_HasNoParameters()
    {
        using var plugin = LoadByName("Parrhesia Test Latency");
        var parameters = Assert.IsAssignableFrom<IPluginParameters>(plugin);
        Assert.Empty(parameters.GetParameters());
    }

    [Fact]
    public void Set_ImmediateForRead()
    {
        using var plugin = LoadByName("Parrhesia Test Gain");
        var parameters = Assert.IsAssignableFrom<IPluginParameters>(plugin);

        Assert.Equal(0.5, parameters.GetParameterValue(GainParamId)); // default

        parameters.SetParameterValue(GainParamId, 0.25);
        Assert.Equal(0.25, parameters.GetParameterValue(GainParamId));
    }

    [Fact]
    public void Set_ClampsToUnitRange()
    {
        using var plugin = LoadByName("Parrhesia Test Gain");
        var parameters = Assert.IsAssignableFrom<IPluginParameters>(plugin);

        parameters.SetParameterValue(GainParamId, 5);
        Assert.Equal(1, parameters.GetParameterValue(GainParamId));

        parameters.SetParameterValue(GainParamId, -2);
        Assert.Equal(0, parameters.GetParameterValue(GainParamId));
    }

    [Fact]
    public void Set_ThenProcess_AppliesGain()
    {
        using var plugin = LoadByName("Parrhesia Test Gain");
        var parameters = Assert.IsAssignableFrom<IPluginParameters>(plugin);
        plugin.Prepare(SampleRate, MaxBlock, 2);

        // Нормировано0.375 → gain = ×1.5; доставка — в ближайший Process.
        parameters.SetParameterValue(GainParamId, 0.375);

        var buffer = new float[MaxBlock * 2];
        Array.Fill(buffer, 0.5f);
        plugin.Process(buffer, MaxBlock);

        Assert.Equal(0.75f, buffer[0]);
        Assert.Equal(0.375, parameters.GetParameterValue(GainParamId));
    }
}
