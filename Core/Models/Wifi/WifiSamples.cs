namespace NetScopeDiagnosticCenter.Core.Models.Wifi;

/// <summary>
/// One sample point in the live RSSI sparkline (Section 5: Strength monitor).
///
/// Produced at 1Hz by <c>WifiSampler</c> while the user is on the Wi-Fi page.
/// Stored in a bounded rolling buffer (last 60 samples = 60 seconds).
/// </summary>
public readonly record struct WifiSignalSample(
    DateTimeOffset Timestamp,
    int RssiDbm,
    WifiSignalLevel Level);

/// <summary>
/// One sample point for the live speed monitors (Section 6).
///
/// <para>
/// Carries TWO different things, deliberately kept separate because they answer
/// different questions:
/// </para>
/// <list type="bullet">
///   <item>
///     <b><see cref="RxRateMbps"/> / <see cref="TxRateMbps"/></b> — the PHY rate
///     negotiated with the AP (the radio's link speed). Nearly constant; shown in the
///     small "Link Speed (PHY rate)" card. NOT real throughput.
///   </item>
///   <item>
///     <b><see cref="RxThroughputMbps"/> / <see cref="TxThroughputMbps"/></b> — the
///     ACTUAL traffic moving over the adapter this second, derived from the NIC byte
///     counters (Δbytes × 8 ÷ Δt). This is what a human means by "speed": ~0 when idle,
///     spikes when you download/upload. Drives the two big throughput monitors.
///   </item>
/// </list>
/// </summary>
public readonly record struct WifiSpeedSample(
    DateTimeOffset Timestamp,
    double RxRateMbps,
    double TxRateMbps,
    double RxThroughputMbps = 0,
    double TxThroughputMbps = 0);
