using System.Runtime.InteropServices;

namespace NetScopeDiagnosticCenter.Infrastructure;

/// <summary>
/// Lists the shares published by an SMB server via <c>NetShareEnum</c> level 1
/// (netapi32) — the same list <c>net view \\host</c> shows, but locale-independent
/// and, per Microsoft docs, requiring no special group membership at level 0/1.
/// Used by the Internal Server / Share targeted test to turn "TCP 445 open" into
/// "these shares actually exist".
/// </summary>
public static class SmbShareEnumerator
{
    private const int NerrSuccess = 0;
    private const int ErrorAccessDenied = 5;
    private const int ErrorMoreData = 234;
    private const uint MaxPreferredLength = 0xFFFFFFFF;

    private const uint StypeMask = 0x000000FF;
    private const uint StypeDisktree = 0;
    private const uint StypePrintq = 1;
    private const uint StypeIpc = 3;
    private const uint StypeSpecial = 0x80000000;

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int NetShareEnum(
        string serverName,
        int level,
        out IntPtr bufPtr,
        uint prefMaxLen,
        out uint entriesRead,
        out uint totalEntries,
        ref uint resumeHandle);

    [DllImport("netapi32.dll", ExactSpelling = true)]
    private static extern int NetApiBufferFree(IntPtr buffer);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShareInfo1
    {
        public string NetName;
        public uint Type;
        public string? Remark;
    }

    /// <summary>
    /// Enumerates shares on <paramref name="host"/>. Never throws — failures are
    /// reported through <see cref="SmbShareEnumerationResult.Status"/>. The native
    /// call runs on the thread pool with a hard timeout because a dying server can
    /// block NetShareEnum for a long time.
    /// </summary>
    public static async Task<SmbShareEnumerationResult> EnumerateAsync(
        string host,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return SmbShareEnumerationResult.Unavailable("No host supplied for share enumeration.");
        }

        try
        {
            return await Task.Run(() => EnumerateCore(host.Trim()), cancellationToken)
                .WaitAsync(timeout, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return SmbShareEnumerationResult.Unavailable($"Share enumeration timed out after {timeout.TotalSeconds:N0} s.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return SmbShareEnumerationResult.Unavailable($"Share enumeration failed: {ex.Message}");
        }
    }

    private static SmbShareEnumerationResult EnumerateCore(string host)
    {
        var bufPtr = IntPtr.Zero;
        uint resume = 0;
        try
        {
            var status = NetShareEnum(host, 1, out bufPtr, MaxPreferredLength, out var read, out _, ref resume);
            if (status is not (NerrSuccess or ErrorMoreData))
            {
                return status == ErrorAccessDenied
                    ? new SmbShareEnumerationResult
                    {
                        Status = "AccessDenied",
                        Detail = "The server refused to list its shares for the current user."
                    }
                    : SmbShareEnumerationResult.Unavailable($"NetShareEnum returned Win32 error {status}.");
            }

            var shares = new List<SmbShareInfo>();
            var structSize = Marshal.SizeOf<ShareInfo1>();
            for (var i = 0; i < read; i++)
            {
                var entry = Marshal.PtrToStructure<ShareInfo1>(bufPtr + i * structSize);
                var baseType = entry.Type & StypeMask;
                if (baseType == StypeIpc)
                {
                    continue; // IPC$ carries no helpdesk signal.
                }

                shares.Add(new SmbShareInfo
                {
                    Name = entry.NetName,
                    Remark = entry.Remark ?? string.Empty,
                    IsPrintQueue = baseType == StypePrintq,
                    IsDiskShare = baseType == StypeDisktree,
                    IsHidden = (entry.Type & StypeSpecial) != 0 || entry.NetName.EndsWith('$')
                });
            }

            return new SmbShareEnumerationResult
            {
                Status = "OK",
                Detail = status == ErrorMoreData
                    ? "Share list truncated by the server (more entries exist)."
                    : string.Empty,
                Shares = shares
                    .OrderBy(share => share.IsHidden)
                    .ThenBy(share => share.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList()
            };
        }
        finally
        {
            if (bufPtr != IntPtr.Zero)
            {
                _ = NetApiBufferFree(bufPtr);
            }
        }
    }
}

public sealed class SmbShareEnumerationResult
{
    /// <summary>"OK" / "AccessDenied" / "Unavailable".</summary>
    public string Status { get; init; } = "Unavailable";

    public string Detail { get; init; } = string.Empty;

    public IReadOnlyList<SmbShareInfo> Shares { get; init; } = [];

    public static SmbShareEnumerationResult Unavailable(string detail) => new()
    {
        Status = "Unavailable",
        Detail = detail
    };
}

public sealed class SmbShareInfo
{
    public string Name { get; init; } = string.Empty;
    public string Remark { get; init; } = string.Empty;
    public bool IsDiskShare { get; init; }
    public bool IsPrintQueue { get; init; }
    public bool IsHidden { get; init; }

    /// <summary>"Scans", "C$ (hidden)", "OFFICEJET (print)" — list-friendly label.</summary>
    public string DisplayLabel =>
        IsPrintQueue ? $"{Name} (print)" : IsHidden ? $"{Name} (hidden)" : Name;
}
