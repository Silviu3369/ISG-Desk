using NetScopeDiagnosticCenter.Core.Models.Wifi;
using NetScopeDiagnosticCenter.UI.Controls;

namespace NetScopeDiagnosticCenter.Tests.UI.Controls;

public sealed class WifiChannelGraphScaleTests
{
    [Theory]
    [InlineData(WifiBand.TwoPointFourGhz, 20, 2.0)]
    [InlineData(WifiBand.TwoPointFourGhz, 40, 4.0)]
    [InlineData(WifiBand.FiveGhz, 20, 2.0)]
    [InlineData(WifiBand.FiveGhz, 80, 8.0)]
    [InlineData(WifiBand.SixGhz, 160, 16.0)]
    public void GetHalfChannelWidth_MapsWifiWidthsToChannelUnits(
        WifiBand band,
        int widthMhz,
        double expectedHalfChannels)
    {
        WifiChannelGraphScale.GetHalfChannelWidth(band, widthMhz)
            .Should().BeApproximately(expectedHalfChannels, 0.001);
    }

    [Fact]
    public void GetAxisChannels_FiveGhz_UsesCommonProfessionalTicks()
    {
        var ticks = WifiChannelGraphScale.GetAxisChannels(WifiBand.FiveGhz);

        ticks.First().Should().Be(36);
        ticks.Should().Contain(36);
        ticks.Should().Contain(44);
        ticks.Should().Contain(52);
        ticks.Should().Contain(100);
        ticks.Should().Contain(149);
        ticks.Should().Contain(165);
        ticks.Should().NotContain(32);
    }

    [Fact]
    public void GetAxisChannels_SixGhz_CoversLowMiddleAndHighChannels()
    {
        var ticks = WifiChannelGraphScale.GetAxisChannels(WifiBand.SixGhz);

        ticks.First().Should().Be(1);
        ticks.Should().Contain(1);
        ticks.Should().Contain(81);
        ticks.Should().Contain(145);
        ticks.Should().Contain(225);
        ticks.Should().OnlyContain(channel => channel >= 1 && channel <= 233);
    }

    [Theory]
    [InlineData(WifiBand.TwoPointFourGhz, 1, 14)]
    [InlineData(WifiBand.FiveGhz, 32, 177)]
    [InlineData(WifiBand.SixGhz, 1, 233)]
    public void GetChannelRange_CoversExpectedBandSpan(WifiBand band, int expectedLow, int expectedHigh)
    {
        WifiChannelGraphScale.GetChannelRange(band).Should().Be((expectedLow, expectedHigh));
    }
}
