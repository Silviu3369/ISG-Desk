namespace NetScopeDiagnosticCenter.Core.Models.Wifi;

/// <summary>
/// Wi-Fi band as derived from the centre frequency.
///
/// Boundaries (per IEEE 802.11):
///   2.4 GHz: 2400–2500 MHz  (channels 1–14)
///   5 GHz:   5150–5895 MHz  (channels 36–177)
///   6 GHz:   5925–7125 MHz  (channels 1–233 in U-NII-5..U-NII-8 — Wi-Fi 6E only)
/// </summary>
public enum WifiBand
{
    Unknown = 0,
    TwoPointFourGhz = 1,
    FiveGhz = 2,
    SixGhz = 3,
}

/// <summary>
/// Channel congestion bucket used to colour the channel-map UI and drive recommendations.
/// Thresholds are calibrated for typical office Wi-Fi (helpdesk context):
///   Free:     0 APs on channel
///   Light:    1–2 APs
///   Moderate: 3–4 APs
///   Heavy:    5+ APs
/// 2.4 GHz uses overlapping-channel counts (a channel-1 AP "leaks" onto 2,3,4,5).
/// 5/6 GHz uses direct counts only (channels are non-overlapping at 20 MHz).
/// </summary>
public enum WifiCongestionLevel
{
    Free = 0,
    Light = 1,
    Moderate = 2,
    Heavy = 3,
}

/// <summary>
/// Signal-strength bucket. Mirrors <c>WifiSignalClassifier</c> labels but in a value type
/// so it can travel through DTOs / be bound to a Foreground brush.
///
/// Thresholds (dBm):
///   Excellent: &gt;= -65
///   Good:      -65 .. -75
///   Fair:      -75 .. -85
///   Poor:      &lt; -85
///   Unknown:   no measurement
/// </summary>
public enum WifiSignalLevel
{
    Unknown = 0,
    Poor = 1,
    Fair = 2,
    Good = 3,
    Excellent = 4,
}
