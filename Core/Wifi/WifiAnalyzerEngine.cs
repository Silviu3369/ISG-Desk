using NetScopeDiagnosticCenter.Core.Models.Wifi;
using NetScopeDiagnosticCenter.Infrastructure.Wlan;

namespace NetScopeDiagnosticCenter.Core.Wifi;

/// <summary>
/// Pure-logic engine that turns raw collector output into:
/// <list type="bullet">
///   <item><see cref="WifiChannelAnalysis"/> — per-channel congestion, used by Channel Map (UI Section 4)</item>
///   <item><see cref="WifiHealthReport"/> — score 0..100 + verdict + recommendations (Section 9)</item>
///   <item>Roaming detection — emits <see cref="WifiRoamingEvent"/> on BSSID change</item>
/// </list>
///
/// <para>
/// Pure: no I/O, no async, no static mutable state. The engine takes inputs (current
/// connection + visible APs + previous BSSID) and produces outputs. This makes scoring
/// + recommendation logic straightforward to unit-test (target: 25+ tests in 5e).
/// </para>
///
/// <para>
/// Health score weights (sum to 100):
/// <list type="bullet">
///   <item>Signal: 40 pts — Excellent=40 / Good=30 / Fair=15 / Poor=5</item>
///   <item>Congestion: 30 pts — Free=30 / Light=22 / Moderate=12 / Heavy=4</item>
///   <item>Security: 15 pts — WPA3=15 / WPA2=12 / WPA-Personal=5 / WEP/Open=0</item>
///   <item>PHY generation: 15 pts — Wi-Fi 6/6E/7=15 / Wi-Fi 5=12 / Wi-Fi 4=8 / older=3</item>
/// </list>
/// </para>
/// </summary>
public sealed class WifiAnalyzerEngine
{
    private readonly TimeProvider _time;

    public WifiAnalyzerEngine(TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
    }

    /// <summary>
    /// Compute the channel map for the given visible AP set.
    /// Returns one entry per (band, channel) actually present, plus a few "free" entries
    /// for the standard non-overlapping 2.4 GHz channels (1/6/11) so the UI always shows them.
    /// </summary>
    public IReadOnlyList<WifiChannelAnalysis> AnalyzeChannels(
        IReadOnlyList<WifiAccessPoint> visibleAps,
        WifiAccessPoint? currentAp)
    {
        if (visibleAps is null || visibleAps.Count == 0)
        {
            return ReturnDefault24GhzMap();
        }

        // First, count APs per (band, channel).
        var counts = new Dictionary<(WifiBand Band, int Channel), int>();
        var freqMap = new Dictionary<(WifiBand Band, int Channel), int>();
        foreach (var ap in visibleAps)
        {
            if (ap.Band == WifiBand.Unknown || ap.Channel == 0) continue;
            var key = (ap.Band, ap.Channel);
            counts.TryGetValue(key, out var n);
            counts[key] = n + 1;
            freqMap[key] = ap.CenterFrequencyMhz;
        }

        // Always include 2.4 GHz channels 1, 6, 11 even if empty (the recommended non-
        // overlapping triplet). Helpdesk uses the map to recommend "switch to ch 11".
        foreach (var ch in new[] { 1, 6, 11 })
        {
            counts.TryAdd((WifiBand.TwoPointFourGhz, ch), 0);
        }

        // Build the analyses with overlap counts (relevant only for 2.4 GHz).
        var result = new List<WifiChannelAnalysis>(counts.Count);
        foreach (var ((band, channel), exact) in counts)
        {
            int overlap;
            if (band == WifiBand.TwoPointFourGhz)
            {
                overlap = 0;
                foreach (var nb in WifiFrequencyHelper.GetOverlappingChannels(band, channel))
                {
                    counts.TryGetValue((band, nb), out var c);
                    overlap += c;
                }
            }
            else
            {
                overlap = exact;   // non-overlapping channels at 20 MHz
            }

            freqMap.TryGetValue((band, channel), out var freq);
            result.Add(new WifiChannelAnalysis(
                Channel: channel,
                Band: band,
                CenterFrequencyMhz: freq,
                ApsOnExactChannel: exact,
                ApsOnOverlappingChannels: overlap,
                CongestionLevel: WifiFrequencyHelper.ClassifyCongestion(overlap),
                IsDfsChannel: WifiFrequencyHelper.IsDfsChannel(band, channel),
                IsCurrentConnectionChannel: currentAp is not null
                    && currentAp.Band == band
                    && currentAp.Channel == channel));
        }

        // Order: band first (2.4 → 5 → 6), then channel ascending. UI expects this order.
        result.Sort((a, b) => a.Band != b.Band
            ? a.Band.CompareTo(b.Band)
            : a.Channel.CompareTo(b.Channel));

        return result;
    }

    /// <summary>
    /// inSSIDer-style "best channel" advice derived from the channel map. For 2.4 GHz we
    /// only ever recommend the non-overlapping triplet (1 / 6 / 11) — recommending any
    /// other 2.4 GHz channel is actively harmful because it overlaps two of those three.
    /// For 5 GHz we prefer non-DFS channels (DFS can force a disconnect on radar). The
    /// returned string is helpdesk-ready ("paste into the ticket") or empty when there
    /// isn't enough data to give honest advice.
    /// </summary>
    public static string BuildBestChannelAdvice(
        IReadOnlyList<WifiChannelAnalysis> channelMap,
        WifiConnectionDetails connection)
    {
        if (channelMap is null || channelMap.Count == 0) return string.Empty;

        var parts = new List<string>(3);

        // --- 2.4 GHz: choose the clearest of 1 / 6 / 11. ---
        var triplet = channelMap
            .Where(c => c.Band == WifiBand.TwoPointFourGhz && c.Channel is 1 or 6 or 11)
            .ToList();
        if (triplet.Count > 0)
        {
            var best = triplet
                .OrderBy(c => c.ApsOnOverlappingChannels)
                .ThenBy(c => c.Channel)
                .First();
            var onCurrent = connection.Band == WifiBand.TwoPointFourGhz
                            && connection.Channel == best.Channel;
            parts.Add(onCurrent
                ? $"2.4 GHz: you're already on the clearest channel ({best.Channel}, {best.ApsOnOverlappingChannels} overlapping AP(s))."
                : $"2.4 GHz: channel {best.Channel} is clearest ({best.ApsOnOverlappingChannels} overlapping AP(s)).");
        }

        // --- 5 GHz: clearest non-DFS channel actually seen in the area. ---
        var fiveGhz = channelMap.Where(c => c.Band == WifiBand.FiveGhz).ToList();
        if (fiveGhz.Count > 0)
        {
            var bestNonDfs = fiveGhz
                .Where(c => !c.IsDfsChannel)
                .OrderBy(c => c.ApsOnExactChannel)
                .ThenBy(c => c.Channel)
                .FirstOrDefault();
            var pick = bestNonDfs ?? fiveGhz.OrderBy(c => c.ApsOnExactChannel).ThenBy(c => c.Channel).First();
            var dfsNote = pick.IsDfsChannel ? " (DFS - may drop on radar)" : string.Empty;
            var onCurrent = connection.Band == WifiBand.FiveGhz && connection.Channel == pick.Channel;
            parts.Add(onCurrent
                ? $"5 GHz: you're already on the clearest channel ({pick.Channel}, {pick.ApsOnExactChannel} AP(s)){dfsNote}."
                : $"5 GHz: channel {pick.Channel} is clearest ({pick.ApsOnExactChannel} AP(s)){dfsNote}.");
        }

        var sixGhz = channelMap.Where(c => c.Band == WifiBand.SixGhz).ToList();
        if (sixGhz.Count > 0)
        {
            var pick = sixGhz
                .OrderBy(c => c.ApsOnExactChannel)
                .ThenBy(c => c.Channel)
                .First();
            var onCurrent = connection.Band == WifiBand.SixGhz && connection.Channel == pick.Channel;
            parts.Add(onCurrent
                ? $"6 GHz: you're already on the clearest channel ({pick.Channel}, {pick.ApsOnExactChannel} AP(s))."
                : $"6 GHz: channel {pick.Channel} is clearest ({pick.ApsOnExactChannel} AP(s)).");
        }

        return parts.Count == 0 ? string.Empty : string.Join(" | ", parts);
    }

    public static WifiChannelRecommendation BuildChannelRecommendation(
        IReadOnlyList<WifiChannelAnalysis> channelMap,
        WifiConnectionDetails connection,
        DateTimeOffset? generatedAt = null)
    {
        var now = generatedAt ?? DateTimeOffset.Now;
        var current = FindCurrentChannel(channelMap, connection);
        var currentText = FormatCurrentChannel(connection, current);

        if (channelMap is null || channelMap.Count == 0)
        {
            return new WifiChannelRecommendation(
                Severity: "Info",
                Current: currentText,
                Recommended: "No recommendation",
                Reason: "No channel scan data is available yet.",
                Detail: "Run Start Analyzer so the module can measure nearby AP congestion before recommending a channel.",
                GeneratedAt: now);
        }

        if (!connection.IsConnected)
        {
            return new WifiChannelRecommendation(
                Severity: "Info",
                Current: "Not connected",
                Recommended: "No recommendation",
                Reason: "There is no active Wi-Fi connection to optimize.",
                Detail: "Connect to the target Wi-Fi network, then run the analyzer again for channel advice.",
                GeneratedAt: now);
        }

        if (connection.Channel is not { } activeChannel || connection.Band == WifiBand.Unknown)
        {
            return new WifiChannelRecommendation(
                Severity: "Info",
                Current: currentText,
                Recommended: "No recommendation",
                Reason: "The adapter did not report a usable band/channel for the current connection.",
                Detail: "The analyzer can still show visible networks, but channel optimization needs the active channel.",
                GeneratedAt: now);
        }

        return connection.Band switch
        {
            WifiBand.TwoPointFourGhz => Build24GhzRecommendation(channelMap, connection, activeChannel, current, currentText, now),
            WifiBand.FiveGhz => Build5GhzRecommendation(channelMap, connection, activeChannel, current, currentText, now),
            WifiBand.SixGhz => Build6GhzRecommendation(channelMap, connection, activeChannel, current, currentText, now),
            _ => new WifiChannelRecommendation(
                Severity: "Info",
                Current: currentText,
                Recommended: "No recommendation",
                Reason: "The current Wi-Fi band is not recognized.",
                Detail: "No channel change is recommended without a known Wi-Fi band.",
                GeneratedAt: now),
        };
    }

    private static WifiChannelRecommendation Build24GhzRecommendation(
        IReadOnlyList<WifiChannelAnalysis> channelMap,
        WifiConnectionDetails connection,
        int activeChannel,
        WifiChannelAnalysis? current,
        string currentText,
        DateTimeOffset generatedAt)
    {
        var triplet = channelMap
            .Where(c => c.Band == WifiBand.TwoPointFourGhz && c.Channel is 1 or 6 or 11)
            .OrderBy(c => c.ApsOnOverlappingChannels)
            .ThenBy(c => c.Channel)
            .ToList();

        if (triplet.Count == 0)
        {
            return NoChannelCandidateRecommendation(currentText, "No 2.4 GHz channel candidates were available in the scan.", generatedAt);
        }

        var best = triplet.First();
        var recommended = FormatRecommendedChannel(best);
        var activeIsTriplet = activeChannel is 1 or 6 or 11;
        var currentLoad = current?.ApsOnOverlappingChannels;

        if (!activeIsTriplet)
        {
            return new WifiChannelRecommendation(
                Severity: "Warning",
                Current: currentText,
                Recommended: recommended,
                Reason: "2.4 GHz should use non-overlapping channels 1, 6 or 11.",
                Detail: $"Channel {activeChannel} overlaps multiple standard channels; {recommended} is the clearest measured option with {FormatApCount(best.ApsOnOverlappingChannels, overlapping: true)}.",
                GeneratedAt: generatedAt);
        }

        if (activeChannel == best.Channel)
        {
            return new WifiChannelRecommendation(
                Severity: "OK",
                Current: currentText,
                Recommended: "No change",
                Reason: "The current 2.4 GHz channel is already the clearest measured option.",
                Detail: $"{recommended} has {FormatApCount(best.ApsOnOverlappingChannels, overlapping: true)} across nearby overlapping channels.",
                GeneratedAt: generatedAt);
        }

        var severity = currentLoad is not null && currentLoad.Value >= 3 ? "Warning" : "Info";
        var currentLoadText = currentLoad is { } load
            ? FormatApCount(load, overlapping: true)
            : "unknown overlapping AP count";

        return new WifiChannelRecommendation(
            Severity: severity,
            Current: currentText,
            Recommended: recommended,
            Reason: "A quieter 2.4 GHz non-overlapping channel is available.",
            Detail: $"Current channel load is {currentLoadText}; {recommended} measured {FormatApCount(best.ApsOnOverlappingChannels, overlapping: true)}.",
            GeneratedAt: generatedAt);
    }

    private static WifiChannelRecommendation Build5GhzRecommendation(
        IReadOnlyList<WifiChannelAnalysis> channelMap,
        WifiConnectionDetails connection,
        int activeChannel,
        WifiChannelAnalysis? current,
        string currentText,
        DateTimeOffset generatedAt)
    {
        var fiveGhz = channelMap
            .Where(c => c.Band == WifiBand.FiveGhz)
            .ToList();

        if (fiveGhz.Count == 0)
        {
            return NoChannelCandidateRecommendation(currentText, "No 5 GHz channel candidates were available in the scan.", generatedAt);
        }

        var bestNonDfs = fiveGhz
            .Where(c => !c.IsDfsChannel)
            .OrderBy(c => c.ApsOnExactChannel)
            .ThenBy(c => c.Channel)
            .FirstOrDefault();
        var best = bestNonDfs ?? fiveGhz
            .OrderBy(c => c.ApsOnExactChannel)
            .ThenBy(c => c.Channel)
            .First();
        var recommended = FormatRecommendedChannel(best);
        var activeIsDfs = current?.IsDfsChannel ?? WifiFrequencyHelper.IsDfsChannel(connection.Band, activeChannel);

        if (activeChannel == best.Channel)
        {
            return new WifiChannelRecommendation(
                Severity: activeIsDfs ? "Info" : "OK",
                Current: currentText,
                Recommended: "No change",
                Reason: activeIsDfs
                    ? "The current DFS channel is the clearest measured option, but DFS can still move on radar events."
                    : "The current 5 GHz channel is already the clearest measured non-DFS option.",
                Detail: $"{FormatRecommendedChannel(best)} measured {FormatApCount(best.ApsOnExactChannel, overlapping: false)} on the exact channel.",
                GeneratedAt: generatedAt);
        }

        if (activeIsDfs && bestNonDfs is not null)
        {
            return new WifiChannelRecommendation(
                Severity: "Warning",
                Current: currentText,
                Recommended: recommended,
                Reason: "A non-DFS 5 GHz channel is preferred for production stability.",
                Detail: $"DFS channels are valid, but radar detection can force a channel move; {recommended} measured {FormatApCount(best.ApsOnExactChannel, overlapping: false)}.",
                GeneratedAt: generatedAt);
        }

        var severity = current?.CongestionLevel is WifiCongestionLevel.Moderate or WifiCongestionLevel.Heavy
            ? "Warning"
            : "Info";
        var currentLoadText = current is not null
            ? FormatApCount(current.ApsOnExactChannel, overlapping: false)
            : "unknown AP count";

        return new WifiChannelRecommendation(
            Severity: severity,
            Current: currentText,
            Recommended: recommended,
            Reason: "A quieter 5 GHz channel is available in the latest scan.",
            Detail: $"Current channel load is {currentLoadText}; {recommended} measured {FormatApCount(best.ApsOnExactChannel, overlapping: false)}.",
            GeneratedAt: generatedAt);
    }

    private static WifiChannelRecommendation Build6GhzRecommendation(
        IReadOnlyList<WifiChannelAnalysis> channelMap,
        WifiConnectionDetails connection,
        int activeChannel,
        WifiChannelAnalysis? current,
        string currentText,
        DateTimeOffset generatedAt)
    {
        var sixGhz = channelMap
            .Where(c => c.Band == WifiBand.SixGhz)
            .OrderBy(c => c.ApsOnExactChannel)
            .ThenBy(c => c.Channel)
            .ToList();

        if (sixGhz.Count == 0)
        {
            return NoChannelCandidateRecommendation(currentText, "No 6 GHz channel candidates were available in the scan.", generatedAt);
        }

        var best = sixGhz.First();
        var recommended = FormatRecommendedChannel(best);

        if (activeChannel == best.Channel)
        {
            return new WifiChannelRecommendation(
                Severity: "OK",
                Current: currentText,
                Recommended: "No change",
                Reason: "The current 6 GHz channel is already the clearest measured option.",
                Detail: $"{recommended} measured {FormatApCount(best.ApsOnExactChannel, overlapping: false)} on the exact channel.",
                GeneratedAt: generatedAt);
        }

        var severity = current?.CongestionLevel is WifiCongestionLevel.Moderate or WifiCongestionLevel.Heavy
            ? "Warning"
            : "Info";
        var currentLoadText = current is not null
            ? FormatApCount(current.ApsOnExactChannel, overlapping: false)
            : "unknown AP count";

        return new WifiChannelRecommendation(
            Severity: severity,
            Current: currentText,
            Recommended: recommended,
            Reason: "A quieter 6 GHz channel is available in the latest scan.",
            Detail: $"Current channel load is {currentLoadText}; {recommended} measured {FormatApCount(best.ApsOnExactChannel, overlapping: false)}.",
            GeneratedAt: generatedAt);
    }

    private static WifiChannelRecommendation NoChannelCandidateRecommendation(
        string currentText,
        string reason,
        DateTimeOffset generatedAt) =>
        new(
            Severity: "Info",
            Current: currentText,
            Recommended: "No recommendation",
            Reason: reason,
            Detail: "The analyzer does not recommend a channel change without measured candidates for the active band.",
            GeneratedAt: generatedAt);

    private static WifiChannelAnalysis? FindCurrentChannel(
        IReadOnlyList<WifiChannelAnalysis>? channelMap,
        WifiConnectionDetails connection)
    {
        if (channelMap is null || connection.Channel is not { } channel)
        {
            return null;
        }

        return channelMap.FirstOrDefault(c => c.Band == connection.Band && c.Channel == channel);
    }

    private static string FormatCurrentChannel(
        WifiConnectionDetails connection,
        WifiChannelAnalysis? current)
    {
        if (!connection.IsConnected)
        {
            return "Not connected";
        }

        if (connection.Channel is not { } channel || connection.Band == WifiBand.Unknown)
        {
            return "Connected, channel unknown";
        }

        var dfs = current?.IsDfsChannel ?? WifiFrequencyHelper.IsDfsChannel(connection.Band, channel);
        if (current is null)
        {
            return $"{FormatBand(connection.Band)} ch {channel} (not seen in latest scan{(dfs ? ", DFS" : string.Empty)})";
        }

        var count = connection.Band == WifiBand.TwoPointFourGhz
            ? current.ApsOnOverlappingChannels
            : current.ApsOnExactChannel;
        var countText = FormatApCount(count, connection.Band == WifiBand.TwoPointFourGhz);
        return $"{FormatBand(connection.Band)} ch {channel} ({countText}{(dfs ? ", DFS" : string.Empty)})";
    }

    private static string FormatRecommendedChannel(WifiChannelAnalysis channel) =>
        $"{FormatBand(channel.Band)} ch {channel.Channel}";

    private static string FormatBand(WifiBand band) => band switch
    {
        WifiBand.TwoPointFourGhz => "2.4 GHz",
        WifiBand.FiveGhz => "5 GHz",
        WifiBand.SixGhz => "6 GHz",
        _ => "Unknown",
    };

    private static string FormatApCount(int count, bool overlapping)
    {
        var noun = count == 1 ? "AP" : "APs";
        return overlapping ? $"{count} overlapping {noun}" : $"{count} {noun}";
    }

    /// <summary>
    /// Generate a health verdict + score + recommendations for the active connection.
    /// Returns <see cref="WifiHealthReport.NotConnected"/> when no active connection.
    /// </summary>
    public WifiHealthReport GenerateHealthReport(
        WifiConnectionDetails connection,
        IReadOnlyList<WifiChannelAnalysis> channelMap,
        IReadOnlyList<WifiAccessPoint> visibleAps,
        WifiAdapterCapabilities capabilities)
    {
        if (!connection.IsConnected)
        {
            return WifiHealthReport.NotConnected();
        }

        var signalContribution = ScoreSignal(connection.SignalLevel);
        var congestionContribution = ScoreCongestion(connection, channelMap);
        var securityContribution = ScoreSecurity(connection.Security);
        var phyContribution = ScorePhy(connection.PhyType);

        var score = Math.Clamp(
            signalContribution + congestionContribution + securityContribution + phyContribution,
            0, 100);

        var verdict = score switch
        {
            >= 85 => "Excellent",
            >= 70 => "Good",
            >= 50 => "Fair",
            _ => "Poor",
        };

        var recommendations = BuildRecommendations(
            connection, channelMap, visibleAps, capabilities);

        return new WifiHealthReport(
            Score: score,
            Verdict: verdict,
            SignalContribution: signalContribution,
            CongestionContribution: congestionContribution,
            SecurityContribution: securityContribution,
            PhyContribution: phyContribution,
            Recommendations: recommendations,
            GeneratedAt: _time.GetUtcNow().ToLocalTime());
    }

    /// <summary>
    /// Detects roaming — same SSID, different BSSID. Returns null if no roam happened.
    /// State (last BSSID) is owned by the caller (the ViewModel keeps a rolling buffer).
    /// </summary>
    public WifiRoamingEvent? DetectRoaming(
        WifiConnectionDetails previous,
        WifiConnectionDetails current)
    {
        if (!previous.IsConnected || !current.IsConnected) return null;
        if (string.IsNullOrEmpty(previous.Bssid) || string.IsNullOrEmpty(current.Bssid)) return null;
        if (string.IsNullOrEmpty(previous.Ssid) || string.IsNullOrEmpty(current.Ssid)) return null;

        // Roam = same SSID, different BSSID.
        if (!string.Equals(previous.Ssid, current.Ssid, StringComparison.Ordinal)) return null;
        if (string.Equals(previous.Bssid, current.Bssid, StringComparison.OrdinalIgnoreCase)) return null;

        return new WifiRoamingEvent(
            Timestamp: _time.GetUtcNow().ToLocalTime(),
            Ssid: current.Ssid!,
            FromBssid: previous.Bssid!,
            ToBssid: current.Bssid!,
            FromRssiDbm: previous.RssiDbm,
            ToRssiDbm: current.RssiDbm,
            FromChannel: previous.Channel,
            ToChannel: current.Channel);
    }

    // ============== Scoring ==============

    private static int ScoreSignal(WifiSignalLevel level) => level switch
    {
        WifiSignalLevel.Excellent => 40,
        WifiSignalLevel.Good => 30,
        WifiSignalLevel.Fair => 15,
        WifiSignalLevel.Poor => 5,
        _ => 0,
    };

    private static int ScoreCongestion(WifiConnectionDetails connection, IReadOnlyList<WifiChannelAnalysis> channelMap)
    {
        if (connection.Channel is null || connection.Band == WifiBand.Unknown) return 15; // unknown — neutral
        var entry = channelMap.FirstOrDefault(c => c.Band == connection.Band && c.Channel == connection.Channel);
        if (entry is null) return 22;   // no data on this channel — assume light

        return entry.CongestionLevel switch
        {
            WifiCongestionLevel.Free => 30,
            WifiCongestionLevel.Light => 22,
            WifiCongestionLevel.Moderate => 12,
            WifiCongestionLevel.Heavy => 4,
            _ => 15,
        };
    }

    private static int ScoreSecurity(WifiSecurityProfile? security)
    {
        if (security is null) return 0;
        if (security.IsOpen) return 0;
        if (security.HasForwardSecrecy) return 15;       // WPA3 / OWE
        if (security.IsModernSecure) return 12;          // WPA2
        if (security.IsLegacyInsecure && !security.IsOpen) return 5;  // WPA, WEP
        return 8;
    }

    private static int ScorePhy(Dot11PhyType phy) => phy switch
    {
        Dot11PhyType.Eht => 15,             // Wi-Fi 7
        Dot11PhyType.He => 15,              // Wi-Fi 6 / 6E
        Dot11PhyType.Vht => 12,             // Wi-Fi 5
        Dot11PhyType.Ht => 8,               // Wi-Fi 4
        Dot11PhyType.Erp => 3,              // Wi-Fi 3 (g)
        Dot11PhyType.Ofdm or Dot11PhyType.HrDsss or Dot11PhyType.Dsss => 2,
        _ => 5,
    };

    // ============== Recommendations ==============

    /// <summary>
    /// Generates 0..5 actionable recommendations. Order = priority (most actionable first).
    /// Format: stable category prefix + concrete advice. Helpdesk can paste these into
    /// tickets verbatim, and the UI can colour-code them without relying on emoji glyphs.
    /// </summary>
    private static IReadOnlyList<string> BuildRecommendations(
        WifiConnectionDetails connection,
        IReadOnlyList<WifiChannelAnalysis> channelMap,
        IReadOnlyList<WifiAccessPoint> visibleAps,
        WifiAdapterCapabilities capabilities)
    {
        var recs = new List<string>();

        // 1. Signal poor → move closer / check obstructions.
        if (connection.SignalLevel == WifiSignalLevel.Poor && connection.RssiDbm.HasValue)
        {
            recs.Add($"Signal: {connection.RssiDbm} dBm is poor. Move closer to the AP or check obstructions (walls, metal cabinets, microwave ovens).");
        }
        else if (connection.SignalLevel == WifiSignalLevel.Fair && connection.RssiDbm.HasValue)
        {
            recs.Add($"Signal: {connection.RssiDbm} dBm is fair. Consider repositioning the AP or this PC for stronger signal.");
        }

        // 2. Heavy channel congestion → suggest a less-congested channel of the same band.
        if (connection.Channel is { } currentCh && connection.Band != WifiBand.Unknown)
        {
            var currentEntry = channelMap.FirstOrDefault(c => c.Band == connection.Band && c.Channel == currentCh);
            if (currentEntry is { CongestionLevel: WifiCongestionLevel.Heavy or WifiCongestionLevel.Moderate })
            {
                var bestAlt = channelMap
                    .Where(c => c.Band == connection.Band
                                && c.Channel != currentCh
                                && c.CongestionLevel <= WifiCongestionLevel.Light
                                && !c.IsDfsChannel)
                    .OrderBy(c => c.ApsOnOverlappingChannels)
                    .FirstOrDefault();
                if (bestAlt is not null)
                {
                    recs.Add($"Channel: channel {currentCh} has {currentEntry.ApsOnOverlappingChannels} APs ({currentEntry.CongestionLabel.ToLowerInvariant()}). Switch the router to channel {bestAlt.Channel} for less interference.");
                }
            }
        }

        // 3. Legacy security → upgrade.
        if (connection.Security is { IsOpen: true })
        {
            recs.Add("Security: network is OPEN (no encryption). Anyone can intercept traffic; switch to a WPA2 or WPA3 network for sensitive work.");
        }
        else if (connection.Security is { IsLegacyInsecure: true, IsOpen: false })
        {
            recs.Add($"Security: network uses {connection.Security.Label}. Upgrade the AP to WPA3-Personal (or at least WPA2-AES) for forward secrecy.");
        }
        else if (connection.Security is { IsModernSecure: true, HasForwardSecrecy: false })
        {
            recs.Add("Security: network uses WPA2. Upgrade to WPA3 if the AP supports it (forward secrecy + protection against offline brute-force).");
        }

        // 4. PHY mismatch — adapter could do more than what was negotiated.
        if (capabilities.SupportsWifi6E && connection.PhyType == Dot11PhyType.He && connection.Band != WifiBand.SixGhz)
        {
            recs.Add("Capability: adapter supports Wi-Fi 6E (6 GHz). If your AP has a 6 GHz radio, connect to it for less interference and higher throughput.");
        }
        else if (capabilities.SupportsWifi6E && connection.PhyType == Dot11PhyType.Vht)
        {
            recs.Add($"Capability: adapter supports Wi-Fi 6 but connected with Wi-Fi 5 ({connection.PhyFriendlyName}). Check that the AP supports 802.11ax (Wi-Fi 6+) and update its firmware.");
        }
        else if (capabilities.SupportsWifi7 && connection.PhyType != Dot11PhyType.Eht)
        {
            recs.Add($"Capability: adapter supports Wi-Fi 7 (be) but connected at {connection.PhyFriendlyName}. Upgrade the AP to a Wi-Fi 7 model for full speed.");
        }

        // 5. Hidden SSID — does NOT improve security, only inconvenience.
        if (connection.IsHiddenSsid)
        {
            recs.Add("Security: hidden SSID does NOT improve security (BSSID still broadcasts in probe frames). Disable it on the AP unless required by policy.");
        }

        // 6. DFS channel notice (informational, not a recommendation per se).
        if (connection.Channel is { } ch && connection.Band == WifiBand.FiveGhz
            && WifiFrequencyHelper.IsDfsChannel(WifiBand.FiveGhz, ch))
        {
            recs.Add($"DFS: channel {ch} can be vacated by the AP if radar is detected (rare, but possible near airports, ports, or weather-radar zones).");
        }

        // 7. Many APs visible same band — informational health check.
        if (visibleAps.Count >= 30 && connection.Band == WifiBand.TwoPointFourGhz)
        {
            recs.Add($"Density: {visibleAps.Count} APs are visible. Consider 5 GHz or 6 GHz where the spectrum is usually less crowded.");
        }

        // Cap at 5 to keep the UI tidy. Order of recommendations above is the priority order.
        return recs.Count <= 5 ? recs : recs.Take(5).ToList();
    }

    /// <summary>
    /// Default 2.4 GHz channel map shown when no scan has run yet — channels 1, 6, 11 marked Free.
    /// Lets the UI render an empty Channel Map instead of nothing.
    /// </summary>
    private static IReadOnlyList<WifiChannelAnalysis> ReturnDefault24GhzMap() => new[]
    {
        new WifiChannelAnalysis(1, WifiBand.TwoPointFourGhz, 2412, 0, 0, WifiCongestionLevel.Free, false, false),
        new WifiChannelAnalysis(6, WifiBand.TwoPointFourGhz, 2437, 0, 0, WifiCongestionLevel.Free, false, false),
        new WifiChannelAnalysis(11, WifiBand.TwoPointFourGhz, 2462, 0, 0, WifiCongestionLevel.Free, false, false),
    };
}
