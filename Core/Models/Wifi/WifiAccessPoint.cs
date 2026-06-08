using NetScopeDiagnosticCenter.Infrastructure.Wlan;

namespace NetScopeDiagnosticCenter.Core.Models.Wifi;

/// <summary>
/// Domain model for one access point detected by the Wi-Fi analyzer.
/// </summary>
public sealed record WifiAccessPoint(
    string Ssid,
    bool IsHidden,
    string Bssid,
    string? Vendor,
    int Channel,
    int CenterFrequencyMhz,
    int? ChannelWidthMhz,
    WifiBand Band,
    Dot11PhyType PhyType,
    int RssiDbm,
    WifiSignalLevel SignalLevel,
    int LinkQualityPercent,
    WifiSecurityProfile Security,
    bool IsCurrentConnection,
    DateTimeOffset FirstSeenAt,
    DateTimeOffset LastSeenAt,
    int? AverageRssiDbm = null,
    int? DetectionPercent = null)
{
    public string PhyFriendlyName => WifiPhyStandard.ToFriendlyName(PhyType, Band);

    public string PhyShortLabel => WifiPhyStandard.ToShortLabel(PhyType);

    public string ChannelLabel => $"Ch {Channel}";

    public string ChannelWidthLabel => ChannelWidthMhz is { } width ? $"{width} MHz" : "Unknown";

    public string BandLabel => Band switch
    {
        WifiBand.TwoPointFourGhz => "2.4 GHz",
        WifiBand.FiveGhz => "5 GHz",
        WifiBand.SixGhz => "6 GHz",
        _ => "Unknown",
    };

    public string SignalText => $"{RssiDbm} dBm";

    public string AverageRssiText => AverageRssiDbm is { } rssi ? $"{rssi} dBm" : "No sample";

    public string DetectionPercentText => DetectionPercent is { } percent
        ? $"{Math.Clamp(percent, 0, 100)}%"
        : "No sample";

    public string VendorDisplay => string.IsNullOrWhiteSpace(Vendor) ? "Unknown" : Vendor!;

    public string SignalColor => SignalLevel switch
    {
        WifiSignalLevel.Excellent => "#10B981",
        WifiSignalLevel.Good => "#3B82F6",
        WifiSignalLevel.Fair => "#F59E0B",
        WifiSignalLevel.Poor => "#EF4444",
        _ => "#94A3B8",
    };

    public double SignalBarWidth
    {
        get
        {
            var clamped = Math.Clamp(RssiDbm, -90, -30);
            return (clamped + 90) / 60.0 * 72.0;
        }
    }

    public string DisplaySsid => IsHidden || string.IsNullOrWhiteSpace(Ssid) ? "<hidden>" : Ssid;
}
