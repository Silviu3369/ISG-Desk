// ReSharper disable InconsistentNaming
// ReSharper disable IdentifierTypo

namespace NetScopeDiagnosticCenter.Infrastructure.Wlan;

/// <summary>
/// Native Windows WLAN API enums needed by the P/Invoke layer.
///
/// Source: Win32 wlanapi.h headers (Windows SDK).
/// Names match Win32 conventions exactly so engineers cross-referencing MSDN docs
/// can find them by string match. Fields are not aliased.
///
/// Only the values we actually consume are listed; the unused tail of larger enums
/// (e.g. all 16 wlan_intf_opcode values) is omitted to keep the file readable.
/// </summary>

#pragma warning disable CA1707 // identifiers contain underscores — required to match Win32 names exactly

/// <summary>
/// State of the WLAN interface — see WLAN_INTERFACE_STATE in wlanapi.h.
/// We use this to skip interfaces that are turned off / not ready before scanning.
/// </summary>
public enum WlanInterfaceState : uint
{
    NotReady = 0,
    Connected = 1,
    AdHocNetworkFormed = 2,
    Disconnecting = 3,
    Disconnected = 4,
    Associating = 5,
    Discovering = 6,
    Authenticating = 7,
}

/// <summary>
/// 802.11 PHY type — encoded by Windows in WLAN_BSS_ENTRY.dot11BssPhyType.
/// We map this to a friendly "Wi-Fi 4 / 5 / 6 / 6E" string for the UI.
/// </summary>
public enum Dot11PhyType : uint
{
    Unknown = 0,     // == native dot11_phy_type_unknown / _any (both 0); we only use Unknown
    Fhss = 1,        // Frequency-hopping spread spectrum (802.11)
    Dsss = 2,        // Direct-sequence spread spectrum (802.11)
    IrBaseband = 3,
    Ofdm = 4,        // 802.11a (5 GHz)
    HrDsss = 5,      // 802.11b
    Erp = 6,         // 802.11g
    Ht = 7,          // 802.11n  (Wi-Fi 4)
    Vht = 8,         // 802.11ac (Wi-Fi 5)
    Dmg = 9,         // 802.11ad (60 GHz)
    He = 10,         // 802.11ax (Wi-Fi 6 / 6E)
    Eht = 11,        // 802.11be (Wi-Fi 7) — Win11 24H2+
}

/// <summary>
/// 802.11 BSS type — Infrastructure (AP-based) vs Independent (ad-hoc).
/// We only care about Infrastructure for helpdesk diagnostics; Independent is filtered out.
/// </summary>
public enum Dot11BssType : uint
{
    Infrastructure = 1,
    Independent = 2,
    Any = 3,
}

/// <summary>
/// Authentication algorithm reported in WLAN_AVAILABLE_NETWORK / WLAN_BSS_ENTRY.
/// We translate this to a human-readable security label ("WPA2-PSK", "WPA3-Personal", etc.)
/// </summary>
public enum Dot11AuthAlgorithm : uint
{
    Open = 1,
    SharedKey = 2,
    Wpa = 3,
    WpaPsk = 4,
    WpaNone = 5,
    Rsna = 6,         // WPA2-Enterprise
    RsnaPsk = 7,      // WPA2-Personal
    Wpa3 = 8,         // WPA3-Enterprise
    Wpa3Sae = 9,      // WPA3-Personal
    Owe = 10,         // Opportunistic Wireless Encryption (Enhanced Open)
    Wpa3Ent192 = 12,
    IhvStart = 0x80000000,
    IhvEnd = 0xFFFFFFFF,
}

/// <summary>
/// Cipher algorithm used to encrypt traffic. Used together with auth alg to label security.
/// </summary>
public enum Dot11CipherAlgorithm : uint
{
    None = 0,
    Wep40 = 1,
    Tkip = 2,
    Ccmp = 4,         // AES-CCMP (WPA2)
    Wep104 = 5,
    BipCmac128 = 6,
    Gcmp = 8,         // GCMP-128 (WPA3)
    Gcmp256 = 9,      // GCMP-256 (WPA3 192-bit suite)
    BipGmac128 = 11,
    BipGmac256 = 12,
    BipCmac256 = 13,
    Wpa = 0x00000050,
    Wep = 0x00000101,
    IhvStart = 0x80000000,
    IhvEnd = 0xFFFFFFFF,
}

// (WlanConnectionMode enum removed — it was only consumed by the deleted
//  WLAN_CONNECTION_ATTRIBUTES marshalling struct; the connectionMode field is skipped
//  by the manual-offset reader since the app never displays it.)

/// <summary>
/// The ONLY three WLAN_INTF_OPCODE values this app queries. The native enum has ~25
/// members; unused ones were removed to keep the surface honest (a dead enum member is
/// just noise that implies a capability we don't have).
/// </summary>
public enum WlanIntfOpcode : uint
{
    /// <summary>wlan_intf_opcode_current_connection — SSID/BSSID/PHY/rates/security.</summary>
    CurrentConnection = 7,

    /// <summary>wlan_intf_opcode_channel_number — the connected channel.</summary>
    ChannelNumber = 8,

    // CRITICAL: wlan_intf_opcode_rssi is NOT 14. In wlanapi.h the RSSI opcode lives in
    // the "MSM" opcode group (wlan_intf_opcode_msm_start = 0x10000100), at offset +2:
    //   wlan_intf_opcode_msm_start  = 0x10000100
    //   wlan_intf_opcode_statistics = 0x10000101
    //   wlan_intf_opcode_rssi       = 0x10000102
    // Using 14 (which is wlan_intf_opcode_certified_safe_mode on modern SDKs) returned 0,
    // which is why "Signal: 0 dBm" + empty RSSI sparkline. Verified empirically: the
    // 0x10000102 opcode returns a valid dBm (e.g. -35) on real hardware.
    Rssi = 0x10000102,
}

#pragma warning restore CA1707
