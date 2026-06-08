using System.Collections.Concurrent;
using NetScopeDiagnosticCenter.Core.Models.Wifi;
using NetScopeDiagnosticCenter.Core.Wifi;
using NetScopeDiagnosticCenter.Infrastructure.Wlan;

namespace NetScopeDiagnosticCenter.Collectors.Wifi;

/// <summary>
/// Production <see cref="IWifiScanner"/> backed by the Win32 WLAN API via <see cref="IWlanApi"/>.
///
/// <para>
/// Responsibilities:
/// <list type="bullet">
///   <item>Translate raw WLAN snapshots (<see cref="WlanBssSnapshot"/>, <see cref="WlanCurrentConnectionSnapshot"/>)
///         into domain models (<see cref="WifiAccessPoint"/>, <see cref="WifiConnectionDetails"/>).</item>
///   <item>Derive channel + band from centre frequency via <see cref="WifiFrequencyHelper"/>.</item>
///   <item>Look up vendor name from BSSID OUI via <see cref="WifiOuiLookup"/>.</item>
///   <item>Wrap auth/cipher into <see cref="WifiSecurityProfile"/>.</item>
///   <item>Maintain a per-BSSID first-seen timestamp dictionary so the UI can show
///         "First seen 5 minutes ago".</item>
/// </list>
/// </para>
///
/// <para>
/// Lifecycle: this scanner does NOT own the <see cref="IWlanApi"/> — the DI container does.
/// Disposing the scanner is a no-op. Owner controls when WLAN handle closes.
/// </para>
/// </summary>
public sealed class WlanApiScanner : IWifiScanner
{
    private readonly IWlanApi _wlan;
    private readonly TimeProvider _time;

    /// <summary>
    /// Tracks first-seen timestamp per BSSID for this session. ConcurrentDictionary because
    /// GetVisibleAccessPoints runs on a thread-pool thread (Task.Run from the VM) and a
    /// manual Rescan + the 30 s auto-rescan could otherwise touch it without synchronisation.
    /// </summary>
    private readonly ConcurrentDictionary<string, DateTimeOffset> _firstSeen = new(StringComparer.OrdinalIgnoreCase);

    private bool _disposed;

    public WlanApiScanner(IWlanApi wlan, TimeProvider? time = null)
    {
        _wlan = wlan ?? throw new ArgumentNullException(nameof(wlan));
        _time = time ?? TimeProvider.System;
    }

    public bool IsAvailable => !_disposed && _wlan.IsAvailable;

    public Guid? GetPrimaryAdapterId()
    {
        if (!IsAvailable) return null;

        // Prefer adapters that are connected; fall back to the first one if none are.
        // Skips adapters in the "NotReady" state (radio off / driver issue).
        var interfaces = _wlan.EnumerateInterfaces();
        if (interfaces.Count == 0) return null;

        var connected = interfaces.FirstOrDefault(i => i.State == WlanInterfaceState.Connected);
        if (connected is not null) return connected.InterfaceGuid;

        var ready = interfaces.FirstOrDefault(i => i.State != WlanInterfaceState.NotReady);
        return ready?.InterfaceGuid ?? interfaces[0].InterfaceGuid;
    }

    public WifiConnectionDetails? GetCurrentConnection(Guid adapterId)
    {
        if (!IsAvailable) return null;

        var snap = _wlan.QueryCurrentConnection(adapterId);
        if (snap is null) return WifiConnectionDetails.Disconnected(WlanInterfaceState.Disconnected);

        // Resolve channel + band from the centre frequency. The current_connection opcode
        // doesn't return channel directly — we query it separately.
        var channel = _wlan.QueryCurrentChannel(adapterId);
        var rssiDbm = _wlan.QueryCurrentRssiDbm(adapterId);

        // Derive band + frequency from the channel number directly. We DELIBERATELY
        // do not enumerate the BSS list here — that's a heavy call (~10 ms) and prone
        // to native marshaling crashes if any single BSS entry has an unexpected layout.
        // BSS enumeration is reserved for explicit Force Scan via GetVisibleAccessPoints.
        var (band, freqMhz) = channel is { } ch
            ? ChannelToBandAndFreq(ch)
            : (WifiBand.Unknown, (int?)null);

        // Pull real L3 facts (IPv4 / mask / gateway / DNS / MAC) straight from the BCL.
        // No more "Probed by Quick Diagnosis" placeholder — this is always available
        // whenever the adapter is connected.
        var ip = WifiAdapterInfo.GetActiveWifi(adapterId);

        return new WifiConnectionDetails(
            IsConnected: true,
            Ssid: string.IsNullOrEmpty(snap.Ssid) ? null : snap.Ssid,
            IsHiddenSsid: string.IsNullOrEmpty(snap.Ssid),
            Bssid: snap.Bssid,
            Vendor: WifiOuiLookup.DescribeVendor(snap.Bssid),
            Channel: channel,
            CenterFrequencyMhz: freqMhz,
            ChannelWidthMhz: null,                // not directly available from current_connection
            Band: band,
            PhyType: snap.PhyType,
            RssiDbm: rssiDbm,
            SignalLevel: WifiFrequencyHelper.ClassifySignal(rssiDbm),
            SignalQualityPercent: snap.SignalQualityPercent,
            RxRateMbps: snap.RxRateBitsPerSecond > 0 ? snap.RxRateBitsPerSecond / 1_000_000.0 : null,
            TxRateMbps: snap.TxRateBitsPerSecond > 0 ? snap.TxRateBitsPerSecond / 1_000_000.0 : null,
            Security: WifiSecurityProfile.From(snap.AuthAlgorithm, snap.CipherAlgorithm, snap.SecurityEnabled),
            ProfileName: string.IsNullOrEmpty(snap.ProfileName) ? null : snap.ProfileName,
            InterfaceState: snap.InterfaceState,
            Ipv4Address: ip.Ipv4Address,
            Ipv4SubnetMask: ip.Ipv4SubnetMask,
            Ipv4Gateway: ip.Ipv4Gateway,
            DnsServers: ip.DnsServers,
            AdapterMacAddress: ip.MacAddress,
            AdapterDescription: ip.Description,
            // Public IP / ISP filled by WifiPublicIpProbe on opt-in only.
            PublicIpAddress: null, IspName: null, IspLocation: null,
            CapturedAt: _time.GetUtcNow().ToLocalTime());
    }

    public async Task<bool> ForceScanAsync(Guid adapterId, CancellationToken cancellationToken)
    {
        if (!IsAvailable) return false;
        return await _wlan.ScanAsync(adapterId, cancellationToken).ConfigureAwait(false);
    }

    public IReadOnlyList<WifiAccessPoint> GetVisibleAccessPoints(Guid adapterId)
    {
        if (!IsAvailable) return Array.Empty<WifiAccessPoint>();

        var raw = _wlan.GetVisibleBssEntries(adapterId);
        if (raw.Count == 0) return Array.Empty<WifiAccessPoint>();

        // Need the current connection to mark which BSS is "us" for the IsCurrentConnection flag.
        var current = _wlan.QueryCurrentConnection(adapterId);
        var currentBssid = current?.Bssid;

        var now = _time.GetUtcNow().ToLocalTime();
        var result = new List<WifiAccessPoint>(raw.Count);

        foreach (var bss in raw)
        {
            if (bss.BssType != Dot11BssType.Infrastructure) continue;     // skip ad-hoc
            if (string.IsNullOrEmpty(bss.Bssid)) continue;                // defensive

            var (band, channel, freqMhz) = WifiFrequencyHelper.FromFrequencyKhz(bss.CenterFrequencyKhz);
            // First-seen tracking — atomically set on initial encounter, kept thereafter.
            var firstSeenAt = _firstSeen.GetOrAdd(bss.Bssid, now);

            // A scanned beacon only exposes the 802.11 "Privacy" capability bit — we CANNOT
            // tell WPA2 vs WPA3 vs Enterprise from it. Report honestly as "Secured"/"Open"
            // (the old code wrongly stamped every secured AP as "WPA2-Enterprise").
            var hasPrivacy = (bss.CapabilityInformation & 0x0010) != 0;
            var approximateSecurity = WifiSecurityProfile.FromBeacon(hasPrivacy);

            result.Add(new WifiAccessPoint(
                Ssid: bss.IsHidden ? string.Empty : bss.Ssid,
                IsHidden: bss.IsHidden,
                Bssid: bss.Bssid,
                Vendor: WifiOuiLookup.DescribeVendor(bss.Bssid),
                Channel: channel,
                CenterFrequencyMhz: freqMhz,
                ChannelWidthMhz: bss.ChannelWidthMhz,        // derived from HT/VHT/HE beacon IEs
                Band: band,
                PhyType: bss.PhyType,
                RssiDbm: bss.RssiDbm,
                SignalLevel: WifiFrequencyHelper.ClassifySignal(bss.RssiDbm),
                LinkQualityPercent: bss.LinkQualityPercent,
                Security: approximateSecurity,
                IsCurrentConnection: !string.IsNullOrEmpty(currentBssid)
                    && string.Equals(bss.Bssid, currentBssid, StringComparison.OrdinalIgnoreCase),
                FirstSeenAt: firstSeenAt,
                LastSeenAt: now));
        }

        // Sort by RSSI (strongest first) so the DataGrid shows the most relevant APs at top.
        result.Sort((a, b) => b.RssiDbm.CompareTo(a.RssiDbm));
        return result;
    }

    public IReadOnlyList<string> GetSavedProfiles(Guid adapterId)
    {
        if (!IsAvailable) return Array.Empty<string>();
        return _wlan.GetProfileNames(adapterId);
    }

    public bool ForgetSavedProfile(Guid adapterId, string profileName)
    {
        if (!IsAvailable || string.IsNullOrWhiteSpace(profileName)) return false;
        return _wlan.DeleteProfile(adapterId, profileName);
    }

    public int? GetCurrentRssiDbm(Guid adapterId)
    {
        if (!IsAvailable) return null;
        return _wlan.QueryCurrentRssiDbm(adapterId);
    }

    public void Dispose()
    {
        // We don't own _wlan — owned by DI container. Just flip the disposed flag so
        // IsAvailable starts returning false. We deliberately do NOT clear _firstSeen:
        // GetVisibleAccessPoints may still be iterating it on a scan thread-pool thread,
        // and clearing it concurrently would throw InvalidOperationException. The whole
        // object is about to be GC'd anyway, so the dictionary needs no manual cleanup.
        _disposed = true;
    }

    /// <summary>
    /// Derive band + centre frequency (in MHz) from a Wi-Fi channel number alone.
    /// 6 GHz channels collide with 2.4 GHz numbering in IEEE 802.11ax — we can't
    /// distinguish 6 GHz channel 5 from 2.4 GHz channel 5 without the centre frequency,
    /// so we default low numbers to 2.4 GHz. The Wi-Fi 6E case is recovered later when
    /// the Force-Scan path runs (which has access to the real centre frequency).
    /// </summary>
    private static (WifiBand Band, int? FrequencyMhz) ChannelToBandAndFreq(int channel) => channel switch
    {
        >= 1 and <= 13 => (WifiBand.TwoPointFourGhz, 2407 + 5 * channel),
        14 => (WifiBand.TwoPointFourGhz, 2484),
        >= 36 and <= 177 => (WifiBand.FiveGhz, 5000 + 5 * channel),
        _ => (WifiBand.Unknown, (int?)null),
    };
}
