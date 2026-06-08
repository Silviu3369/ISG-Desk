using NetScopeDiagnosticCenter.Core.Models.Wifi;
using NetScopeDiagnosticCenter.Core.Wifi;
using NetScopeDiagnosticCenter.Infrastructure.Wlan;

namespace NetScopeDiagnosticCenter.Tests.Core.Wifi;

public sealed class WifiSavedProfileHygieneBuilderTests
{
    [Fact]
    public void Build_NoProfiles_ReturnsEmptyOkSummary()
    {
        var report = WifiSavedProfileHygieneBuilder.Build(
            MakeConnection(),
            Array.Empty<WifiAccessPoint>(),
            Array.Empty<WifiSavedProfile>());

        report.Items.Should().BeEmpty();
        report.Summary.Should().Contain("No saved Wi-Fi profiles");
    }

    [Fact]
    public void Build_PublicOpenAndStaleProfiles_AuditEachProfile()
    {
        WifiSavedProfile[] profiles =
        [
            new("Office"),
            new("Airport Free WiFi"),
            new("LegacyGuest"),
            new("OldLab"),
        ];
        WifiAccessPoint[] aps =
        [
            MakeAp("Office", WifiSecurityProfile.From(Dot11AuthAlgorithm.RsnaPsk, Dot11CipherAlgorithm.Ccmp, true)),
            MakeAp("LegacyGuest", WifiSecurityProfile.From(Dot11AuthAlgorithm.Open, Dot11CipherAlgorithm.None, false)),
        ];

        var report = WifiSavedProfileHygieneBuilder.Build(
            MakeConnection(),
            aps,
            profiles);

        report.Items.Should().Contain(item =>
            item.ProfileName == "Office" &&
            item.Severity == "OK" &&
            item.State == "Current");
        report.Items.Should().Contain(item =>
            item.ProfileName == "Airport Free WiFi" &&
            item.Severity == "Warning" &&
            item.Evidence.Contains("public", StringComparison.OrdinalIgnoreCase));
        report.Items.Should().Contain(item =>
            item.ProfileName == "LegacyGuest" &&
            item.Severity == "Warning" &&
            item.Security.Contains("Open", StringComparison.OrdinalIgnoreCase));
        report.Items.Should().Contain(item =>
            item.ProfileName == "OldLab" &&
            item.Severity == "Info" &&
            item.State == "Not visible");
        report.Summary.Should().Contain("warning");
    }

    [Fact]
    public void Build_MixedSecurityVisibleProfile_ReturnsCritical()
    {
        WifiAccessPoint[] aps =
        [
            MakeAp("Office", WifiSecurityProfile.From(Dot11AuthAlgorithm.Open, Dot11CipherAlgorithm.None, false)),
            MakeAp("Office", WifiSecurityProfile.From(Dot11AuthAlgorithm.RsnaPsk, Dot11CipherAlgorithm.Ccmp, true), bssid: "AA:BB:CC:00:00:02"),
        ];

        var report = WifiSavedProfileHygieneBuilder.Build(
            MakeConnection(),
            aps,
            [new WifiSavedProfile("Office")]);

        report.Items.Should().ContainSingle(item =>
            item.Severity == "Critical" &&
            item.Recommendation.Contains("rogue", StringComparison.OrdinalIgnoreCase));
    }

    private static WifiConnectionDetails MakeConnection() => new(
        IsConnected: true,
        Ssid: "Office",
        IsHiddenSsid: false,
        Bssid: "AA:BB:CC:00:00:01",
        Vendor: "Test Vendor",
        Channel: 6,
        CenterFrequencyMhz: 2437,
        ChannelWidthMhz: 20,
        Band: WifiBand.TwoPointFourGhz,
        PhyType: Dot11PhyType.He,
        RssiDbm: -55,
        SignalLevel: WifiFrequencyHelper.ClassifySignal(-55),
        SignalQualityPercent: 90,
        RxRateMbps: 866,
        TxRateMbps: 866,
        Security: WifiSecurityProfile.From(Dot11AuthAlgorithm.RsnaPsk, Dot11CipherAlgorithm.Ccmp, true),
        ProfileName: "Office",
        InterfaceState: WlanInterfaceState.Connected,
        Ipv4Address: null,
        Ipv4SubnetMask: null,
        Ipv4Gateway: null,
        DnsServers: null,
        AdapterMacAddress: null,
        AdapterDescription: null,
        PublicIpAddress: null,
        IspName: null,
        IspLocation: null,
        CapturedAt: DateTimeOffset.Now);

    private static WifiAccessPoint MakeAp(
        string ssid,
        WifiSecurityProfile security,
        string bssid = "AA:BB:CC:00:00:01") =>
        new(
            Ssid: ssid,
            IsHidden: false,
            Bssid: bssid,
            Vendor: "Test Vendor",
            Channel: 6,
            CenterFrequencyMhz: 2437,
            ChannelWidthMhz: 20,
            Band: WifiBand.TwoPointFourGhz,
            PhyType: Dot11PhyType.He,
            RssiDbm: -55,
            SignalLevel: WifiFrequencyHelper.ClassifySignal(-55),
            LinkQualityPercent: 90,
            Security: security,
            IsCurrentConnection: false,
            FirstSeenAt: DateTimeOffset.Now,
            LastSeenAt: DateTimeOffset.Now);
}
