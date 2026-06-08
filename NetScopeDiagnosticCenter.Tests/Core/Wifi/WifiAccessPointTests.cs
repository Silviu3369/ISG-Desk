using NetScopeDiagnosticCenter.Core.Models.Wifi;
using NetScopeDiagnosticCenter.Core.Wifi;
using NetScopeDiagnosticCenter.Infrastructure.Wlan;

namespace NetScopeDiagnosticCenter.Tests.Core.Wifi;

public sealed class WifiAccessPointTests
{
    [Fact]
    public void DisplayProperties_FormatVisibleNetworkRow()
    {
        var ap = MakeAccessPoint(
            ssid: "Office",
            vendor: "Contoso",
            channelWidthMhz: 80,
            averageRssiDbm: -61,
            detectionPercent: 87);

        ap.DisplaySsid.Should().Be("Office");
        ap.ChannelLabel.Should().Be("Ch 6");
        ap.ChannelWidthLabel.Should().Be("80 MHz");
        ap.SignalText.Should().Be("-58 dBm");
        ap.AverageRssiText.Should().Be("-61 dBm");
        ap.DetectionPercentText.Should().Be("87%");
        ap.VendorDisplay.Should().Be("Contoso");
    }

    [Fact]
    public void DisplayProperties_UseExplicitFallbacksForUnknownValues()
    {
        var ap = MakeAccessPoint(
            ssid: "",
            isHidden: true,
            vendor: null,
            channelWidthMhz: null,
            averageRssiDbm: null,
            detectionPercent: null);

        ap.DisplaySsid.Should().Be("<hidden>");
        ap.ChannelWidthLabel.Should().Be("Unknown");
        ap.AverageRssiText.Should().Be("No sample");
        ap.DetectionPercentText.Should().Be("No sample");
        ap.VendorDisplay.Should().Be("Unknown");
    }

    [Theory]
    [InlineData(-100, 0)]
    [InlineData(-90, 0)]
    [InlineData(-60, 36)]
    [InlineData(-30, 72)]
    [InlineData(-10, 72)]
    public void SignalBarWidth_ClampsToExpectedRange(int rssiDbm, double expectedWidth)
    {
        var ap = MakeAccessPoint(rssiDbm: rssiDbm);

        ap.SignalBarWidth.Should().BeApproximately(expectedWidth, 0.001);
    }

    [Fact]
    public void DetectionPercentText_ClampsOutOfRangePercent()
    {
        MakeAccessPoint(detectionPercent: 145).DetectionPercentText.Should().Be("100%");
        MakeAccessPoint(detectionPercent: -5).DetectionPercentText.Should().Be("0%");
    }

    private static WifiAccessPoint MakeAccessPoint(
        string ssid = "Office",
        bool isHidden = false,
        string? vendor = "Contoso",
        int? channelWidthMhz = 20,
        int rssiDbm = -58,
        int? averageRssiDbm = -60,
        int? detectionPercent = 100)
    {
        return new WifiAccessPoint(
            Ssid: ssid,
            IsHidden: isHidden,
            Bssid: "AA:BB:CC:00:00:01",
            Vendor: vendor,
            Channel: 6,
            CenterFrequencyMhz: 2437,
            ChannelWidthMhz: channelWidthMhz,
            Band: WifiBand.TwoPointFourGhz,
            PhyType: Dot11PhyType.He,
            RssiDbm: rssiDbm,
            SignalLevel: WifiFrequencyHelper.ClassifySignal(rssiDbm),
            LinkQualityPercent: 82,
            Security: WifiSecurityProfile.From(Dot11AuthAlgorithm.RsnaPsk, Dot11CipherAlgorithm.Ccmp, true),
            IsCurrentConnection: false,
            FirstSeenAt: DateTimeOffset.Now,
            LastSeenAt: DateTimeOffset.Now,
            AverageRssiDbm: averageRssiDbm,
            DetectionPercent: detectionPercent);
    }
}
