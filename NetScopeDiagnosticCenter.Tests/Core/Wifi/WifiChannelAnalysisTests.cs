using NetScopeDiagnosticCenter.Core.Models.Wifi;

namespace NetScopeDiagnosticCenter.Tests.Core.Wifi;

public sealed class WifiChannelAnalysisTests
{
    [Fact]
    public void DisplayProperties_FormatChannelCongestionRow()
    {
        var row = new WifiChannelAnalysis(
            Channel: 11,
            Band: WifiBand.TwoPointFourGhz,
            CenterFrequencyMhz: 2462,
            ApsOnExactChannel: 2,
            ApsOnOverlappingChannels: 4,
            CongestionLevel: WifiCongestionLevel.Moderate,
            IsDfsChannel: false,
            IsCurrentConnectionChannel: true);

        row.ChannelLabel.Should().Be("Ch 11");
        row.BandLabel.Should().Be("2.4 GHz");
        row.CongestionLabel.Should().Be("Moderate");
        row.CongestionColor.Should().Be("#F59E0B");
        row.ApCountText.Should().Be("4 APs");
        row.ExactCountText.Should().Be("2 exact");
        row.CongestionMeterValue.Should().Be(4);
        row.CongestionMeterMax.Should().Be(WifiChannelAnalysis.CongestionMeterMaximum);
    }

    [Fact]
    public void CongestionMeterValue_CapsBusyChannels()
    {
        var row = new WifiChannelAnalysis(
            Channel: 36,
            Band: WifiBand.FiveGhz,
            CenterFrequencyMhz: 5180,
            ApsOnExactChannel: 24,
            ApsOnOverlappingChannels: 24,
            CongestionLevel: WifiCongestionLevel.Heavy,
            IsDfsChannel: false,
            IsCurrentConnectionChannel: false);

        row.CongestionMeterValue.Should().Be(WifiChannelAnalysis.CongestionMeterMaximum);
        row.ApCountText.Should().Be("24 APs");
    }

    [Fact]
    public void ApCountText_UsesSingularForOneAp()
    {
        var row = new WifiChannelAnalysis(
            Channel: 149,
            Band: WifiBand.FiveGhz,
            CenterFrequencyMhz: 5745,
            ApsOnExactChannel: 1,
            ApsOnOverlappingChannels: 1,
            CongestionLevel: WifiCongestionLevel.Light,
            IsDfsChannel: false,
            IsCurrentConnectionChannel: false);

        row.ApCountText.Should().Be("1 AP");
    }
}
