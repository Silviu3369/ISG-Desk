using NetScopeDiagnosticCenter.Core.Models.Wifi;
using NetScopeDiagnosticCenter.Core.Wifi;
using NetScopeDiagnosticCenter.Infrastructure.Wlan;

namespace NetScopeDiagnosticCenter.Tests.Core.Wifi;

/// <summary>
/// Tests for the pure-logic engine — channel analysis + scoring + roaming detection +
/// recommendations. Inputs are constructed by hand (no scanner dependency).
/// </summary>
public class WifiAnalyzerEngineTests
{
    private readonly WifiAnalyzerEngine _engine = new();

    // ------------------------------------------------------------------ helpers

    private static WifiAccessPoint MakeAp(
        string ssid = "Office",
        string bssid = "AA:BB:CC:00:00:01",
        int channel = 6,
        WifiBand band = WifiBand.TwoPointFourGhz,
        int rssi = -60,
        Dot11PhyType phy = Dot11PhyType.He,
        bool isCurrent = false)
    {
        var security = WifiSecurityProfile.From(Dot11AuthAlgorithm.RsnaPsk, Dot11CipherAlgorithm.Ccmp, true);
        return new WifiAccessPoint(
            Ssid: ssid, IsHidden: false, Bssid: bssid, Vendor: null,
            Channel: channel, CenterFrequencyMhz: 2437, ChannelWidthMhz: 20,
            Band: band, PhyType: phy,
            RssiDbm: rssi, SignalLevel: WifiFrequencyHelper.ClassifySignal(rssi),
            LinkQualityPercent: 80, Security: security,
            IsCurrentConnection: isCurrent,
            FirstSeenAt: DateTimeOffset.Now, LastSeenAt: DateTimeOffset.Now);
    }

    private static WifiConnectionDetails MakeConnection(
        string ssid = "Office",
        string bssid = "AA:BB:CC:00:00:01",
        int? channel = 6,
        WifiBand band = WifiBand.TwoPointFourGhz,
        int? rssiDbm = -60,
        Dot11PhyType phy = Dot11PhyType.He,
        Dot11AuthAlgorithm auth = Dot11AuthAlgorithm.RsnaPsk,
        Dot11CipherAlgorithm cipher = Dot11CipherAlgorithm.Ccmp,
        bool isConnected = true)
    {
        var security = isConnected ? WifiSecurityProfile.From(auth, cipher, true) : null;
        return new WifiConnectionDetails(
            IsConnected: isConnected, Ssid: ssid, IsHiddenSsid: false, Bssid: bssid,
            Vendor: null, Channel: channel, CenterFrequencyMhz: 2437, ChannelWidthMhz: 20,
            Band: band, PhyType: phy,
            RssiDbm: rssiDbm, SignalLevel: WifiFrequencyHelper.ClassifySignal(rssiDbm),
            SignalQualityPercent: 80, RxRateMbps: 866, TxRateMbps: 866,
            Security: security, ProfileName: ssid,
            InterfaceState: WlanInterfaceState.Connected,
            Ipv4Address: null, Ipv4SubnetMask: null, Ipv4Gateway: null, DnsServers: null,
            AdapterMacAddress: null, AdapterDescription: null,
            PublicIpAddress: null, IspName: null, IspLocation: null,
            CapturedAt: DateTimeOffset.Now);
    }

    private static WifiAdapterCapabilities MakeCaps(bool supports6E = true, bool supports7 = false) =>
        new(InterfaceGuid: Guid.NewGuid(), Description: "Test adapter",
            PermanentMacAddress: "AA:BB:CC:DD:EE:FF",
            DriverName: "Intel", DriverVersion: "22.0.0", CountryCode: "RO",
            SupportedBands: new[] { WifiBand.TwoPointFourGhz, WifiBand.FiveGhz },
            SupportedPhyTypes: new[] { Dot11PhyType.He, Dot11PhyType.Vht, Dot11PhyType.Ht },
            SupportedTechnologies: new[] { "MIMO", "OFDMA" },
            SupportsWpa3: true, SupportsWifi6E: supports6E, SupportsWifi7: supports7,
            CapturedAt: DateTimeOffset.Now);

    private static WifiChannelAnalysis MakeChannel(
        int channel,
        WifiBand band,
        int exact,
        int overlap,
        WifiCongestionLevel congestion = WifiCongestionLevel.Light,
        bool isDfs = false,
        bool isCurrent = false) =>
        new(
            Channel: channel,
            Band: band,
            CenterFrequencyMhz: 0,
            ApsOnExactChannel: exact,
            ApsOnOverlappingChannels: overlap,
            CongestionLevel: congestion,
            IsDfsChannel: isDfs,
            IsCurrentConnectionChannel: isCurrent);

    // ------------------------------------------------------------------ AnalyzeChannels

    [Fact]
    public void AnalyzeChannels_EmptyInput_ReturnsDefault24GhzMap()
    {
        var map = _engine.AnalyzeChannels(Array.Empty<WifiAccessPoint>(), currentAp: null);

        // Always shows channels 1, 6, 11 even when there are no scans.
        map.Should().HaveCountGreaterThanOrEqualTo(3);
        map.Should().Contain(c => c.Channel == 1 && c.Band == WifiBand.TwoPointFourGhz);
        map.Should().Contain(c => c.Channel == 6);
        map.Should().Contain(c => c.Channel == 11);
        map.Should().OnlyContain(c => c.CongestionLevel == WifiCongestionLevel.Free);
    }

    [Fact]
    public void AnalyzeChannels_24GhzApsCountedTowardOverlappingChannels()
    {
        // 5 APs all on channel 1 → channel 1 has 5 exact + 5 overlap (within self).
        // Channel 6 has 0 exact + ? overlap depending on window.
        var aps = Enumerable.Range(0, 5).Select(i => MakeAp(channel: 1, bssid: $"AA:BB:CC:00:00:{i:X2}")).ToList();

        var map = _engine.AnalyzeChannels(aps, null);
        var ch1 = map.First(c => c.Channel == 1 && c.Band == WifiBand.TwoPointFourGhz);

        ch1.ApsOnExactChannel.Should().Be(5);
        ch1.ApsOnOverlappingChannels.Should().Be(5);  // only channel 1 has APs; overlap window 1..3 = 5
        ch1.CongestionLevel.Should().Be(WifiCongestionLevel.Heavy);
    }

    [Fact]
    public void AnalyzeChannels_5GhzNonOverlapping()
    {
        // Two APs on different non-overlapping 5 GHz channels — each shows only its own count.
        var aps = new[]
        {
            MakeAp(channel: 36, band: WifiBand.FiveGhz, bssid: "AA:BB:CC:00:00:01"),
            MakeAp(channel: 40, band: WifiBand.FiveGhz, bssid: "AA:BB:CC:00:00:02"),
        };

        var map = _engine.AnalyzeChannels(aps, null);
        var ch36 = map.First(c => c.Channel == 36);
        var ch40 = map.First(c => c.Channel == 40);

        ch36.ApsOnExactChannel.Should().Be(1);
        ch36.ApsOnOverlappingChannels.Should().Be(1);   // 5 GHz: no overlap accumulation
        ch40.ApsOnExactChannel.Should().Be(1);
        ch40.ApsOnOverlappingChannels.Should().Be(1);
    }

    [Fact]
    public void AnalyzeChannels_MarksCurrentConnectionChannel()
    {
        var aps = new[] { MakeAp(channel: 11, isCurrent: true) };
        var currentAp = aps[0];

        var map = _engine.AnalyzeChannels(aps, currentAp);

        map.First(c => c.Channel == 11 && c.Band == WifiBand.TwoPointFourGhz)
            .IsCurrentConnectionChannel.Should().BeTrue();
        map.First(c => c.Channel == 1).IsCurrentConnectionChannel.Should().BeFalse();
    }

    [Fact]
    public void AnalyzeChannels_MarksDfsChannels()
    {
        var aps = new[] { MakeAp(channel: 52, band: WifiBand.FiveGhz) };
        var map = _engine.AnalyzeChannels(aps, null);
        map.First(c => c.Channel == 52).IsDfsChannel.Should().BeTrue();
    }

    // ------------------------------------------------------------------ Health scoring

    [Fact]
    public void GenerateHealthReport_NotConnected_ReturnsNotConnectedSentinel()
    {
        var conn = MakeConnection(isConnected: false);
        var report = _engine.GenerateHealthReport(conn, Array.Empty<WifiChannelAnalysis>(),
            Array.Empty<WifiAccessPoint>(), MakeCaps());

        report.Score.Should().BeNull();
        report.Verdict.Should().Be("Not connected");
    }

    [Fact]
    public void GenerateHealthReport_ExcellentSignalCleanChannelWpa3Wifi6_ScoresHigh()
    {
        var conn = MakeConnection(rssiDbm: -55, phy: Dot11PhyType.He,
            auth: Dot11AuthAlgorithm.Wpa3Sae, cipher: Dot11CipherAlgorithm.Ccmp);
        var map = _engine.AnalyzeChannels(new[] { MakeAp(channel: 6, isCurrent: true, rssi: -55) }, null);

        var report = _engine.GenerateHealthReport(conn, map, new[] { MakeAp() }, MakeCaps());

        report.Score.Should().BeGreaterThanOrEqualTo(85);
        report.Verdict.Should().Be("Excellent");
        report.SignalContribution.Should().Be(40);    // Excellent
        report.SecurityContribution.Should().Be(15);  // WPA3 forward secrecy
        report.PhyContribution.Should().Be(15);       // Wi-Fi 6
    }

    [Fact]
    public void GenerateHealthReport_PoorSignalOpenNetworkLegacyPhy_ScoresLow()
    {
        var conn = MakeConnection(rssiDbm: -90, phy: Dot11PhyType.Erp,
            auth: Dot11AuthAlgorithm.Open, cipher: Dot11CipherAlgorithm.None);
        var report = _engine.GenerateHealthReport(conn, Array.Empty<WifiChannelAnalysis>(),
            Array.Empty<WifiAccessPoint>(), MakeCaps());

        report.Score.Should().BeLessThan(50);
        report.Verdict.Should().Be("Poor");
        report.SignalContribution.Should().Be(5);     // Poor band
        report.SecurityContribution.Should().Be(0);   // Open
    }

    [Fact]
    public void GenerateHealthReport_HeavyCongestion_LowersCongestionContribution()
    {
        var conn = MakeConnection(channel: 1);
        // 7 APs on channel 1 → Heavy congestion.
        var aps = Enumerable.Range(0, 7).Select(i => MakeAp(channel: 1, bssid: $"00:00:00:00:00:{i:X2}")).ToList();
        var map = _engine.AnalyzeChannels(aps, currentAp: aps[0]);

        var report = _engine.GenerateHealthReport(conn, map, aps, MakeCaps());
        report.CongestionContribution.Should().Be(4); // Heavy
    }

    // ------------------------------------------------------------------ Roaming detection

    [Fact]
    public void DetectRoaming_BssidChangedSameSsid_EmitsEvent()
    {
        var prev = MakeConnection(ssid: "Office", bssid: "AA:BB:CC:00:00:01", rssiDbm: -60);
        var curr = MakeConnection(ssid: "Office", bssid: "AA:BB:CC:00:00:02", rssiDbm: -45);

        var ev = _engine.DetectRoaming(prev, curr);

        ev.Should().NotBeNull();
        ev!.Value.Ssid.Should().Be("Office");
        ev.Value.FromBssid.Should().Be("AA:BB:CC:00:00:01");
        ev.Value.ToBssid.Should().Be("AA:BB:CC:00:00:02");
        ev.Value.DeltaLabel.Should().Be("Stronger");   // -45 > -60 by 15 dB
    }

    [Fact]
    public void DetectRoaming_DifferentSsid_NoEvent()
    {
        // Network switch (not a roam) — SSID changed → not a roam event.
        var prev = MakeConnection(ssid: "Office", bssid: "AA:BB:CC:00:00:01");
        var curr = MakeConnection(ssid: "Home", bssid: "DD:EE:FF:00:00:02");

        _engine.DetectRoaming(prev, curr).Should().BeNull();
    }

    [Fact]
    public void DetectRoaming_SameBssid_NoEvent()
    {
        var prev = MakeConnection(bssid: "AA:BB:CC:00:00:01");
        var curr = MakeConnection(bssid: "AA:BB:CC:00:00:01");
        _engine.DetectRoaming(prev, curr).Should().BeNull();
    }

    [Fact]
    public void DetectRoaming_PreviouslyDisconnected_NoEvent()
    {
        var prev = MakeConnection(isConnected: false);
        var curr = MakeConnection(bssid: "AA:BB:CC:00:00:02");
        _engine.DetectRoaming(prev, curr).Should().BeNull();
    }

    // ------------------------------------------------------------------ Recommendations

    [Fact]
    public void Recommendations_PoorSignal_AdvisesMovingCloser()
    {
        var conn = MakeConnection(rssiDbm: -90);
        var report = _engine.GenerateHealthReport(conn, Array.Empty<WifiChannelAnalysis>(),
            Array.Empty<WifiAccessPoint>(), MakeCaps());

        report.Recommendations.Should().Contain(r => r.Contains("dBm") && r.Contains("poor", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Recommendations_HeavyCongestion_SuggestsChannelSwitch()
    {
        // Current channel 1 heavily congested, channel 11 free → recommend switch to 11.
        var apsCh1 = Enumerable.Range(0, 6).Select(i => MakeAp(channel: 1, bssid: $"AA:00:00:00:00:{i:X2}")).ToList();
        var conn = MakeConnection(channel: 1);
        var map = _engine.AnalyzeChannels(apsCh1, currentAp: apsCh1[0]);

        var report = _engine.GenerateHealthReport(conn, map, apsCh1, MakeCaps());

        report.Recommendations.Should().Contain(r => r.Contains("channel", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Recommendations_OpenNetwork_AdvisesSwitch()
    {
        var conn = MakeConnection(auth: Dot11AuthAlgorithm.Open, cipher: Dot11CipherAlgorithm.None);
        var report = _engine.GenerateHealthReport(conn, Array.Empty<WifiChannelAnalysis>(),
            Array.Empty<WifiAccessPoint>(), MakeCaps());

        report.Recommendations.Should().Contain(r => r.Contains("OPEN", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Recommendations_AreCapAtFive()
    {
        // Stack every red flag: poor signal + heavy congestion + open + hidden + DFS.
        var apsCh1 = Enumerable.Range(0, 8).Select(i => MakeAp(channel: 1, bssid: $"AA:00:00:00:00:{i:X2}")).ToList();
        var conn = MakeConnection(channel: 1, rssiDbm: -90,
            auth: Dot11AuthAlgorithm.Open, cipher: Dot11CipherAlgorithm.None) with
        {
            IsHiddenSsid = true,
        };
        var map = _engine.AnalyzeChannels(apsCh1, currentAp: apsCh1[0]);
        var report = _engine.GenerateHealthReport(conn, map, apsCh1, MakeCaps());

        report.Recommendations.Count.Should().BeLessThanOrEqualTo(5);
    }

    [Fact]
    public void Recommendations_UseStableAsciiTextPrefixes()
    {
        var apsCh1 = Enumerable.Range(0, 8)
            .Select(i => MakeAp(channel: 1, bssid: $"AA:00:00:00:00:{i:X2}"))
            .ToList();
        var conn = MakeConnection(channel: 1, rssiDbm: -90,
            auth: Dot11AuthAlgorithm.Open, cipher: Dot11CipherAlgorithm.None);
        var map = _engine.AnalyzeChannels(apsCh1, currentAp: apsCh1[0]);

        var report = _engine.GenerateHealthReport(conn, map, apsCh1, MakeCaps());

        report.Recommendations.Should().NotBeEmpty();
        report.Recommendations.Should().Contain(r => r.StartsWith("Signal:", StringComparison.Ordinal));
        report.Recommendations.Should().Contain(r => r.StartsWith("Channel:", StringComparison.Ordinal));
        report.Recommendations.Should().Contain(r => r.StartsWith("Security:", StringComparison.Ordinal));
        report.Recommendations.SelectMany(r => r).Should().OnlyContain(c => c <= 127);
    }

    // ------------------------------------------------------------------ BuildBestChannelAdvice

    [Fact]
    public void BestChannelAdvice_EmptyMap_ReturnsEmpty()
    {
        var advice = WifiAnalyzerEngine.BuildBestChannelAdvice(
            Array.Empty<WifiChannelAnalysis>(), MakeConnection());

        advice.Should().BeEmpty();
    }

    [Fact]
    public void BestChannelAdvice_24Ghz_PicksClearestOfOneSixEleven()
    {
        // Channel 1 crowded (6 APs), channel 6 light (1 AP), channel 11 crowded (5 APs).
        var aps = new List<WifiAccessPoint>();
        aps.AddRange(Enumerable.Range(0, 6).Select(i => MakeAp(channel: 1, bssid: $"A1:00:00:00:00:{i:X2}")));
        aps.AddRange(Enumerable.Range(0, 1).Select(i => MakeAp(channel: 6, bssid: $"A6:00:00:00:00:{i:X2}")));
        aps.AddRange(Enumerable.Range(0, 5).Select(i => MakeAp(channel: 11, bssid: $"AB:00:00:00:00:{i:X2}")));
        var map = _engine.AnalyzeChannels(aps, null);

        var advice = WifiAnalyzerEngine.BuildBestChannelAdvice(map, MakeConnection(channel: 1));

        advice.Should().Contain("2.4 GHz: channel 6 is clearest");
    }

    [Fact]
    public void BestChannelAdvice_AlreadyOnClearestChannel_SaysSo()
    {
        // Ch 1 = 3 APs, ch 11 = 2 APs, ch 6 = just us (1 AP) → 6 is genuinely clearest.
        var aps = new List<WifiAccessPoint>();
        aps.AddRange(Enumerable.Range(0, 3).Select(i => MakeAp(channel: 1, bssid: $"A1:00:00:00:00:{i:X2}")));
        aps.AddRange(Enumerable.Range(0, 2).Select(i => MakeAp(channel: 11, bssid: $"AB:00:00:00:00:{i:X2}")));
        aps.Add(MakeAp(channel: 6, bssid: "A6:00:00:00:00:01"));
        var map = _engine.AnalyzeChannels(aps, null);

        var advice = WifiAnalyzerEngine.BuildBestChannelAdvice(map, MakeConnection(channel: 6));

        advice.Should().Contain("already on the clearest");
    }

    [Fact]
    public void BestChannelAdvice_5Ghz_PrefersNonDfsChannel()
    {
        // Non-DFS ch 36 (2 APs) vs DFS ch 52 (0 APs). Must still prefer 36 (DFS can drop).
        var aps = new List<WifiAccessPoint>
        {
            MakeAp(channel: 36, band: WifiBand.FiveGhz, bssid: "B0:00:00:00:00:01"),
            MakeAp(channel: 36, band: WifiBand.FiveGhz, bssid: "B0:00:00:00:00:02"),
        };
        var map = _engine.AnalyzeChannels(aps, null);

        var advice = WifiAnalyzerEngine.BuildBestChannelAdvice(map, MakeConnection(band: WifiBand.FiveGhz, channel: 40));

        advice.Should().Contain("5 GHz: channel 36 is clearest");
    }

    [Fact]
    public void BestChannelAdvice_6Ghz_IncludesClearestChannel()
    {
        var aps = new List<WifiAccessPoint>
        {
            MakeAp(channel: 5, band: WifiBand.SixGhz, bssid: "C0:00:00:00:00:01"),
            MakeAp(channel: 5, band: WifiBand.SixGhz, bssid: "C0:00:00:00:00:02"),
            MakeAp(channel: 37, band: WifiBand.SixGhz, bssid: "C0:00:00:00:00:03"),
        };
        var map = _engine.AnalyzeChannels(aps, null);

        var advice = WifiAnalyzerEngine.BuildBestChannelAdvice(map, MakeConnection(band: WifiBand.SixGhz, channel: 5));

        advice.Should().Contain("6 GHz: channel 37 is clearest");
        advice.ToCharArray().Should().OnlyContain(c => c <= 127);
    }

    // ------------------------------------------------------------------ BuildChannelRecommendation

    [Fact]
    public void ChannelRecommendation_24Ghz_NonStandardChannelRecommendsOneSixOrEleven()
    {
        var map = new[]
        {
            MakeChannel(1, WifiBand.TwoPointFourGhz, exact: 3, overlap: 5, congestion: WifiCongestionLevel.Heavy),
            MakeChannel(3, WifiBand.TwoPointFourGhz, exact: 1, overlap: 4, congestion: WifiCongestionLevel.Moderate, isCurrent: true),
            MakeChannel(6, WifiBand.TwoPointFourGhz, exact: 1, overlap: 2, congestion: WifiCongestionLevel.Light),
            MakeChannel(11, WifiBand.TwoPointFourGhz, exact: 0, overlap: 0, congestion: WifiCongestionLevel.Free),
        };

        var recommendation = WifiAnalyzerEngine.BuildChannelRecommendation(
            map,
            MakeConnection(channel: 3),
            new DateTimeOffset(2026, 6, 4, 8, 0, 0, TimeSpan.Zero));

        recommendation.Severity.Should().Be("Warning");
        recommendation.Current.Should().Contain("2.4 GHz ch 3");
        recommendation.Recommended.Should().Be("2.4 GHz ch 11");
        recommendation.Reason.Should().Contain("1, 6 or 11");
        recommendation.HasMoveRecommendation.Should().BeTrue();
        recommendation.Detail.ToCharArray().Should().OnlyContain(c => c <= 127);
    }

    [Fact]
    public void ChannelRecommendation_24Ghz_AlreadyBestReturnsOkNoChange()
    {
        var map = new[]
        {
            MakeChannel(1, WifiBand.TwoPointFourGhz, exact: 2, overlap: 4, congestion: WifiCongestionLevel.Moderate),
            MakeChannel(6, WifiBand.TwoPointFourGhz, exact: 1, overlap: 1, congestion: WifiCongestionLevel.Light, isCurrent: true),
            MakeChannel(11, WifiBand.TwoPointFourGhz, exact: 3, overlap: 5, congestion: WifiCongestionLevel.Heavy),
        };

        var recommendation = WifiAnalyzerEngine.BuildChannelRecommendation(map, MakeConnection(channel: 6));

        recommendation.Severity.Should().Be("OK");
        recommendation.Recommended.Should().Be("No change");
        recommendation.HasMoveRecommendation.Should().BeFalse();
        recommendation.Reason.Should().Contain("already");
    }

    [Fact]
    public void ChannelRecommendation_5Ghz_DfsCurrentRecommendsNonDfs()
    {
        var map = new[]
        {
            MakeChannel(36, WifiBand.FiveGhz, exact: 2, overlap: 2, congestion: WifiCongestionLevel.Light),
            MakeChannel(52, WifiBand.FiveGhz, exact: 0, overlap: 0, congestion: WifiCongestionLevel.Free, isDfs: true, isCurrent: true),
        };

        var recommendation = WifiAnalyzerEngine.BuildChannelRecommendation(
            map,
            MakeConnection(band: WifiBand.FiveGhz, channel: 52));

        recommendation.Severity.Should().Be("Warning");
        recommendation.Current.Should().Contain("DFS");
        recommendation.Recommended.Should().Be("5 GHz ch 36");
        recommendation.Reason.Should().Contain("non-DFS");
    }

    [Fact]
    public void ChannelRecommendation_6Ghz_PicksClearestMeasuredChannel()
    {
        var map = new[]
        {
            MakeChannel(5, WifiBand.SixGhz, exact: 3, overlap: 3, congestion: WifiCongestionLevel.Moderate, isCurrent: true),
            MakeChannel(37, WifiBand.SixGhz, exact: 1, overlap: 1, congestion: WifiCongestionLevel.Light),
        };

        var recommendation = WifiAnalyzerEngine.BuildChannelRecommendation(
            map,
            MakeConnection(band: WifiBand.SixGhz, channel: 5));

        recommendation.Severity.Should().Be("Warning");
        recommendation.Recommended.Should().Be("6 GHz ch 37");
        recommendation.Reason.Should().Contain("quieter");
    }

    [Fact]
    public void ChannelRecommendation_DisconnectedDoesNotRecommendMove()
    {
        var recommendation = WifiAnalyzerEngine.BuildChannelRecommendation(
            new[] { MakeChannel(6, WifiBand.TwoPointFourGhz, exact: 0, overlap: 0, congestion: WifiCongestionLevel.Free) },
            MakeConnection(isConnected: false));

        recommendation.Severity.Should().Be("Info");
        recommendation.Current.Should().Be("Not connected");
        recommendation.Recommended.Should().Be("No recommendation");
        recommendation.HasMoveRecommendation.Should().BeFalse();
    }
}
