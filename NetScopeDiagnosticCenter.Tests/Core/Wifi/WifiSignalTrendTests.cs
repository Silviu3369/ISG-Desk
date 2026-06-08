using NetScopeDiagnosticCenter.Core.Models.Wifi;

namespace NetScopeDiagnosticCenter.Tests.Core.Wifi;

public sealed class WifiSignalTrendTests
{
    [Fact]
    public void LatestRssiText_WithSamples_ShowsLatestDbm()
    {
        var trend = new WifiSignalTrend(
            bssid: "AA:BB:CC:DD:EE:FF",
            displaySsid: "Office",
            isCurrentConnection: true,
            rssiHistory: new[] { -67, -63, -61 },
            colorHex: "#38BDF8",
            isVisible: true);

        trend.LatestRssi.Should().Be(-61);
        trend.LatestRssiText.Should().Be("-61 dBm");
    }

    [Fact]
    public void LatestRssiText_WithoutSamples_IsExplicit()
    {
        var trend = new WifiSignalTrend(
            bssid: "AA:BB:CC:DD:EE:FF",
            displaySsid: "Office",
            isCurrentConnection: false,
            rssiHistory: Array.Empty<int>(),
            colorHex: "#38BDF8",
            isVisible: true);

        trend.LatestRssi.Should().BeNull();
        trend.LatestRssiText.Should().Be("No sample");
    }

    [Fact]
    public void IsVisible_RaisesPropertyChanged()
    {
        var trend = new WifiSignalTrend(
            bssid: "AA:BB:CC:DD:EE:FF",
            displaySsid: "Office",
            isCurrentConnection: false,
            rssiHistory: new[] { -70 },
            colorHex: "#38BDF8",
            isVisible: true);
        var changed = false;
        trend.PropertyChanged += (_, e) => changed = e.PropertyName == nameof(WifiSignalTrend.IsVisible);

        trend.IsVisible = false;

        changed.Should().BeTrue();
    }
}
