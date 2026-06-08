namespace NetScopeDiagnosticCenter.Core.Models.Wifi;

/// <summary>
/// One roaming-event entry in Section 10 (Roaming history).
///
/// Detected by polling the current BSSID at 1Hz; whenever the BSSID changes for the SAME
/// SSID, we emit one event with before/after RSSI. Used by helpdesk to diagnose:
/// <list type="bullet">
///   <item>Aggressive roaming (many flips per minute → laptop hops between weak signals)</item>
///   <item>Sticky client (stays on AP at -85 dBm when -50 dBm AP is available)</item>
///   <item>Asymmetric handoff (RSSI drop after roam → moved to weaker AP)</item>
/// </list>
///
/// Stored in a bounded ring buffer (last 50 events).
/// </summary>
public readonly record struct WifiRoamingEvent(
    DateTimeOffset Timestamp,
    string Ssid,
    string FromBssid,
    string ToBssid,
    int? FromRssiDbm,
    int? ToRssiDbm,
    int? FromChannel,
    int? ToChannel)
{
    /// <summary>"Stronger" / "Weaker" / "Same" tag for at-a-glance roaming-quality.</summary>
    public string DeltaLabel
    {
        get
        {
            if (FromRssiDbm is null || ToRssiDbm is null) return "Unknown";
            var delta = ToRssiDbm.Value - FromRssiDbm.Value;
            if (delta > 5) return "Stronger";
            if (delta < -5) return "Weaker";
            return "Same";
        }
    }
}
