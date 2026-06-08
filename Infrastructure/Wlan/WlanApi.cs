using System.Runtime.InteropServices;

// ReSharper disable InconsistentNaming
// ReSharper disable IdentifierTypo

namespace NetScopeDiagnosticCenter.Infrastructure.Wlan;

/// <summary>
/// Raw P/Invoke declarations for wlanapi.dll. This class only contains DllImport
/// signatures — no business logic, no resource management. Wrap callers in
/// <see cref="WlanHandle"/> + <see cref="WlanApiNative"/> for safe usage.
///
/// All functions return DWORD = 0 (ERROR_SUCCESS) on success, otherwise a Win32
/// error code. Callers translate non-zero into <see cref="System.ComponentModel.Win32Exception"/>.
///
/// Pointer-returning functions (WlanEnumInterfaces, WlanGetNetworkBssList,
/// WlanGetProfileList, WlanQueryInterface) MUST have their out IntPtr passed to
/// <see cref="WlanFreeMemory"/> after use, or memory leaks. See WlanApiNative for
/// the disposal pattern.
/// </summary>
internal static class WlanApi
{
    private const string Dll = "wlanapi.dll";

    /// <summary>Client API version we negotiate with the WLAN service. 2 = Vista+.</summary>
    public const uint ClientVersionVista = 2;

    /// <summary>ERROR_SUCCESS — value returned by all WLAN functions on success.</summary>
    public const uint ErrorSuccess = 0;

    [DllImport(Dll, ExactSpelling = true)]
    public static extern uint WlanOpenHandle(
        uint dwClientVersion,
        IntPtr pReserved,
        out uint pdwNegotiatedVersion,
        out IntPtr phClientHandle);

    [DllImport(Dll, ExactSpelling = true)]
    public static extern uint WlanCloseHandle(
        IntPtr hClientHandle,
        IntPtr pReserved);

    /// <summary>
    /// Enumerates installed Wi-Fi adapters. Returns pointer to WLAN_INTERFACE_INFO_LIST;
    /// caller must free with WlanFreeMemory.
    /// </summary>
    [DllImport(Dll, ExactSpelling = true)]
    public static extern uint WlanEnumInterfaces(
        IntPtr hClientHandle,
        IntPtr pReserved,
        out IntPtr ppInterfaceList);

    /// <summary>
    /// Triggers an active scan on the given adapter. Async on the kernel side —
    /// returns immediately, scan completion is notified via WlanRegisterNotification
    /// or by polling WlanGetNetworkBssList ~4 seconds later.
    /// </summary>
    [DllImport(Dll, ExactSpelling = true)]
    public static extern uint WlanScan(
        IntPtr hClientHandle,
        ref Guid pInterfaceGuid,
        IntPtr pDot11Ssid,           // optional — IntPtr.Zero scans all SSIDs
        IntPtr pIeData,              // optional — IntPtr.Zero
        IntPtr pReserved);

    /// <summary>
    /// Returns all BSSIDs the adapter has currently in its scan cache. Pass IntPtr.Zero
    /// for ssid+bssType to get every visible AP. Caller must free with WlanFreeMemory.
    /// </summary>
    [DllImport(Dll, ExactSpelling = true)]
    public static extern uint WlanGetNetworkBssList(
        IntPtr hClientHandle,
        ref Guid pInterfaceGuid,
        IntPtr pDot11Ssid,                          // IntPtr.Zero = all SSIDs
        Dot11BssType dot11BssType,
        [MarshalAs(UnmanagedType.U1)] bool bSecurityEnabled,
        IntPtr pReserved,
        out IntPtr ppWlanBssList);

    /// <summary>
    /// Reads an interface property (current connection, RSSI, channel, etc.).
    /// The shape of the returned data depends on the opcode — caller must marshal
    /// according to the WLAN_INTF_OPCODE documentation. Free with WlanFreeMemory.
    /// </summary>
    [DllImport(Dll, ExactSpelling = true)]
    public static extern uint WlanQueryInterface(
        IntPtr hClientHandle,
        ref Guid pInterfaceGuid,
        WlanIntfOpcode OpCode,
        IntPtr pReserved,
        out uint pdwDataSize,
        out IntPtr ppData,
        IntPtr pWlanOpcodeValueType);

    /// <summary>
    /// Returns the saved Wi-Fi profiles for the adapter. Each entry has only the name +
    /// flags; for the actual XML use WlanGetProfile. Free with WlanFreeMemory.
    /// </summary>
    [DllImport(Dll, ExactSpelling = true)]
    public static extern uint WlanGetProfileList(
        IntPtr hClientHandle,
        ref Guid pInterfaceGuid,
        IntPtr pReserved,
        out IntPtr ppProfileList);

    /// <summary>
    /// Deletes a saved Wi-Fi profile by name for the adapter.
    /// </summary>
    [DllImport(Dll, ExactSpelling = true, CharSet = CharSet.Unicode)]
    public static extern uint WlanDeleteProfile(
        IntPtr hClientHandle,
        ref Guid pInterfaceGuid,
        [MarshalAs(UnmanagedType.LPWStr)] string strProfileName,
        IntPtr pReserved);

    /// <summary>
    /// Frees memory allocated by any WLAN_* function that returns an out IntPtr.
    /// Calling this on null is safe (no-op).
    /// </summary>
    [DllImport(Dll, ExactSpelling = true)]
    public static extern void WlanFreeMemory(IntPtr pMemory);
}
