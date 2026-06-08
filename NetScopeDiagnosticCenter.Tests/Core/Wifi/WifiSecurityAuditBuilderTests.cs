using NetScopeDiagnosticCenter.Core.Models.Wifi;
using NetScopeDiagnosticCenter.Core.Wifi;
using NetScopeDiagnosticCenter.Infrastructure.Wlan;

namespace NetScopeDiagnosticCenter.Tests.Core.Wifi;

public sealed class WifiSecurityAuditBuilderTests
{
    [Fact]
    public void Build_OpenCurrentNetwork_ReturnsCriticalFinding()
    {
        var report = WifiSecurityAuditBuilder.Build(
            MakeConnection(auth: Dot11AuthAlgorithm.Open, cipher: Dot11CipherAlgorithm.None, securityEnabled: false),
            Array.Empty<WifiAccessPoint>(),
            Array.Empty<WifiSavedProfile>(),
            MakeCaps());

        report.Findings.Should().Contain(f =>
            f.Severity == "Critical" &&
            f.Category == "Current connection" &&
            f.Finding.Contains("open", StringComparison.OrdinalIgnoreCase));
        report.Summary.Should().Contain("critical");
    }

    [Fact]
    public void Build_SameSsidWithOpenAndSecuredBeacons_FlagsPossibleRogueAp()
    {
        WifiAccessPoint[] aps =
        [
            MakeAp(
                ssid: "Office",
                bssid: "AA:BB:CC:00:00:01",
                security: WifiSecurityProfile.From(Dot11AuthAlgorithm.RsnaPsk, Dot11CipherAlgorithm.Ccmp, true)),
            MakeAp(
                ssid: "Office",
                bssid: "DD:EE:FF:00:00:01",
                security: WifiSecurityProfile.From(Dot11AuthAlgorithm.Open, Dot11CipherAlgorithm.None, false))
        ];

        var report = WifiSecurityAuditBuilder.Build(
            MakeConnection(),
            aps,
            Array.Empty<WifiSavedProfile>(),
            MakeCaps());

        report.Findings.Should().Contain(f =>
            f.Severity == "Critical" &&
            f.Category == "SSID consistency" &&
            f.Recommendation.Contains("evil-twin", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Build_Wpa2WhenAdapterSupportsWpa3_ReturnsUpgradeWarning()
    {
        var report = WifiSecurityAuditBuilder.Build(
            MakeConnection(auth: Dot11AuthAlgorithm.RsnaPsk, cipher: Dot11CipherAlgorithm.Ccmp),
            Array.Empty<WifiAccessPoint>(),
            Array.Empty<WifiSavedProfile>(),
            MakeCaps(supportsWpa3: true));

        report.Findings.Should().Contain(f =>
            f.Severity == "Warning" &&
            f.Category == "Current connection" &&
            f.Recommendation.Contains("WPA3", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Build_PublicLikeSavedProfiles_ReturnsProfileHygieneWarning()
    {
        WifiSavedProfile[] profiles =
        [
            new("Office"),
            new("Airport Free WiFi"),
            new("Hotel Guest")
        ];

        var report = WifiSecurityAuditBuilder.Build(
            MakeConnection(auth: Dot11AuthAlgorithm.Wpa3Sae, cipher: Dot11CipherAlgorithm.Ccmp),
            Array.Empty<WifiAccessPoint>(),
            profiles,
            MakeCaps());

        report.Findings.Should().Contain(f =>
            f.Severity == "Warning" &&
            f.Category == "Saved profiles" &&
            f.Evidence.Contains("Airport Free WiFi", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Build_Wpa3NoOpenNetworks_ReturnsNoCriticalSummary()
    {
        var report = WifiSecurityAuditBuilder.Build(
            MakeConnection(auth: Dot11AuthAlgorithm.Wpa3Sae, cipher: Dot11CipherAlgorithm.Ccmp),
            [MakeAp(security: WifiSecurityProfile.From(Dot11AuthAlgorithm.Wpa3Sae, Dot11CipherAlgorithm.Ccmp, true))],
            [new WifiSavedProfile("Office")],
            MakeCaps());

        report.Summary.Should().Contain("No critical");
        report.Findings.Should().NotContain(f => f.Severity == "Critical" || f.Severity == "Warning");
    }

    private static WifiConnectionDetails MakeConnection(
        Dot11AuthAlgorithm auth = Dot11AuthAlgorithm.RsnaPsk,
        Dot11CipherAlgorithm cipher = Dot11CipherAlgorithm.Ccmp,
        bool securityEnabled = true)
    {
        var security = WifiSecurityProfile.From(auth, cipher, securityEnabled);
        return new WifiConnectionDetails(
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
            Security: security,
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
    }

    private static WifiAccessPoint MakeAp(
        string ssid = "Office",
        string bssid = "AA:BB:CC:00:00:01",
        WifiSecurityProfile? security = null)
    {
        return new WifiAccessPoint(
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
            Security: security ?? WifiSecurityProfile.From(Dot11AuthAlgorithm.RsnaPsk, Dot11CipherAlgorithm.Ccmp, true),
            IsCurrentConnection: false,
            FirstSeenAt: DateTimeOffset.Now,
            LastSeenAt: DateTimeOffset.Now);
    }

    private static WifiAdapterCapabilities MakeCaps(bool supportsWpa3 = true) =>
        new(
            InterfaceGuid: Guid.NewGuid(),
            Description: "Test adapter",
            PermanentMacAddress: "AA:BB:CC:DD:EE:FF",
            DriverName: "Intel",
            DriverVersion: "22.0.0",
            CountryCode: "US",
            SupportedBands: [WifiBand.TwoPointFourGhz, WifiBand.FiveGhz],
            SupportedPhyTypes: [Dot11PhyType.He, Dot11PhyType.Vht],
            SupportedTechnologies: ["MIMO", "OFDMA"],
            SupportsWpa3: supportsWpa3,
            SupportsWifi6E: false,
            SupportsWifi7: false,
            CapturedAt: DateTimeOffset.Now);
}
