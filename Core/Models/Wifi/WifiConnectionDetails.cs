using NetScopeDiagnosticCenter.Infrastructure.Wlan;

namespace NetScopeDiagnosticCenter.Core.Models.Wifi;

/// <summary>
/// Snapshot of the active Wi-Fi connection — drives Section 1 (Connection card) of the
/// Wi-Fi Analyzer page. Combines L2 (WLAN driver) + L3 (IP config) data into one
/// object the UI binds to.
///
/// Created when:
///  <list type="bullet">
///   <item>Page load (one-shot snapshot)</item>
///   <item>WlanScan completes (refresh)</item>
///   <item>Live monitor cycle (cheap re-query of RSSI / link rates)</item>
///  </list>
///
/// Properties null-friendly: missing fields surface as null rather than empty strings,
/// so the UI can display "—" or skip the row instead of "Unknown".
/// </summary>
public sealed record WifiConnectionDetails(
    bool IsConnected,
    string? Ssid,
    bool IsHiddenSsid,
    string? Bssid,
    string? Vendor,
    int? Channel,
    int? CenterFrequencyMhz,
    int? ChannelWidthMhz,
    WifiBand Band,
    Dot11PhyType PhyType,

    /// <summary>RSSI in dBm at snapshot time. -100..-30 typical.</summary>
    int? RssiDbm,
    WifiSignalLevel SignalLevel,
    int? SignalQualityPercent,

    /// <summary>Receive link rate in Mbps (PHY rate, not throughput).</summary>
    double? RxRateMbps,
    /// <summary>Transmit link rate in Mbps.</summary>
    double? TxRateMbps,

    WifiSecurityProfile? Security,

    /// <summary>Profile name on this PC (typically equals SSID for personal networks).</summary>
    string? ProfileName,

    WlanInterfaceState InterfaceState,

    // ---------- L3 / IP configuration (filled by NetIpConfigurationProbe) ----------
    string? Ipv4Address,
    string? Ipv4SubnetMask,
    string? Ipv4Gateway,
    IReadOnlyList<string>? DnsServers,
    string? AdapterMacAddress,
    string? AdapterDescription,

    // ---------- Public IP / ISP (opt-in lookup) ----------
    /// <summary>External IP returned by the opt-in lookup. Null if user hasn't requested it.</summary>
    string? PublicIpAddress,

    /// <summary>ISP / org name (e.g. "RCS &amp; RDS S.A."). Null if not requested or unknown.</summary>
    string? IspName,

    /// <summary>City/country from the lookup, displayed as e.g. "Bucharest, RO".</summary>
    string? IspLocation,

    DateTimeOffset CapturedAt)
{
    /// <summary>Friendly Wi-Fi generation ("Wi-Fi 6 (ax)") for the connection card.</summary>
    public string PhyFriendlyName => WifiPhyStandard.ToFriendlyName(PhyType, Band);

    /// <summary>"Channel 36 (5180 MHz, 80 MHz)" — used in the connection card row.</summary>
    public string ChannelDescription
    {
        get
        {
            if (Channel is null) return "—";
            var freq = CenterFrequencyMhz is { } mhz ? $" ({mhz} MHz" : string.Empty;
            var width = ChannelWidthMhz is { } w ? $", {w} MHz)" : (CenterFrequencyMhz is null ? string.Empty : ")");
            return $"Ch {Channel}{freq}{width}";
        }
    }

    /// <summary>DNS servers as a compact comma-joined string for the Connection card.</summary>
    public string DnsServersText =>
        DnsServers is { Count: > 0 } ? string.Join(", ", DnsServers) : "—";

    /// <summary>Just the centre frequency, e.g. "5220 MHz" — separate row in the card.</summary>
    public string FrequencyText =>
        CenterFrequencyMhz is { } mhz ? $"{mhz} MHz" : "—";

    /// <summary>Empty placeholder used when no Wi-Fi adapter is connected.</summary>
    public static WifiConnectionDetails Disconnected(WlanInterfaceState state) => new(
        IsConnected: false,
        Ssid: null, IsHiddenSsid: false, Bssid: null, Vendor: null,
        Channel: null, CenterFrequencyMhz: null, ChannelWidthMhz: null,
        Band: WifiBand.Unknown, PhyType: Dot11PhyType.Unknown,
        RssiDbm: null, SignalLevel: WifiSignalLevel.Unknown, SignalQualityPercent: null,
        RxRateMbps: null, TxRateMbps: null,
        Security: null, ProfileName: null, InterfaceState: state,
        Ipv4Address: null, Ipv4SubnetMask: null, Ipv4Gateway: null, DnsServers: null,
        AdapterMacAddress: null, AdapterDescription: null,
        PublicIpAddress: null, IspName: null, IspLocation: null,
        CapturedAt: DateTimeOffset.Now);
}
