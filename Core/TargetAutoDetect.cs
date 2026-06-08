using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Net;
using NetScopeDiagnosticCenter.Core.Models;
using Microsoft.Win32;

namespace NetScopeDiagnosticCenter.Core;

/// <summary>
/// Best-effort auto-detection of likely Targeted-Test targets, using ONLY local
/// read-only sources — no network scan, no PowerShell, no probing:
/// <list type="bullet">
///   <item>mapped SMB drives from <c>HKCU\Network\&lt;drive&gt;\RemotePath</c></item>
///   <item>logon server / AD domain from process environment</item>
///   <item>default gateway / primary DNS from <see cref="NetworkInterface"/></item>
/// </list>
/// Every method swallows failures and returns empty/null — detection is a convenience,
/// never a hard dependency, and can run safely on page interaction.
/// </summary>
public static class TargetAutoDetect
{
    private const int MaxPassiveShareCandidates = DiagnosticConstants.MaxTargetedTestTargets;

    /// <summary>UNC roots of every currently-mapped network drive (e.g. \\SRV\Share).</summary>
    public static IReadOnlyList<string> MappedShares()
    {
        var results = new List<string>();
        try
        {
            using var net = Registry.CurrentUser.OpenSubKey(@"Network");
            if (net is null) return results;
            foreach (var drive in net.GetSubKeyNames())
            {
                try
                {
                    using var k = net.OpenSubKey(drive);
                    if (k?.GetValue("RemotePath") is string unc && !string.IsNullOrWhiteSpace(unc))
                    {
                        var trimmed = unc.Trim();
                        if (!results.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
                            results.Add(trimmed);
                    }
                }
                catch { /* skip a single unreadable drive key */ }
            }
        }
        catch { /* no HKCU\Network or access denied — return whatever we have */ }
        return results;
    }

    public static IReadOnlyList<ShareTargetCandidate> LocalShareTargets(NetworkProfile profile)
    {
        var results = new List<ShareTargetCandidate>();
        AddShareCandidates(results, MappedShares(), "Mapped network drive", "High");

        if (profile.FileServers.Count > 0)
        {
            AddShareCandidates(
                results,
                profile.FileServers.Select(target => string.IsNullOrWhiteSpace(target.SharePath) ? target.Host : target.SharePath),
                "Network profile file server",
                "High");
        }

        return results
            .Take(MaxPassiveShareCandidates)
            .ToList();
    }

    private static void AddShareCandidates(
        List<ShareTargetCandidate> results,
        IEnumerable<string> targets,
        string source,
        string confidence)
    {
        foreach (var target in targets)
        {
            if (!DiagnosticTargetValidator.TryNormalizeHostOrShare(target, out var host, out var sharePath, out _))
            {
                continue;
            }

            var normalized = string.IsNullOrWhiteSpace(sharePath) ? host : sharePath;
            if (results.Any(item => item.Target.Equals(normalized, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            results.Add(new ShareTargetCandidate
            {
                Target = normalized,
                Source = source,
                Confidence = confidence
            });
        }
    }

    public static IReadOnlyList<DomainControllerCandidate> LocalDomainControllerTargets(NetworkProfile profile)
    {
        var results = new List<DomainControllerCandidate>();

        AddDomainControllerCandidates(
            results,
            profile.DomainControllers.Select(target => target.Host),
            profile.DomainName,
            "Network profile domain controller",
            "High");

        var logonServer = LogonServer();
        if (!string.IsNullOrWhiteSpace(logonServer))
        {
            AddDomainControllerCandidates(
                results,
                [logonServer],
                FirstNonEmpty(profile.DomainName, DomainName()),
                "Windows logon server",
                "High");
        }

        return results
            .Take(DiagnosticConstants.MaxTargetedTestTargets)
            .ToList();
    }

    public static IReadOnlyList<string> DomainNameHints(NetworkProfile profile)
    {
        var hints = new List<string>();
        AddDomainNameHint(hints, profile.DomainName);
        AddDomainNameHint(hints, DomainName());
        return hints
            .Take(DiagnosticConstants.MaxDomainDiscoveryDomains)
            .ToList();
    }

    private static void AddDomainControllerCandidates(
        List<DomainControllerCandidate> results,
        IEnumerable<string> hosts,
        string? domainName,
        string source,
        string confidence)
    {
        foreach (var host in hosts)
        {
            if (!DiagnosticTargetValidator.TryNormalizeHost(host, out var normalized, out _))
            {
                continue;
            }

            if (results.Any(item => item.Host.Equals(normalized, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            results.Add(new DomainControllerCandidate
            {
                Host = normalized,
                DomainName = domainName ?? string.Empty,
                Source = source,
                Confidence = confidence
            });
        }
    }

    private static void AddDomainNameHint(List<string> hints, string? domainName)
    {
        if (string.IsNullOrWhiteSpace(domainName) ||
            domainName.Equals("Not domain joined", StringComparison.OrdinalIgnoreCase) ||
            domainName.Equals("Unknown", StringComparison.OrdinalIgnoreCase) ||
            !DiagnosticTargetValidator.TryNormalizeHost(domainName, out var normalized, out _))
        {
            return;
        }

        if (!hints.Contains(normalized, StringComparer.OrdinalIgnoreCase))
        {
            hints.Add(normalized);
        }
    }

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;

    public static IReadOnlyList<ServiceTargetCandidate> LocalServiceTargets(NetworkProfile profile)
    {
        var results = new List<ServiceTargetCandidate>();

        AddServiceCandidates(
            results,
            profile.ImportantServers.Select(target => target.Host),
            "Network profile important server",
            "High");

        AddServiceCandidates(
            results,
            profile.DomainControllers.Select(target => target.Host),
            "Network profile domain controller",
            "Medium");

        var gateway = DefaultGateway();
        if (!string.IsNullOrWhiteSpace(gateway) && IsPrivateIpv4String(gateway))
        {
            AddServiceCandidates(results, [gateway], "Active adapter default gateway", "Medium");
        }

        var dns = PrimaryDns();
        if (!string.IsNullOrWhiteSpace(dns) && IsPrivateIpv4String(dns))
        {
            AddServiceCandidates(results, [dns], "Active adapter primary DNS", "Medium");
        }

        return results
            .Take(DiagnosticConstants.MaxTargetedTestTargets)
            .ToList();
    }

    private static void AddServiceCandidates(
        List<ServiceTargetCandidate> results,
        IEnumerable<string> hosts,
        string source,
        string confidence)
    {
        foreach (var host in hosts)
        {
            if (!DiagnosticTargetValidator.TryNormalizeHost(host, out var normalized, out _))
            {
                continue;
            }

            if (results.Any(item => item.Host.Equals(normalized, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            results.Add(new ServiceTargetCandidate
            {
                Host = normalized,
                Source = source,
                Confidence = confidence
            });
        }
    }

    private static bool IsPrivateIpv4String(string? value)
    {
        if (!IPAddress.TryParse(value, out var address) ||
            address.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        var bytes = address.GetAddressBytes();
        return bytes[0] == 10 ||
               (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) ||
               (bytes[0] == 192 && bytes[1] == 168);
    }

    /// <summary>The authenticating domain controller host (from %LOGONSERVER%), without the leading \\.</summary>
    public static string? LogonServer()
    {
        try
        {
            var ls = Environment.GetEnvironmentVariable("LOGONSERVER");
            if (string.IsNullOrWhiteSpace(ls)) return null;
            var host = ls.TrimStart('\\').Trim();
            // %LOGONSERVER% is the local machine on a workgroup PC — not a real DC.
            return string.Equals(host, Environment.MachineName, StringComparison.OrdinalIgnoreCase)
                ? null
                : (host.Length == 0 ? null : host);
        }
        catch { return null; }
    }

    /// <summary>AD domain name, or null on a workgroup machine.</summary>
    public static string? DomainName()
    {
        try
        {
            var d = Environment.UserDomainName;
            return string.IsNullOrWhiteSpace(d)
                   || string.Equals(d, Environment.MachineName, StringComparison.OrdinalIgnoreCase)
                ? null
                : d;
        }
        catch { return null; }
    }

    /// <summary>IPv4 default gateway of the active adapter (a sane local service-test candidate).</summary>
    public static string? DefaultGateway() => FromActiveNic(p =>
        p.GatewayAddresses.FirstOrDefault(g => g.Address.AddressFamily == AddressFamily.InterNetwork)?.Address.ToString());

    /// <summary>Primary IPv4 DNS server of the active adapter.</summary>
    public static string? PrimaryDns() => FromActiveNic(p =>
        p.DnsAddresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)?.ToString());

    private static string? FromActiveNic(Func<IPInterfaceProperties, string?> pick)
    {
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up
                    || nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                {
                    continue;
                }

                IPInterfaceProperties props;
                try { props = nic.GetIPProperties(); }
                catch { continue; }

                if (!props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork))
                    continue; // not the traffic-carrying NIC

                var value = pick(props);
                if (!string.IsNullOrWhiteSpace(value)) return value;
            }
        }
        catch { /* fall through */ }
        return null;
    }
}
