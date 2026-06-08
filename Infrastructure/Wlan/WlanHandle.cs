using System.ComponentModel;
using Microsoft.Win32.SafeHandles;

namespace NetScopeDiagnosticCenter.Infrastructure.Wlan;

/// <summary>
/// SafeHandle wrapper for the WLAN client handle returned by <c>WlanOpenHandle</c>.
///
/// <para>
/// Why SafeHandle (vs a plain IntPtr field): the WLAN client handle holds a kernel
/// reference and a session in the WLAN AutoConfig service. If the managed wrapper
/// is garbage-collected without explicit Close, SafeHandle's CER-protected
/// <see cref="ReleaseHandle"/> still runs in the finalizer queue and calls
/// <c>WlanCloseHandle</c>. This prevents service-side handle leaks even when callers
/// forget to dispose.
/// </para>
///
/// <para>
/// Open() throws Win32Exception when:
/// <list type="bullet">
///   <item>WLAN AutoConfig service is stopped (ERROR_SERVICE_NOT_ACTIVE) — typical on
///         servers / pure-ethernet desktops.</item>
///   <item>RPC call to wlansvc.exe fails — antivirus / security policy interference.</item>
/// </list>
/// Callers should catch and surface a graceful "Wi-Fi service not available" message
/// rather than crashing the app.
/// </para>
/// </summary>
public sealed class WlanHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    /// <summary>
    /// Private constructor — instances are created via <see cref="Open"/>.
    /// SafeHandle requires the parameterless constructor for Marshal scenarios;
    /// we don't expose it because creating an empty handle has no use here.
    /// </summary>
    private WlanHandle()
        : base(ownsHandle: true)
    {
    }

    /// <summary>
    /// Opens a session with the WLAN AutoConfig service.
    /// </summary>
    /// <returns>A live <see cref="WlanHandle"/> that must be disposed by caller.</returns>
    /// <exception cref="Win32Exception">
    /// Thrown with <c>NativeErrorCode</c> set to the underlying WLAN error
    /// (ERROR_SERVICE_NOT_ACTIVE, ERROR_INVALID_PARAMETER, etc.).
    /// </exception>
    public static WlanHandle Open()
    {
        var status = WlanApi.WlanOpenHandle(
            WlanApi.ClientVersionVista,
            IntPtr.Zero,
            out _,
            out var rawHandle);

        if (status != WlanApi.ErrorSuccess)
        {
            throw new Win32Exception(
                (int)status,
                $"WlanOpenHandle failed (Win32 {status}). The WLAN AutoConfig service may be stopped or restricted.");
        }

        var handle = new WlanHandle();
        handle.SetHandle(rawHandle);
        return handle;
    }

    /// <summary>
    /// Called automatically by the SafeHandle infrastructure during Dispose / finalize.
    /// Returning false would log a CER warning; we always return true since
    /// WlanCloseHandle either succeeds or returns ERROR_INVALID_HANDLE (already closed)
    /// — both are acceptable end states.
    /// </summary>
    protected override bool ReleaseHandle()
    {
        // Cannot throw from ReleaseHandle (CER constraint) — swallow the status code.
        // The handle is now considered released regardless.
        _ = WlanApi.WlanCloseHandle(handle, IntPtr.Zero);
        return true;
    }

    /// <summary>
    /// Exposes the raw handle for P/Invoke calls that need it. Use only inside the
    /// Wlan infrastructure layer — do not propagate to higher layers.
    /// </summary>
    internal IntPtr Raw => handle;
}
