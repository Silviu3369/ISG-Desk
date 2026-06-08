using NetScopeDiagnosticCenter.Core.Models.Wifi;

namespace NetScopeDiagnosticCenter.Collectors.Wifi;

/// <summary>
/// Abstraction over Wi-Fi scanning. The only implementation is
/// <see cref="WlanApiScanner"/> (P/Invoke over wlanapi.dll).
///
/// <para>
/// There is intentionally NO netsh fallback. An earlier netsh-based scanner was removed
/// because it could only ever surface the *connected* network (netsh exposes no neighbour
/// BSS list), so it implied a working degraded mode that did not actually exist. When the
/// WLAN AutoConfig service is unavailable, <see cref="IsAvailable"/> returns false and the
/// scanner cleanly yields empty results; the UI then shows an honest "WLAN unavailable"
/// state rather than a misleading partial one. The interface abstraction is kept so the
/// scanner can be substituted in unit tests.
/// </para>
/// </summary>
public interface IWifiScanner : IDisposable
{
    bool IsAvailable { get; }

    /// <summary>Returns the GUID of the first available Wi-Fi adapter, or null if none.</summary>
    Guid? GetPrimaryAdapterId();

    /// <summary>Snapshot of the active connection (or null if disconnected / no adapter).</summary>
    WifiConnectionDetails? GetCurrentConnection(Guid adapterId);

    /// <summary>Trigger an active scan + wait for cache refresh (4-5s typical).</summary>
    Task<bool> ForceScanAsync(Guid adapterId, CancellationToken cancellationToken);

    /// <summary>Read the current scan-cache (does NOT trigger a fresh scan).</summary>
    IReadOnlyList<WifiAccessPoint> GetVisibleAccessPoints(Guid adapterId);

    /// <summary>Read saved Wi-Fi profile names from the WLAN service.</summary>
    IReadOnlyList<string> GetSavedProfiles(Guid adapterId);

    /// <summary>Forget one saved Wi-Fi profile by name. Caller must confirm intent first.</summary>
    bool ForgetSavedProfile(Guid adapterId, string profileName);

    /// <summary>
    /// Read the current RSSI dBm. Cheap (~1ms) — safe to call at 1Hz from <c>WifiSampler</c>.
    /// Null when not connected.
    /// </summary>
    int? GetCurrentRssiDbm(Guid adapterId);
}
