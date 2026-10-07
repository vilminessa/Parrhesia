using Parrhesia.Audio.Engine;

namespace Parrhesia.Audio.Tests.Engine;

/// <summary>Правила формат движка: база (авто) и явная настройка (Ф6-B3).</summary>
public class EngineFormatTests
{
    [Fact]
    public void Constants_MatchDriverFeedAndGraphProcessor()
    {
        Assert.Equal(48000, EngineFormat.DefaultRate);
        Assert.Equal(DriverFeed.Rate, EngineFormat.DefaultRate);
        Assert.Equal(2, EngineFormat.Channels);
    }

    [Fact]
    public void Resolve_ConfiguredRate_WinsOverEverything()
    {
        // Пользователь принудительно выбрал 44.1k — даже с виртуальным сником.
        Assert.Equal(44100, EngineFormat.Resolve(44100, hasVirtualSink: true, firstRealMixRate: 96000));
    }

    [Fact]
    public void Resolve_InvalidConfigured_FallsBackToAuto()
    {
        Assert.Equal(48000, EngineFormat.Resolve(12345, hasVirtualSink: true, firstRealMixRate: null));
    }

    [Fact]
    public void Resolve_Auto_WithVirtualSink_IsFeedCanonical48k()
    {
        Assert.Equal(48000, EngineFormat.Resolve(null, hasVirtualSink: true, firstRealMixRate: 44100));
    }

    [Fact]
    public void Resolve_Auto_RealSinksOnly_TakesFirstMixRate()
    {
        Assert.Equal(44100, EngineFormat.Resolve(null, hasVirtualSink: false, firstRealMixRate: 44100));
    }

    [Fact]
    public void Resolve_NoSinksYet_DefaultsTo48k()
    {
        // Первый запуск/чистый профиль: синков нет — стартовый формат.
        Assert.Equal(48000, EngineFormat.Resolve(null, hasVirtualSink: false, firstRealMixRate: null));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("auto", null)]
    [InlineData("AUTO", null)]
    [InlineData("44100", 44100)]
    [InlineData(" 48000 ", 48000)]
    [InlineData("96000", 96000)]
    [InlineData("12345", null)] // недопустимая частота → авто, а не отказ
    [InlineData("не число", null)]
    public void ParseSetting_DegradesGracefully(string? input, int? expected)
    {
        Assert.Equal(expected, EngineFormat.ParseSetting(input));
    }

    [Theory]
    [InlineData(44100, true)]
    [InlineData(48000, true)]
    [InlineData(88200, true)]
    [InlineData(96000, true)]
    [InlineData(22050, false)]
    [InlineData(0, false)]
    public void IsSupportedRate_ListedOnly(int rate, bool expected)
    {
        Assert.Equal(expected, EngineFormat.IsSupportedRate(rate));
    }

    [Fact]
    public void Rates_MatchesUiList()
    {
        Assert.Equal(new[] { 44100, 48000, 88200, 96000 }, EngineFormat.Rates);
    }
}
