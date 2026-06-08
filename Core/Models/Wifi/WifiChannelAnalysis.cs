namespace NetScopeDiagnosticCenter.Core.Models.Wifi;

/// <summary>
/// Per-channel analysis result: one row in the Channel Congestion table.
/// </summary>
public sealed record WifiChannelAnalysis(
    int Channel,
    WifiBand Band,
    int CenterFrequencyMhz,
    int ApsOnExactChannel,
    int ApsOnOverlappingChannels,
    WifiCongestionLevel CongestionLevel,
    bool IsDfsChannel,
    bool IsCurrentConnectionChannel)
{
    public const int CongestionMeterMaximum = 8;
    public int CongestionMeterMax => CongestionMeterMaximum;

    public string ChannelLabel => $"Ch {Channel}";

    public string CongestionLabel => CongestionLevel switch
    {
        WifiCongestionLevel.Free => "Free",
        WifiCongestionLevel.Light => "Light",
        WifiCongestionLevel.Moderate => "Moderate",
        WifiCongestionLevel.Heavy => "Heavy",
        _ => "Unknown",
    };

    public string BandLabel => Band switch
    {
        WifiBand.TwoPointFourGhz => "2.4 GHz",
        WifiBand.FiveGhz => "5 GHz",
        WifiBand.SixGhz => "6 GHz",
        _ => "Unknown",
    };

    public string CongestionColor => CongestionLevel switch
    {
        WifiCongestionLevel.Free => "#10B981",
        WifiCongestionLevel.Light => "#3B82F6",
        WifiCongestionLevel.Moderate => "#F59E0B",
        WifiCongestionLevel.Heavy => "#EF4444",
        _ => "#94A3B8",
    };

    public int CongestionMeterValue => Math.Clamp(ApsOnOverlappingChannels, 0, CongestionMeterMaximum);

    public string ApCountText
    {
        get
        {
            var suffix = ApsOnOverlappingChannels == 1 ? "AP" : "APs";
            return $"{ApsOnOverlappingChannels} {suffix}";
        }
    }

    public string ExactCountText => $"{ApsOnExactChannel} exact";
}
