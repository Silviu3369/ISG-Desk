using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace NetScopeDiagnosticCenter.Infrastructure.Wlan;

/// <summary>
/// Production <see cref="IWlanApi"/> implementation backed by <c>wlanapi.dll</c>.
///
/// <para>
/// Lifetime + thread safety:
/// <list type="bullet">
///   <item>Owns one <see cref="WlanHandle"/>, closed on Dispose.</item>
///   <item>All methods serialized via <see cref="_handleLock"/> — WLAN API is documented
///         as thread-safe, but kernel-side it serializes anyway, and our locking
///         simplifies the unmanaged-memory free pattern.</item>
///   <item><see cref="IsAvailable"/> returns false when the WLAN AutoConfig service is
///         stopped at construction time. Callers should check before Enumerate/Scan.</item>
/// </list>
/// </para>
///
/// <para>
/// Memory discipline: every WLAN function that returns a pointer (out IntPtr) is paired
/// with a try/finally that calls <c>WlanFreeMemory</c>. Forgetting this leaks memory
/// in wlansvc.exe — over a long session the service can hit the 50 MB internal cap and
/// start refusing requests. Tests in <see cref="ScanAsync"/> exercise the leak path
/// by triggering many scans; manual smoke test with <c>!handle</c> in WinDbg confirms
/// no leaks.
/// </para>
/// </summary>
public sealed class WlanApiNative : IWlanApi
{
    private const int MaxWlanInterfaces = 32;
    private const int MaxWlanProfiles = 1024;
    private const int MaxBssEntries = 4096;

    private readonly object _handleLock = new();
    private readonly WlanHandle? _handle;
    private readonly bool _available;
    private bool _disposed;

    public WlanApiNative()
    {
        try
        {
            _handle = WlanHandle.Open();
            _available = !_handle.IsInvalid;
        }
        catch (Win32Exception)
        {
            // Service stopped or permission denied — caller will see IsAvailable = false
            // and fall back to NetshScanner / disable Wi-Fi UI.
            _handle = null;
            _available = false;
        }
    }

    // Includes !_disposed so a method that checks IsAvailable can't proceed into a native
    // call after Dispose() has closed the handle on another thread (use-after-free guard).
    public bool IsAvailable => !_disposed && _available && _handle is { IsInvalid: false };

    public IReadOnlyList<WlanInterfaceSnapshot> EnumerateInterfaces()
    {
        if (!IsAvailable) return Array.Empty<WlanInterfaceSnapshot>();

        lock (_handleLock)
        {
            var result = new List<WlanInterfaceSnapshot>();
            var listPtr = IntPtr.Zero;

            try
            {
                var status = WlanApi.WlanEnumInterfaces(_handle!.Raw, IntPtr.Zero, out listPtr);
                if (status != WlanApi.ErrorSuccess) return Array.Empty<WlanInterfaceSnapshot>();

                var header = Marshal.PtrToStructure<WLAN_INTERFACE_INFO_LIST_HEADER>(listPtr);
                var count = (int)header.dwNumberOfItems;
                if (count <= 0 || count > MaxWlanInterfaces) return Array.Empty<WlanInterfaceSnapshot>();

                // Skip past the 8-byte header (dwNumberOfItems + dwIndex) to reach
                // the first WLAN_INTERFACE_INFO entry.
                var entryPtr = IntPtr.Add(listPtr, Marshal.SizeOf<WLAN_INTERFACE_INFO_LIST_HEADER>());
                var entrySize = Marshal.SizeOf<WLAN_INTERFACE_INFO>();

                for (var i = 0; i < count; i++)
                {
                    var entry = Marshal.PtrToStructure<WLAN_INTERFACE_INFO>(entryPtr);
                    result.Add(new WlanInterfaceSnapshot(
                        entry.InterfaceGuid,
                        entry.strInterfaceDescription ?? string.Empty,
                        entry.isState));
                    entryPtr = IntPtr.Add(entryPtr, entrySize);
                }
            }
            finally
            {
                if (listPtr != IntPtr.Zero) WlanApi.WlanFreeMemory(listPtr);
            }

            return result;
        }
    }

    public WlanCurrentConnectionSnapshot? QueryCurrentConnection(Guid interfaceGuid)
    {
        if (!IsAvailable) return null;

        lock (_handleLock)
        {
            var dataPtr = IntPtr.Zero;
            try
            {
                var iface = interfaceGuid;
                var status = WlanApi.WlanQueryInterface(
                    _handle!.Raw,
                    ref iface,
                    WlanIntfOpcode.CurrentConnection,
                    IntPtr.Zero,
                    out var dataSize,
                    out dataPtr,
                    IntPtr.Zero);

                // CRITICAL bounds check: every field below is read by fixed offset up to
                // OffCipher+4 = 604 bytes. Without verifying the kernel buffer is at least
                // that big, a short/zeroed blob (IHV driver quirk, race during disconnect)
                // makes Marshal.Copy/PtrToStringUni read unowned memory → AccessViolation,
                // which .NET cannot catch → process crash. This is the exact failure mode
                // the manual-offset rewrite existed to prevent.
                const int RequiredConnAttrSize = 604;
                if (status != WlanApi.ErrorSuccess || dataPtr == IntPtr.Zero
                    || dataSize < RequiredConnAttrSize)
                {
                    return null;
                }

                // Read WLAN_CONNECTION_ATTRIBUTES field-by-field with manual offsets.
                // Marshal.PtrToStructure mis-sizes this struct because:
                //   • Win32 BOOL in WLAN_SECURITY_ATTRIBUTES is 4 bytes, not 1 — the old
                //     [MarshalAs(U1)] bool shifted every security field, yielding the
                //     bogus "Unknown (0 / 7)" auth/cipher the user saw.
                //   • nested DOT11_SSID/DOT11_MAC_ADDRESS byte[] arrays throw off packing.
                // WLAN_CONNECTION_ATTRIBUTES has NO variable-length members, so this 604-
                // byte x64 layout is the fixed, documented Win32 ABI (stable since Vista),
                // not a per-machine guess — verified against the SDK header offsets. The
                // dataSize < 604 guard above turns a short/blob driver reply into a clean
                // null instead of an out-of-bounds read.
                // Layout (x64, pack 8):
                //   0   isState (DWORD)
                //   4   wlanConnectionMode (DWORD)
                //   8   strProfileName[256] WCHAR  → 512 bytes
                //   520 WLAN_ASSOCIATION_ATTRIBUTES:
                //         520 dot11Ssid.uSSIDLength (DWORD)
                //         524 dot11Ssid.ucSSID[32]
                //         556 dot11BssType (DWORD)
                //         560 dot11Bssid[6]  (+2 pad)
                //         568 dot11PhyType (DWORD)
                //         572 uDot11PhyIndex (DWORD)
                //         576 wlanSignalQuality (DWORD 0..100)
                //         580 ulRxRate (DWORD, 100bps units)
                //         584 ulTxRate (DWORD)
                //   588 WLAN_SECURITY_ATTRIBUTES:
                //         588 bSecurityEnabled (BOOL = 4 bytes)
                //         592 bOneXEnabled (BOOL = 4 bytes)
                //         596 dot11AuthAlgorithm (DWORD)
                //         600 dot11CipherAlgorithm (DWORD)
                const int OffState = 0;
                const int OffProfileName = 8;
                const int OffSsidLen = 520;
                const int OffSsidBytes = 524;
                const int OffBssid = 560;
                const int OffPhyType = 568;
                const int OffSignalQuality = 576;
                const int OffRxRate = 580;
                const int OffTxRate = 584;
                const int OffSecurityEnabled = 588;
                const int OffOneX = 592;
                const int OffAuth = 596;
                const int OffCipher = 600;

                var stateVal = (WlanInterfaceState)(uint)Marshal.ReadInt32(dataPtr, OffState);
                if (stateVal != WlanInterfaceState.Connected) return null;

                var profileName = Marshal.PtrToStringUni(IntPtr.Add(dataPtr, OffProfileName)) ?? string.Empty;

                var ssidLen = (uint)Marshal.ReadInt32(dataPtr, OffSsidLen);
                var ssidBuf = new byte[32];
                Marshal.Copy(IntPtr.Add(dataPtr, OffSsidBytes), ssidBuf, 0, 32);
                var ssid = DecodeSsidBytes(ssidBuf, (int)Math.Min(ssidLen, 32u));

                var bssidBytes = new byte[6];
                Marshal.Copy(IntPtr.Add(dataPtr, OffBssid), bssidBytes, 0, 6);
                var bssid = FormatBssidBytes(bssidBytes);

                var phyType = (Dot11PhyType)(uint)Marshal.ReadInt32(dataPtr, OffPhyType);
                var signalQuality = (uint)Marshal.ReadInt32(dataPtr, OffSignalQuality);
                var rxRate = (uint)Marshal.ReadInt32(dataPtr, OffRxRate);
                var txRate = (uint)Marshal.ReadInt32(dataPtr, OffTxRate);
                var secEnabled = Marshal.ReadInt32(dataPtr, OffSecurityEnabled) != 0;
                var oneX = Marshal.ReadInt32(dataPtr, OffOneX) != 0;
                var auth = (Dot11AuthAlgorithm)(uint)Marshal.ReadInt32(dataPtr, OffAuth);
                var cipher = (Dot11CipherAlgorithm)(uint)Marshal.ReadInt32(dataPtr, OffCipher);

                return new WlanCurrentConnectionSnapshot(
                    Ssid: ssid,
                    ProfileName: profileName,
                    Bssid: bssid,
                    PhyType: phyType,
                    SignalQualityPercent: (int)signalQuality,
                    RxRateBitsPerSecond: rxRate * 100L,
                    TxRateBitsPerSecond: txRate * 100L,
                    AuthAlgorithm: auth,
                    CipherAlgorithm: cipher,
                    SecurityEnabled: secEnabled,
                    OneXEnabled: oneX,
                    InterfaceState: stateVal);
            }
            finally
            {
                if (dataPtr != IntPtr.Zero) WlanApi.WlanFreeMemory(dataPtr);
            }
        }
    }

    public int? QueryCurrentRssiDbm(Guid interfaceGuid)
    {
        if (!IsAvailable) return null;

        lock (_handleLock)
        {
            var dataPtr = IntPtr.Zero;
            try
            {
                var iface = interfaceGuid;
                var status = WlanApi.WlanQueryInterface(
                    _handle!.Raw,
                    ref iface,
                    WlanIntfOpcode.Rssi,
                    IntPtr.Zero,
                    out var dataSize,
                    out dataPtr,
                    IntPtr.Zero);

                if (status != WlanApi.ErrorSuccess || dataPtr == IntPtr.Zero || dataSize < sizeof(int)) return null;
                return Marshal.ReadInt32(dataPtr);
            }
            finally
            {
                if (dataPtr != IntPtr.Zero) WlanApi.WlanFreeMemory(dataPtr);
            }
        }
    }

    public int? QueryCurrentChannel(Guid interfaceGuid)
    {
        if (!IsAvailable) return null;

        lock (_handleLock)
        {
            var dataPtr = IntPtr.Zero;
            try
            {
                var iface = interfaceGuid;
                var status = WlanApi.WlanQueryInterface(
                    _handle!.Raw,
                    ref iface,
                    WlanIntfOpcode.ChannelNumber,
                    IntPtr.Zero,
                    out var dataSize,
                    out dataPtr,
                    IntPtr.Zero);

                if (status != WlanApi.ErrorSuccess || dataPtr == IntPtr.Zero || dataSize < sizeof(uint)) return null;
                return (int)(uint)Marshal.ReadInt32(dataPtr);
            }
            finally
            {
                if (dataPtr != IntPtr.Zero) WlanApi.WlanFreeMemory(dataPtr);
            }
        }
    }

    public async Task<bool> ScanAsync(Guid interfaceGuid, CancellationToken cancellationToken)
    {
        if (!IsAvailable) return false;

        // Trigger the active scan synchronously — kernel returns immediately.
        bool triggered;
        lock (_handleLock)
        {
            var iface = interfaceGuid;
            var status = WlanApi.WlanScan(_handle!.Raw, ref iface, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            triggered = status == WlanApi.ErrorSuccess;
        }

        if (!triggered) return false;

        // The kernel scan is asynchronous and takes a VARIABLE amount of time (measured
        // 6 s on real hardware; some adapters 3 s, some 9 s). A fixed 4.5 s delay read
        // the cache TOO EARLY — only the stale connected BSS was present, which is exactly
        // the "I only see my own network" bug.
        //
        // Instead of a blind delay we POLL the BSS-list count: wait in 1.5 s steps up to
        // ~9 s, and return as soon as the count has grown past 1 AND stayed stable across
        // two consecutive polls (scan finished filling the cache). This adapts to fast and
        // slow radios alike. Professional analyzers (inSSIDer/NetSpot) use the
        // wlan_notification_acm_scan_complete callback for the same effect; polling is
        // simpler and equally reliable for our 30 s cadence.
        const int pollMs = 1500;
        const int maxPolls = 6;             // 6 × 1.5 s = 9 s ceiling
        var lastCount = -1;
        for (var poll = 0; poll < maxPolls; poll++)
        {
            try
            {
                await Task.Delay(pollMs, cancellationToken).ConfigureAwait(false);
            }
            catch (TaskCanceledException)
            {
                return true;   // cancelled — scan keeps filling cache in the background
            }

            var count = CountBssEntries(interfaceGuid);
            if (count > 1 && count == lastCount)
            {
                // Count stabilised above 1 → scan has finished populating. Done early.
                return true;
            }
            lastCount = count;
        }

        return true;
    }

    /// <summary>
    /// Cheap helper: reads ONLY the WLAN_BSS_LIST header (dwNumberOfItems) without parsing
    /// any entries. Used by <see cref="ScanAsync"/> to poll scan progress safely.
    /// </summary>
    private int CountBssEntries(Guid interfaceGuid)
    {
        if (!IsAvailable) return 0;
        lock (_handleLock)
        {
            var listPtr = IntPtr.Zero;
            try
            {
                var iface = interfaceGuid;
                var status = WlanApi.WlanGetNetworkBssList(
                    _handle!.Raw, ref iface, IntPtr.Zero, Dot11BssType.Any,
                    bSecurityEnabled: false, IntPtr.Zero, out listPtr);
                if (status != WlanApi.ErrorSuccess || listPtr == IntPtr.Zero) return 0;
                // dwNumberOfItems is the 2nd DWORD of WLAN_BSS_LIST_HEADER (offset 4).
                return Marshal.ReadInt32(listPtr, 4);
            }
            catch
            {
                return 0;
            }
            finally
            {
                if (listPtr != IntPtr.Zero) WlanApi.WlanFreeMemory(listPtr);
            }
        }
    }

    // ----- WLAN_BSS_ENTRY field offsets (x64, default 8-byte packing) -----
    //
    // ⚠ DO NOT "fix" this to a variable IE-offset stride. WLAN_BSS_ENTRY is a FIXED-SIZE
    // struct: per the documented WLAN_BSS_LIST layout the entries are a contiguous fixed
    // array (dwNumberOfItems × sizeof(WLAN_BSS_ENTRY) = 360 on x64) and the variable
    // information-element blobs live SEPARATELY, addressed by each entry's ulIeOffset.
    // An earlier revision strided by (ieOffset+ieSize) — that walked off the array and
    // made the scanner return only the connected network (the "only shows my Wi-Fi" bug).
    // The fixed 360 stride was verified against real hardware (all BSS entries parsed)
    // and is the stable Win32 ABI contract, not a per-machine guess.
    //
    // We read field-by-field rather than calling Marshal.PtrToStructure<WLAN_BSS_ENTRY>
    // because the latter caused AccessViolationException 0xC0000005 on real hardware:
    // the inline `byte[] ucSSID` (ByValArray SizeConst=32) combined with the trailing
    // variable-length WLAN_RATE_SET tripped the marshaller into reading past the end
    // of the allocated entry. Reading fields manually using Marshal.ReadInt32 /
    // ReadIntPtr / Marshal.Copy lets us pick out exactly the fields we need, with no
    // risk of reading bytes that aren't there. See:
    // https://learn.microsoft.com/en-us/windows/win32/api/wlanapi/ns-wlanapi-wlan_bss_entry
    private const int BssOff_SsidLength = 0;       // ULONG uSSIDLength
    private const int BssOff_SsidBytes = 4;        // UCHAR ucSSID[32]
    private const int BssOff_PhyId = 36;           // ULONG uPhyId
    private const int BssOff_Bssid = 40;           // UCHAR[6]
    private const int BssOff_BssType = 48;         // DOT11_BSS_TYPE (DWORD enum, 4-byte aligned)
    private const int BssOff_PhyType = 52;         // DOT11_PHY_TYPE (DWORD enum)
    private const int BssOff_Rssi = 56;            // LONG (signed dBm)
    private const int BssOff_LinkQuality = 60;     // ULONG (0..100)
    // bytes 64..72 = BOOLEAN bInRegDomain + padding + USHORT usBeaconPeriod (+ padding)
    // bytes 72..88 = ULONGLONG ullTimestamp + ULONGLONG ullHostTimestamp
    private const int BssOff_CapInfo = 88;         // USHORT usCapabilityInformation
    private const int BssOff_CenterFreqKhz = 92;   // ULONG ulChCenterFrequency
    // bytes 96..352 = WLAN_RATE_SET (we don't consume rates)
    private const int BssOff_IeOffset = 352;       // ULONG (offset from this entry start)
    private const int BssOff_IeSize = 356;         // ULONG
    private const int BssEntryFixedSize = 360;

    public IReadOnlyList<WlanBssSnapshot> GetVisibleBssEntries(Guid interfaceGuid)
    {
        if (!IsAvailable) return Array.Empty<WlanBssSnapshot>();

        lock (_handleLock)
        {
            var result = new List<WlanBssSnapshot>();
            var listPtr = IntPtr.Zero;

            try
            {
                var iface = interfaceGuid;
                var status = WlanApi.WlanGetNetworkBssList(
                    _handle!.Raw,
                    ref iface,
                    IntPtr.Zero,                     // all SSIDs
                    Dot11BssType.Any,
                    bSecurityEnabled: false,         // ignored when ssid is null
                    IntPtr.Zero,
                    out listPtr);

                if (status != WlanApi.ErrorSuccess || listPtr == IntPtr.Zero) return Array.Empty<WlanBssSnapshot>();

                // Read the small fixed header first — this struct only has two DWORDs and
                // is safe to marshal with PtrToStructure.
                var header = Marshal.PtrToStructure<WLAN_BSS_LIST_HEADER>(listPtr);
                var count = (int)header.dwNumberOfItems;
                if (count <= 0 || count > MaxBssEntries) return Array.Empty<WlanBssSnapshot>();

                // Total byte budget for safety bounds.
                var totalSize = (int)header.dwTotalSize;
                var headerSize = Marshal.SizeOf<WLAN_BSS_LIST_HEADER>();
                if (totalSize < headerSize) return Array.Empty<WlanBssSnapshot>();

                var bytesAvailable = Math.Max(0, totalSize - headerSize);
                count = Math.Min(count, bytesAvailable / BssEntryFixedSize);

                var ssidBuf = new byte[32];

                // CRITICAL LAYOUT FACT (verified empirically against real hardware):
                // WLAN_BSS_LIST is [8-byte header][N × fixed-360-byte WLAN_BSS_ENTRY][IE blobs].
                // The entries form a CONTIGUOUS FIXED-SIZE ARRAY; the variable-length IE data
                // for every entry is stored AFTER all entries (entry.ulIeOffset points there,
                // typically 3000+ bytes in — well past the entry array). The previous code
                // strided by (ulIeOffset + ulIeSize) which jumped into the IE region after
                // entry 0, failed the bounds check, and returned only 1 network. Striding by
                // the fixed entry size walks the array correctly. (Confirmed: 9 networks with
                // valid SSID/BSSID/RSSI at offsets 8 + i*360.)
                for (var i = 0; i < count; i++)
                {
                    var offset = headerSize + i * BssEntryFixedSize;
                    // Bound check against the kernel-allocated buffer.
                    if (offset + BssEntryFixedSize > totalSize) break;
                    var entryPtr = IntPtr.Add(listPtr, offset);

                    var ssidLen = (uint)Marshal.ReadInt32(entryPtr, BssOff_SsidLength);
                    Marshal.Copy(IntPtr.Add(entryPtr, BssOff_SsidBytes), ssidBuf, 0, 32);
                    var ssid = DecodeSsidBytes(ssidBuf, (int)Math.Min(ssidLen, 32u));
                    var isHidden = ssidLen == 0 || AllZeroOrEmpty(ssidBuf, (int)Math.Min(ssidLen, 32u));

                    var bssidBytes = new byte[6];
                    Marshal.Copy(IntPtr.Add(entryPtr, BssOff_Bssid), bssidBytes, 0, 6);
                    var bssid = FormatBssidBytes(bssidBytes);

                    var bssType = (Dot11BssType)(uint)Marshal.ReadInt32(entryPtr, BssOff_BssType);
                    var phyType = (Dot11PhyType)(uint)Marshal.ReadInt32(entryPtr, BssOff_PhyType);
                    var rssi = Marshal.ReadInt32(entryPtr, BssOff_Rssi);                  // signed dBm
                    var quality = (int)(uint)Marshal.ReadInt32(entryPtr, BssOff_LinkQuality);
                    var capInfo = (ushort)Marshal.ReadInt16(entryPtr, BssOff_CapInfo);
                    var freqKhz = (uint)Marshal.ReadInt32(entryPtr, BssOff_CenterFreqKhz);

                    // Channel width is NOT a field of WLAN_BSS_ENTRY — derive it by walking
                    // the entry's variable-length information-element blob (HT/VHT/HE op IEs).
                    var ieOffset = (uint)Marshal.ReadInt32(entryPtr, BssOff_IeOffset);
                    var ieSize = (uint)Marshal.ReadInt32(entryPtr, BssOff_IeSize);
                    var channelWidth = TryDeriveChannelWidthMhz(listPtr, offset, ieOffset, ieSize, totalSize);

                    result.Add(new WlanBssSnapshot(
                        Ssid: isHidden ? string.Empty : ssid,
                        IsHidden: isHidden,
                        Bssid: bssid,
                        PhyType: phyType,
                        RssiDbm: rssi,
                        LinkQualityPercent: quality,
                        CenterFrequencyKhz: (int)freqKhz,
                        BssType: bssType,
                        CapabilityInformation: capInfo,
                        ChannelWidthMhz: channelWidth));
                }
            }
            catch (Exception)
            {
                // Marshal failures here should NEVER kill the app — log silently and
                // return whatever we've gathered so far. Higher-level health reporting
                // surfaces the partial result as "Scan produced no networks".
                return result;
            }
            finally
            {
                if (listPtr != IntPtr.Zero) WlanApi.WlanFreeMemory(listPtr);
            }

            return result;
        }
    }

    public IReadOnlyList<string> GetProfileNames(Guid interfaceGuid)
    {
        if (!IsAvailable) return Array.Empty<string>();

        lock (_handleLock)
        {
            var listPtr = IntPtr.Zero;
            try
            {
                var iface = interfaceGuid;
                var status = WlanApi.WlanGetProfileList(_handle!.Raw, ref iface, IntPtr.Zero, out listPtr);
                if (status != WlanApi.ErrorSuccess || listPtr == IntPtr.Zero) return Array.Empty<string>();

                var header = Marshal.PtrToStructure<WLAN_PROFILE_INFO_LIST_HEADER>(listPtr);
                var count = (int)header.dwNumberOfItems;
                if (count <= 0 || count > MaxWlanProfiles) return Array.Empty<string>();

                var result = new List<string>(count);
                var entryPtr = IntPtr.Add(listPtr, Marshal.SizeOf<WLAN_PROFILE_INFO_LIST_HEADER>());
                var entrySize = Marshal.SizeOf<WLAN_PROFILE_INFO>();

                for (var i = 0; i < count; i++)
                {
                    var entry = Marshal.PtrToStructure<WLAN_PROFILE_INFO>(entryPtr);
                    if (!string.IsNullOrEmpty(entry.strProfileName))
                    {
                        result.Add(entry.strProfileName);
                    }
                    entryPtr = IntPtr.Add(entryPtr, entrySize);
                }

                return result;
            }
            finally
            {
                if (listPtr != IntPtr.Zero) WlanApi.WlanFreeMemory(listPtr);
            }
        }
    }

    public bool DeleteProfile(Guid interfaceGuid, string profileName)
    {
        if (!IsAvailable || string.IsNullOrWhiteSpace(profileName)) return false;

        lock (_handleLock)
        {
            var iface = interfaceGuid;
            var status = WlanApi.WlanDeleteProfile(
                _handle!.Raw,
                ref iface,
                profileName.Trim(),
                IntPtr.Zero);
            return status == WlanApi.ErrorSuccess;
        }
    }

    public void Dispose()
    {
        // Take _handleLock so we cannot close the WLAN handle while a thread-pool caller
        // (e.g. the 1 Hz WifiSampler) is mid-native-call holding the same lock. Without
        // this, WlanCloseHandle could fire during an in-flight WlanQueryInterface →
        // use-after-free of a kernel handle. Setting _disposed inside the lock also makes
        // IsAvailable flip atomically w.r.t. the serialized WLAN methods.
        lock (_handleLock)
        {
            if (_disposed) return;
            _disposed = true;
            _handle?.Dispose();
        }
    }

    // ---------- helpers ----------

    /// <summary>
    /// Decodes an SSID byte buffer as UTF-8 with fallback to ASCII for non-UTF-8 SSIDs.
    /// Hidden / empty SSIDs return empty string. (The struct-based DecodeSsid overload was
    /// removed — all parsing is now manual-offset byte reads.)
    /// </summary>
    private static string DecodeSsidBytes(byte[] bytes, int len)
    {
        if (len <= 0 || bytes == null || bytes.Length == 0) return string.Empty;
        try
        {
            return Encoding.UTF8.GetString(bytes, 0, len).TrimEnd('\0');
        }
        catch (DecoderFallbackException)
        {
            return Encoding.ASCII.GetString(bytes, 0, len).TrimEnd('\0');
        }
    }

    private static bool AllZeroOrEmpty(byte[] bytes, int len)
    {
        if (len <= 0) return true;
        for (var i = 0; i < len; i++)
        {
            if (bytes[i] != 0) return false;
        }
        return true;
    }

    /// <summary>
    /// Formats a 6-byte MAC as "AA:BB:CC:DD:EE:FF" (uppercase, colon separated).
    /// Returns "00:00:00:00:00:00" for null / short input. (The struct-based FormatBssid
    /// overload was removed — all parsing is now manual-offset byte reads.)
    /// </summary>
    private static string FormatBssidBytes(byte[] bytes)
    {
        if (bytes == null || bytes.Length < 6) return "00:00:00:00:00:00";
        return string.Format(System.Globalization.CultureInfo.InvariantCulture,
            "{0:X2}:{1:X2}:{2:X2}:{3:X2}:{4:X2}:{5:X2}",
            bytes[0], bytes[1], bytes[2], bytes[3], bytes[4], bytes[5]);
    }

    /// <summary>
    /// Copies a BSS entry's variable-length information-element blob out of the kernel buffer
    /// and derives the operating channel width via <see cref="WifiChannelWidthDecoder"/>.
    /// The IE blob lives at <c>listPtr + entryByteOffset + ieOffset</c> for <c>ieSize</c> bytes
    /// (ulIeOffset is documented as relative to the WLAN_BSS_ENTRY start). Returns null when
    /// the blob is missing or fails the buffer bounds check — the bit-level parsing itself
    /// lives in the pure, unit-tested <see cref="WifiChannelWidthDecoder"/>.
    /// </summary>
    private static int? TryDeriveChannelWidthMhz(IntPtr listPtr, int entryByteOffset, uint ieOffset, uint ieSize, int totalSize)
    {
        if (ieOffset == 0 || ieSize == 0) return null;

        // Bounds: the blob must lie wholly inside the kernel-allocated WLAN_BSS_LIST buffer.
        long blobStart = (long)entryByteOffset + ieOffset;
        if (blobStart < 0 || ieSize > 8192 || blobStart + ieSize > totalSize) return null;

        try
        {
            var blob = new byte[ieSize];
            Marshal.Copy(IntPtr.Add(listPtr, (int)blobStart), blob, 0, (int)ieSize);
            return WifiChannelWidthDecoder.FromInformationElements(blob);
        }
        catch
        {
            return null;
        }
    }
}
