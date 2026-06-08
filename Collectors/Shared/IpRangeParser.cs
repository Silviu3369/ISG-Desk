using System.Net;
using System.Net.Sockets;

namespace NetScopeDiagnosticCenter.Collectors.Shared;

/// <summary>
/// Result of parsing an IP range input string.
/// </summary>
public sealed record IpRangeParseResult(
    List<IPAddress> Hosts,
    string Source,
    int EstimatedHosts,
    string Error,
    IReadOnlyList<string> Evidence,
    string DetectedSubnet,
    int DetectedSubnetHosts,
    string SafeScanRange);

/// <summary>
/// Parses user-supplied IP range strings (single IP, CIDR, explicit range)
/// into a list of scannable host addresses.
/// Extracted from duplicate logic in PrinterDiscoveryCollector and NetworkDeviceCollector.
/// </summary>
public static class IpRangeParser
{
    private static readonly IpRangeParseResult Empty =
        new([], string.Empty, 0, string.Empty, [], string.Empty, 0, string.Empty);

    /// <summary>
    /// Resolves an input string to a list of IPv4 host addresses.
    /// Supports single IP, CIDR notation (e.g. 192.168.1.0/24), and explicit range (e.g. 192.168.1.1-192.168.1.50).
    /// </summary>
    public static IpRangeParseResult TryResolveHosts(string input, int maxHosts)
    {
        var value = input.Trim();

        // Single IP
        if (IPAddress.TryParse(value, out var singleIp) && singleIp.AddressFamily == AddressFamily.InterNetwork)
        {
            if (!NetworkSafetyPolicy.IsPrivateIpv4(singleIp))
            {
                return Empty with
                {
                    Source = value,
                    EstimatedHosts = 1,
                    Error = "Public IP scanning is not allowed. Enter an authorized private IPv4 target."
                };
            }

            return Empty with
            {
                Hosts = [singleIp],
                Source = singleIp.ToString(),
                EstimatedHosts = 1,
                Evidence = [$"Resolved single host {singleIp}."]
            };
        }

        // CIDR notation
        if (value.Contains('/', StringComparison.Ordinal))
        {
            return TryResolveCidr(value, maxHosts);
        }

        // Explicit range
        if (value.Contains('-', StringComparison.Ordinal))
        {
            return TryResolveExplicitRange(value, maxHosts);
        }

        return Empty with
        {
            Source = value,
            Error = "Enter a valid single IPv4 address, CIDR up to 254 hosts, or explicit range."
        };
    }

    /// <summary>Parses a CIDR notation string (e.g. 192.168.1.0/24).</summary>
    public static IpRangeParseResult TryResolveCidr(string value, int maxHosts)
    {
        var parts = value.Split('/', StringSplitOptions.TrimEntries);
        if (parts.Length != 2 ||
            !IPAddress.TryParse(parts[0], out var ip) ||
            ip.AddressFamily != AddressFamily.InterNetwork ||
            !int.TryParse(parts[1], out var prefix) ||
            prefix is < 0 or > 32)
        {
            return Empty with { Source = value, Error = "Invalid CIDR range." };
        }

        if (!NetworkSafetyPolicy.IsPrivateIpv4(ip))
        {
            return Empty with
            {
                Source = value,
                Error = "Public IP scanning is not allowed. Enter an authorized private IPv4 range."
            };
        }

        var network = NetworkSafetyPolicy.ToUInt32(ip) & NetworkSafetyPolicy.Mask(prefix);
        var subnetStr = $"{NetworkSafetyPolicy.FromUInt32(network)}/{prefix}";
        var usableHosts = NetworkSafetyPolicy.EstimateUsableHosts(prefix);

        if (prefix < 24)
        {
            var estimatedLong = prefix == 0 ? long.MaxValue : 1L << (32 - prefix);
            var estimated = estimatedLong > int.MaxValue ? int.MaxValue : (int)estimatedLong;
            return Empty with
            {
                Source = value,
                EstimatedHosts = estimated,
                Error = $"CIDR /{prefix} is larger than the v1 safety limit. Enter a CIDR up to {maxHosts} usable hosts, single IP, or explicit range up to {maxHosts} hosts.",
                Evidence = [$"Rejected CIDR {value}; estimated host count is larger than {maxHosts}."],
                DetectedSubnet = subnetStr,
                DetectedSubnetHosts = usableHosts,
                SafeScanRange = string.Empty
            };
        }

        var allCount = prefix == 32 ? 1 : 1 << (32 - prefix);
        var start = allCount <= 2 ? network : network + 1;
        var end = allCount <= 2 ? network + (uint)allCount - 1 : network + (uint)allCount - 2;
        var count = (int)(end - start + 1);

        if (count > maxHosts)
        {
            return Empty with
            {
                Source = value,
                EstimatedHosts = count,
                Error = $"CIDR contains {count} hosts; maximum allowed is {maxHosts}."
            };
        }

        var hosts = Enumerable.Range(0, count)
            .Select(offset => NetworkSafetyPolicy.FromUInt32(start + (uint)offset))
            .ToList();

        var source = $"{NetworkSafetyPolicy.FromUInt32(network)}/{prefix}";
        return new IpRangeParseResult(
            hosts, source, hosts.Count, string.Empty,
            [$"Resolved CIDR {value} to {hosts.Count} hosts."],
            source, hosts.Count, source);
    }

    /// <summary>Parses an explicit range string (e.g. 192.168.1.1-192.168.1.50).</summary>
    public static IpRangeParseResult TryResolveExplicitRange(string value, int maxHosts)
    {
        var parts = value.Split('-', StringSplitOptions.TrimEntries);
        if (parts.Length != 2 ||
            !IPAddress.TryParse(parts[0], out var startIp) ||
            !IPAddress.TryParse(parts[1], out var endIp) ||
            startIp.AddressFamily != AddressFamily.InterNetwork ||
            endIp.AddressFamily != AddressFamily.InterNetwork)
        {
            return Empty with { Source = value, Error = "Invalid explicit IP range." };
        }

        var start = NetworkSafetyPolicy.ToUInt32(startIp);
        var end = NetworkSafetyPolicy.ToUInt32(endIp);
        if (end < start)
        {
            return Empty with
            {
                Source = value,
                Error = "IP range end must be greater than or equal to the start."
            };
        }

        if (!NetworkSafetyPolicy.IsPrivateIpv4(startIp) || !NetworkSafetyPolicy.IsPrivateIpv4(endIp))
        {
            return Empty with
            {
                Source = value,
                Error = "Public IP scanning is not allowed. Enter an authorized private IPv4 range."
            };
        }

        var count = end - start + 1;
        if (count > maxHosts)
        {
            var estimated = count > int.MaxValue ? int.MaxValue : (int)count;
            return Empty with
            {
                Source = value,
                EstimatedHosts = estimated,
                Error = $"IP range contains {count} hosts; maximum allowed is {maxHosts}."
            };
        }

        var hosts = Enumerable.Range(0, (int)count)
            .Select(offset => NetworkSafetyPolicy.FromUInt32(start + (uint)offset))
            .ToList();

        var source = $"{startIp}-{endIp}";
        return new IpRangeParseResult(
            hosts, source, hosts.Count, string.Empty,
            [$"Resolved explicit range {value} to {hosts.Count} hosts."],
            source, hosts.Count, source);
    }
}
