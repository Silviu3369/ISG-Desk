using System.Diagnostics;
using System.Net;

namespace NetScopeDiagnosticCenter.Collectors.Shared;

/// <summary>
/// Resolves a Windows machine name via NetBIOS adapter status (<c>nbtstat -A ip</c>) —
/// the same approach the Wi-Fi LAN scanner uses. Bounded, never throws on failure,
/// returns <c>null</c> when the host doesn't speak NetBIOS (phones, IoT, Linux without
/// Samba). Shared by the Network Devices discovery scan.
/// </summary>
public static class NetBiosNameResolver
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(900);

    public static async Task<string?> ResolveAsync(string ip, CancellationToken cancellationToken)
    {
        if (!IPAddress.TryParse(ip, out var address) || address.GetAddressBytes().Length != 4)
        {
            return null;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);

        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = "nbtstat",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        process.StartInfo.ArgumentList.Add("-A");
        process.StartInfo.ArgumentList.Add(address.ToString());

        try
        {
            if (!process.Start())
            {
                return null;
            }

            var outputTask = process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                return null;
            }

            return ParseName(await outputTask.ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            return null;
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Parses nbtstat output: prefers the UNIQUE workstation name (&lt;00&gt;), falls back to the server name (&lt;20&gt;).</summary>
    public static string? ParseName(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return null;
        }

        string? serverFallback = null;
        foreach (var rawLine in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (line.IndexOf("UNIQUE", StringComparison.OrdinalIgnoreCase) < 0)
            {
                continue;
            }

            var workstationMarker = line.IndexOf("<00>", StringComparison.OrdinalIgnoreCase);
            if (workstationMarker > 0)
            {
                var workstationName = line[..workstationMarker].Trim();
                if (IsUsable(workstationName))
                {
                    return workstationName;
                }
            }

            var serverMarker = line.IndexOf("<20>", StringComparison.OrdinalIgnoreCase);
            if (serverMarker > 0 && serverFallback is null)
            {
                var serverName = line[..serverMarker].Trim();
                if (IsUsable(serverName))
                {
                    serverFallback = serverName;
                }
            }
        }

        return serverFallback;
    }

    private static bool IsUsable(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        name = name.Trim();
        return !name.Equals("__MSBROWSE__", StringComparison.OrdinalIgnoreCase)
               && !IPAddress.TryParse(name, out _);
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Best-effort cleanup after timeout.
        }
    }
}
