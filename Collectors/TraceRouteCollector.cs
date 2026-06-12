using System.Net;
using NetScopeDiagnosticCenter.Collectors.Shared;
using NetScopeDiagnosticCenter.Core.Models;
using NetScopeDiagnosticCenter.Infrastructure;

namespace NetScopeDiagnosticCenter.Collectors;

/// <summary>
/// Bounded traceroute (default beacon 1.1.1.1) so the diagnosis can attribute a fault
/// to LOCAL (switch/gateway) vs ISP vs beyond — the path-visibility gap professional
/// tools have. Uses <c>Test-NetConnection -TraceRoute</c> (locale-stable cmdlet, no text
/// parsing), capped at 15 hops + a hard collector timeout so a black-hole path can't
/// hang the diagnosis. Caller-supplied targets (manual traceroute on the Diagnosis page)
/// are validated upstream and embedded via <see cref="JsonCollectorBase.PsSingleQuote"/>.
/// </summary>
public class TraceRouteCollector : JsonCollectorBase
{
    public TraceRouteCollector(PowerShellRunner powerShellRunner) : base(powerShellRunner)
    {
    }

    // Virtual so unit tests can fake hop results without spawning PowerShell.
    public virtual async Task<TraceRouteResult> TraceAsync(string target = "1.1.1.1", CancellationToken cancellationToken = default)
    {
        var safeTarget = PsSingleQuote(target);
        var script = $$"""
$ErrorActionPreference = 'SilentlyContinue'
try {
    $r = Test-NetConnection -ComputerName '{{safeTarget}}' -TraceRoute -Hops 15 -WarningAction SilentlyContinue -ErrorAction Stop
    $hops = @()
    $i = 0
    foreach ($h in @($r.TraceRoute)) { $i++; $hops += [pscustomobject]@{ Hop = $i; Address = [string]$h } }
    [pscustomobject]@{
        Target = '{{safeTarget}}'
        ReachedTarget = [bool]$r.PingSucceeded
        Hops = @($hops)
    } | ConvertTo-Json -Depth 4
}
catch {
    [pscustomobject]@{ Target = '{{safeTarget}}'; ReachedTarget = $false; Hops = @(); Error = $_.Exception.Message } | ConvertTo-Json -Depth 4
}
""";

        var (data, execution) = await RunCollectorWithExecutionAsync<RawTrace>(
            script, TimeSpan.FromSeconds(25), cancellationToken).ConfigureAwait(false);

        if (data is null)
        {
            return new TraceRouteResult
            {
                Target = target,
                Status = "Unknown",
                CollectorStatus = execution.TimedOut
                    ? "Traceroute timed out (path may be black-holing)."
                    : $"Traceroute failed: {execution.Error}",
                Summary = "Path could not be traced.",
            };
        }

        var result = new TraceRouteResult
        {
            Target = target,
            ReachedTarget = data.ReachedTarget,
        };

        foreach (var h in data.Hops ?? [])
        {
            var addr = string.IsNullOrWhiteSpace(h.Address) || h.Address == "0.0.0.0" ? "*" : h.Address.Trim();
            result.Hops.Add(new TraceHop
            {
                Hop = h.Hop,
                Address = addr,
                Scope = ClassifyHop(addr),
            });
        }

        // Attribution: where does the path stop making progress?
        var lastResponding = result.Hops.LastOrDefault(x => x.Address != "*");
        if (data.ReachedTarget)
        {
            result.Status = "OK";
            result.Summary = $"Path to {target} completed in {result.Hops.Count} hop(s).";
        }
        else if (lastResponding is null)
        {
            result.Status = "Critical";
            result.Summary = "No hops responded — the path is broken at the very first hop (local switch / gateway / cabling).";
        }
        else if (lastResponding.Scope == "local")
        {
            result.Status = "Critical";
            result.Summary = $"Path stops inside the local network (last responding hop {lastResponding.Address}). Likely a LOCAL switch / gateway / VLAN issue, not the ISP.";
        }
        else
        {
            result.Status = "Warning";
            result.Summary = $"Path leaves the LAN but does not reach {target} (last responding hop {lastResponding.Address}, public). Likely an ISP / upstream issue — not this PC or the local network.";
        }

        return result;
    }

    private static string ClassifyHop(string address)
    {
        if (address == "*") return "timeout";
        if (IPAddress.TryParse(address, out var ip)
            && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
            && NetworkSafetyPolicy.IsPrivateIpv4(ip))
        {
            return "local";
        }
        return "isp/internet";
    }

    private sealed class RawTrace
    {
        public bool ReachedTarget { get; set; }
        public List<RawHop>? Hops { get; set; }
    }

    private sealed class RawHop
    {
        public int Hop { get; set; }
        public string Address { get; set; } = string.Empty;
    }
}
