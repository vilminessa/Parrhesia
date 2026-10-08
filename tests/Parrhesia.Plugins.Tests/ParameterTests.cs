using Parrhesia.Plugins.Clap;

namespace Parrhesia.Plugins.Tests;

/// <summary>
/// IPluginParameters (CLAP): описание параметров, установка (flush вне process,
/// очередь в process), кламп, форматирование, персистентность через state.
/// </summary>
public class ParameterTests
{
    private const string GainId = "com.parrhesia.test.gain";
    private const string LatencyId = "com.parrhesia.test.latency";
    private const int SampleRate = 48000;
    private const int MaxBlock = 480;
    private const int GainParamId = 1;

    private static string Dll => NativeTestPluginTests.TestPluginPath;

    [Fact]
    public void Gain_ListsParameterWithRange()
    {
        using var plugin = ClapLoader.Load(Dll, GainId);
        var parameters = Assert.IsAssignableFrom<IPluginParameters>(plugin);

        var gain = Assert.Single(parameters.GetParameters());
        Assert.Equal(GainParamId, gain.Id);
        Assert.Equal("Gain", gain.Name);
        Assert.Equal(0, gain.Min);
        Assert.Equal(4, gain.Max);
        Assert.Equal(2, gain.Default);
        Assert.False(gain.IsReadOnly);
        Assert.False(gain.IsHidden);
    }

    [Fact]
    public void Latency_HasNoParameters()
    {
        using var plugin = ClapLoader.Load(Dll, LatencyId);
        var parameters = Assert.IsAssignableFrom<IPluginParameters>(plugin);
        Assert.Empty(parameters.GetParameters());
    }

    [Fact]
    public void Set_WhileInactive_AppliesViaFlush()
    {
        using var plugin = ClapLoader.Load(Dll, GainId);
        var parameters = Assert.IsAssignableFrom<IPluginParameters>(plugin);

        Assert.Equal(2, parameters.GetParameterValue(GainParamId)); // default

        parameters.SetParameterValue(GainParamId, 3.5);
        Assert.Equal(3.5, parameters.GetParameterValue(GainParamId));
    }

    [Fact]
    public void Set_ClampsToRange()
    {
        using var plugin = ClapLoader.Load(Dll, GainId);
        var parameters = Assert.IsAssignableFrom<IPluginParameters>(plugin);

        parameters.SetParameterValue(GainParamId, 99);
        Assert.Equal(4, parameters.GetParameterValue(GainParamId));

        parameters.SetParameterValue(GainParamId, -1);
        Assert.Equal(0, parameters.GetParameterValue(GainParamId));
    }

    [Fact]
    public void Set_WhileActive_DeliveredOnNextProcess()
    {
        using var plugin = ClapLoader.Load(Dll, GainId);
        var parameters = Assert.IsAssignableFrom<IPluginParameters>(plugin);
        plugin.Prepare(SampleRate, MaxBlock, 2);

        parameters.SetParameterValue(GainParamId, 1.5);

        // Активен → событие лежит в очереди до ближайшего Process.
        Assert.Equal(2, parameters.GetParameterValue(GainParamId));

        var buffer = new float[MaxBlock * 2];
        Array.Fill(buffer, 0.5f);
        plugin.Process(buffer, MaxBlock);

        Assert.Equal(1.5, parameters.GetParameterValue(GainParamId));
        Assert.Equal(0.75f, buffer[0]); // 0.5 × новый gain1.5
    }

    [Fact]
    public void Format_UsesPluginValueToText()
    {
        using var plugin = ClapLoader.Load(Dll, GainId);
        var parameters = Assert.IsAssignableFrom<IPluginParameters>(plugin);
        Assert.Equal("3.50x", parameters.FormatParameterValue(GainParamId, 3.5));
    }

    [Fact]
    public void State_RoundTrip_KeepsParameterValue()
    {
        using var source = ClapLoader.Load(Dll, GainId);
        var sourceParams = Assert.IsAssignableFrom<IPluginParameters>(source);
        sourceParams.SetParameterValue(GainParamId, 3.25);

        var state = source.GetState();
        Assert.NotNull(state);

        using var target = ClapLoader.Load(Dll, GainId);
        target.SetState(state);
        var targetParams = Assert.IsAssignableFrom<IPluginParameters>(target);
        Assert.Equal(3.25, targetParams.GetParameterValue(GainParamId));
    }
}
