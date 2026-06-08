using System.Runtime.InteropServices;

// ReSharper disable InconsistentNaming
// ReSharper disable IdentifierTypo
// ReSharper disable FieldCanBeMadeReadOnly.Global

namespace NetScopeDiagnosticCenter.Infrastructure.Wlan;

/// <summary>
/// Native Windows WLAN API structures the P/Invoke layer ACTUALLY marshals.
///
/// <para>
/// Only the small, fixed-size header/entry structs that are safe to pass to
/// <see cref="Marshal.PtrToStructure{T}"/> live here. The rich variable-length structs
/// (WLAN_BSS_ENTRY, WLAN_CONNECTION_ATTRIBUTES and their nested DOT11_SSID /
/// DOT11_MAC_ADDRESS / WLAN_RATE_SET / WLAN_ASSOCIATION_ATTRIBUTES /
/// WLAN_SECURITY_ATTRIBUTES) were DELETED on purpose:
/// </para>
/// <list type="bullet">
///   <item>They are no longer marshalled — <see cref="WlanApiNative"/> reads those blobs
///         field-by-field with explicit byte offsets (Marshal.ReadInt32 / Copy /
///         PtrToStringUni) because the inline <c>byte[]</c> ByValArray + trailing
///         variable-length rate set tripped the marshaller into reading past the
///         allocation → AccessViolationException 0xC0000005 (process crash).</item>
///   <item>Keeping the struct definitions around was a footgun: a future edit could
///         innocently call <c>Marshal.PtrToStructure&lt;WLAN_BSS_ENTRY&gt;</c> and
///         reintroduce the exact crash the manual-offset rewrite eliminated.</item>
/// </list>
///
/// <para>
/// The authoritative native layout used to derive the manual offsets is documented inline
/// in <see cref="WlanApiNative"/> (see the BssOff_* constants and the
/// QueryCurrentConnection offset table). Source: Win32 wlanapi.h / wlantypes.h (SDK 10).
/// </para>
/// </summary>

#pragma warning disable CA1707 // identifiers contain underscores — match Win32 names exactly

/// <summary>
/// One entry in WLAN_INTERFACE_INFO_LIST. Identifies a single Wi-Fi adapter.
/// Fixed-size + only blittable/string fields → safe for PtrToStructure.
/// </summary>
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
public struct WLAN_INTERFACE_INFO
{
    public Guid InterfaceGuid;

    /// <summary>Human-readable name shown in Network Connections (e.g. "Wi-Fi", "Wi-Fi 2").</summary>
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
    public string strInterfaceDescription;

    public WlanInterfaceState isState;
}

/// <summary>
/// Header of WLAN_INTERFACE_INFO_LIST. Native is a variable-length struct ending in
/// InterfaceInfo[1]; we marshal just this 8-byte header then walk the trailing
/// fixed-size <see cref="WLAN_INTERFACE_INFO"/> array manually.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct WLAN_INTERFACE_INFO_LIST_HEADER
{
    public uint dwNumberOfItems;
    public uint dwIndex;
}

/// <summary>
/// Header of WLAN_BSS_LIST. <c>dwTotalSize</c> is the full byte size (header + entries +
/// IE blobs); the trailing WLAN_BSS_ENTRY array is parsed by fixed-offset reads in
/// <see cref="WlanApiNative.GetVisibleBssEntries"/>, NOT via PtrToStructure.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct WLAN_BSS_LIST_HEADER
{
    public uint dwTotalSize;
    public uint dwNumberOfItems;
}

/// <summary>
/// One profile entry returned by WlanGetProfileList. Just name + flags; fixed-size and
/// string-only → safe for PtrToStructure.
/// </summary>
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
public struct WLAN_PROFILE_INFO
{
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
    public string strProfileName;

    public uint dwFlags;
}

/// <summary>
/// Header of WLAN_PROFILE_INFO_LIST. Trailing fixed-size <see cref="WLAN_PROFILE_INFO"/>
/// array is walked manually.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct WLAN_PROFILE_INFO_LIST_HEADER
{
    public uint dwNumberOfItems;
    public uint dwIndex;
}

#pragma warning restore CA1707
