namespace NetScopeDiagnosticCenter.Infrastructure.Wlan;

/// <summary>
/// Testable abstraction over the raw WLAN P/Invoke layer. Implementations either
/// wrap <c>wlanapi.dll</c> (production = <see cref="WlanApiNative"/>) or feed
/// canned data (tests). Methods return raw native structs (or wrapped DTOs that
/// shape closely to native), NOT domain models — domain mapping happens one
/// layer up in <c>Collectors/Wifi</c>.
///
/// All methods are synchronous because WLAN API is itself synchronous + fast
/// (no network I/O — kernel just reads cached state). The only exception is
/// <see cref="ScanAsync"/> which models the inherent 4-5s delay between
/// triggering an active scan and the cache being refreshed.
///
/// Lifetime: the interface itself does not own the WLAN handle. Implementations
/// own + dispose it via <see cref="IDisposable"/>.
/// </summary>
public interface IWlanApi : IDisposable
{
    /// <summary>
    /// Returns true if the implementation can talk to the WLAN service.
    /// False means the service is stopped, the adapter is missing, or
    /// permissions are denied — caller should fall back to NetshScanner
    /// or surface a "Wi-Fi unavailable" message.
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Enumerate Wi-Fi adapters installed on this PC. Empty list when none present.
    /// </summary>
    IReadOnlyList<WlanInterfaceSnapshot> EnumerateInterfaces();

    /// <summary>
    /// Get the current connection details (SSID, BSSID, security, link rates) for the
    /// adapter, or null if not connected / adapter missing.
    /// </summary>
    WlanCurrentConnectionSnapshot? QueryCurrentConnection(Guid interfaceGuid);

    /// <summary>
    /// Read the current RSSI in dBm for the connected BSSID. Returns null if not connected.
    /// Cheap (~&lt;1ms); safe to call at 1Hz for live monitoring.
    /// </summary>
    int? QueryCurrentRssiDbm(Guid interfaceGuid);

    /// <summary>
    /// Read the current channel number (centre channel for 40+ MHz). Null if not connected.
    /// </summary>
    int? QueryCurrentChannel(Guid interfaceGuid);

    /// <summary>
    /// Trigger an active scan + wait for the cache to be refreshed. Typical wait
    /// is 4-5 seconds. Caller should show a loading overlay during this period.
    /// </summary>
    /// <returns>True if the kernel accepted the scan request, false otherwise.</returns>
    Task<bool> ScanAsync(Guid interfaceGuid, CancellationToken cancellationToken);

    /// <summary>
    /// Read the current scan-cache list of visible BSSIDs without triggering a fresh scan.
    /// Includes hidden SSIDs (returned with empty SSID byte array — caller should map to
    /// "&lt;hidden&gt;" or similar for display).
    /// </summary>
    IReadOnlyList<WlanBssSnapshot> GetVisibleBssEntries(Guid interfaceGuid);

    /// <summary>
    /// Read the saved Wi-Fi profile names for the adapter. Used by the read-only
    /// profile listing section of the UI.
    /// </summary>
    IReadOnlyList<string> GetProfileNames(Guid interfaceGuid);

    /// <summary>
    /// Delete one saved Wi-Fi profile from the adapter. Returns false when the WLAN
    /// service rejects the request or the profile does not exist.
    /// </summary>
    bool DeleteProfile(Guid interfaceGuid, string profileName);
}

/// <summary>Snapshot of one Wi-Fi adapter at the time of EnumerateInterfaces.</summary>
public sealed record WlanInterfaceSnapshot(
    Guid InterfaceGuid,
    string Description,
    WlanInterfaceState State);

/// <summary>
/// Decoded current-connection result. SSID is decoded UTF-8 (with fallback to ASCII for
/// non-UTF-8 SSIDs — Windows tolerates this so we do too).
/// </summary>
public sealed record WlanCurrentConnectionSnapshot(
    string Ssid,
    string ProfileName,
    string Bssid,                   // colon-separated 6 bytes, e.g. "AA:BB:CC:DD:EE:FF"
    Dot11PhyType PhyType,
    int SignalQualityPercent,
    long RxRateBitsPerSecond,
    long TxRateBitsPerSecond,
    Dot11AuthAlgorithm AuthAlgorithm,
    Dot11CipherAlgorithm CipherAlgorithm,
    bool SecurityEnabled,
    bool OneXEnabled,
    WlanInterfaceState InterfaceState);

/// <summary>
/// Decoded BSS entry. Mirrors WLAN_BSS_ENTRY but with friendlier types
/// (string SSID/BSSID, dBm int, MHz int).
/// </summary>
public sealed record WlanBssSnapshot(
    string Ssid,                    // empty string when SSID is hidden
    bool IsHidden,
    string Bssid,
    Dot11PhyType PhyType,
    int RssiDbm,
    int LinkQualityPercent,
    int CenterFrequencyKhz,         // raw; channel + width derived in Engine
    Dot11BssType BssType,
    ushort CapabilityInformation,

    /// <summary>
    /// Operating channel width in MHz (20 / 40 / 80 / 160), derived by parsing the BSS
    /// beacon HT / VHT / HE operation information elements. Null only when the IE blob
    /// could not be read (truncated / out-of-bounds driver reply). Defaults to null so
    /// existing test fixtures that don't supply it keep compiling.
    /// </summary>
    int? ChannelWidthMhz = null);
