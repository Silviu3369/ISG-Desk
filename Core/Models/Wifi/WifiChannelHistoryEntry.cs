namespace NetScopeDiagnosticCenter.Core.Models.Wifi;

public sealed record WifiChannelHistoryEntry(
    DateTimeOffset Timestamp,
    string? Ssid,
    string? Bssid,
    WifiBand Band,
    int Channel,
    int? RssiDbm,
    int? LinkScore,
    int ExactApCount,
    int OverlappingApCount,
    WifiCongestionLevel CongestionLevel,
    bool IsDfsChannel,
    string RecommendationSeverity,
    string Recommendation)
{
    public string TimeDisplay => Timestamp.ToString("HH:mm:ss");

    public string NetworkDisplay => string.IsNullOrWhiteSpace(Ssid)
        ? "Hidden or unknown SSID"
        : Ssid;

    public string BssidDisplay => string.IsNullOrWhiteSpace(Bssid)
        ? "BSSID unknown"
        : Bssid;

    public string BandLabel => Band switch
    {
        WifiBand.TwoPointFourGhz => "2.4 GHz",
        WifiBand.FiveGhz => "5 GHz",
        WifiBand.SixGhz => "6 GHz",
        _ => "Unknown",
    };

    public string ChannelDisplay => $"{BandLabel} ch {Channel}";

    public string RssiDisplay => RssiDbm is { } rssi ? $"{rssi} dBm" : "Not measured";

    public string LinkScoreDisplay => LinkScore is { } score ? $"{score}/100" : "Not scored";

    public int ComparisonLoad => Band == WifiBand.TwoPointFourGhz
        ? OverlappingApCount
        : ExactApCount;

    public string LoadDisplay
    {
        get
        {
            var count = ComparisonLoad;
            var noun = count == 1 ? "AP" : "APs";
            return Band == WifiBand.TwoPointFourGhz
                ? $"{count} overlapping {noun}"
                : $"{count} {noun}";
        }
    }

    public string DfsDisplay => Band == WifiBand.FiveGhz
        ? (IsDfsChannel ? "DFS" : "Non-DFS")
        : "-";

    public string RecommendationDisplay => string.IsNullOrWhiteSpace(Recommendation)
        ? "No recommendation"
        : Recommendation;

    public string RecommendationSeverityDisplay => string.IsNullOrWhiteSpace(RecommendationSeverity)
        ? "Info"
        : RecommendationSeverity;

    public string CompactDisplay => $"{ChannelDisplay}, {RssiDisplay}, {LinkScoreDisplay}, {LoadDisplay}";
}
