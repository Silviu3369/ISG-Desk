using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using NetScopeDiagnosticCenter.Collectors.Shared;
using NetScopeDiagnosticCenter.Core;
using NetScopeDiagnosticCenter.Core.Models;

namespace NetScopeDiagnosticCenter.Collectors;

public class TargetServiceDiscoveryCollector
{
    private const int DefaultMaxHosts = DiagnosticConstants.MaxServiceDiscoveryHosts;
    private static readonly TimeSpan PortTimeout = TimeSpan.FromMilliseconds(320);
    private static readonly TimeSpan ReverseDnsTimeout = TimeSpan.FromMilliseconds(350);

    public virtual async Task<ServiceTargetDiscoveryResult> ScanLocalSubnetAsync(
        IReadOnlyList<int> ports,
        CancellationToken cancellationToken = default)
    {
        var local = TryGetLocalScanRange();
        if (!string.IsNullOrWhiteSpace(local.Error))
        {
            return new ServiceTargetDiscoveryResult
            {
                Verdict = local.Error,
                Severity = "Warning",
                MaxHosts = DefaultMaxHosts,
                SkippedReason = local.Error,
                Ports = NormalizePorts(ports),
                Evidence = [local.Error],
                Limitations = ["Service discovery did not run because no usable private local IPv4 configuration was available."]
            };
        }

        return await ScanRangeAsync(local.Range, ports, cancellationToken, local.Evidence);
    }

    public virtual async Task<ServiceTargetDiscoveryResult> ScanRangeAsync(
        string rangeInput,
        IReadOnlyList<int> ports,
        CancellationToken cancellationToken = default)
    {
        return await ScanRangeAsync(rangeInput, ports, cancellationToken, []);
    }

    private static async Task<ServiceTargetDiscoveryResult> ScanRangeAsync(
        string rangeInput,
        IReadOnlyList<int> ports,
        CancellationToken cancellationToken,
        IReadOnlyList<string> initialEvidence)
    {
        var normalizedPorts = NormalizePorts(ports);
        if (normalizedPorts.Count == 0)
        {
            return new ServiceTargetDiscoveryResult
            {
                Source = rangeInput,
                Verdict = "Enter at least one valid TCP port between 1 and 65535 before scanning services.",
                Severity = "Warning",
                MaxHosts = DefaultMaxHosts,
                SkippedReason = "No valid TCP ports were supplied.",
                Ports = normalizedPorts,
                Limitations = ["Service discovery only scans explicitly requested TCP ports."]
            };
        }

        var evidence = new List<string>(initialEvidence);
        var parse = IpRangeParser.TryResolveHosts(rangeInput, DefaultMaxHosts);
        evidence.AddRange(parse.Evidence);

        if (!string.IsNullOrWhiteSpace(parse.Error))
        {
            return new ServiceTargetDiscoveryResult
            {
                Source = rangeInput,
                Verdict = parse.Error,
                Severity = "Warning",
                ScannedHosts = 0,
                MaxHosts = DefaultMaxHosts,
                SkippedReason = parse.Error,
                Ports = normalizedPorts,
                Evidence = evidence,
                Limitations =
                [
                    "Scan was not started because the range is invalid, public, or larger than the production safety limit.",
                    $"Service discovery is limited to {DefaultMaxHosts} private IPv4 addresses."
                ]
            };
        }

        evidence.Add($"Scanning {parse.Source} for TCP ports: {string.Join(", ", normalizedPorts)}.");
        evidence.Add($"Scan host count: {parse.Hosts.Count}; safety limit: {DefaultMaxHosts}.");

        var candidates = new List<(IPAddress Address, ServiceTargetCandidate Candidate)>();
        using var concurrency = new SemaphoreSlim(64);
        var tasks = parse.Hosts.Select(async address =>
        {
            await concurrency.WaitAsync(cancellationToken);
            try
            {
                var candidate = await ScanHostAsync(address, parse.Source, normalizedPorts, cancellationToken);
                if (candidate is not null)
                {
                    lock (candidates)
                    {
                        candidates.Add((address, candidate));
                    }
                }
            }
            finally
            {
                concurrency.Release();
            }
        });

        await Task.WhenAll(tasks);

        var ordered = candidates
            .OrderBy(item => NetworkSafetyPolicy.ToUInt32(item.Address))
            .Select(item => item.Candidate)
            .ToList();

        return new ServiceTargetDiscoveryResult
        {
            Source = parse.Source,
            Verdict = ordered.Count > 0
                ? $"Found {ordered.Count} host(s) with requested service ports open."
                : $"No requested service ports found in {parse.Source}.",
            Severity = ordered.Count > 0 ? "OK" : "Warning",
            ScannedHosts = parse.Hosts.Count,
            MaxHosts = DefaultMaxHosts,
            Ports = normalizedPorts,
            Candidates = ordered,
            Evidence = evidence,
            Limitations =
            [
                "Service discovery only checks TCP connect success; it does not authenticate or inspect application protocols.",
                "Only private IPv4 ranges up to 254 hosts are scanned.",
                "Run the selected Service Access test after discovery to collect DNS, ping, packet loss and per-port evidence."
            ]
        };
    }

    private static async Task<ServiceTargetCandidate?> ScanHostAsync(
        IPAddress address,
        string sourceRange,
        IReadOnlyList<int> ports,
        CancellationToken cancellationToken)
    {
        var openPorts = new List<int>();
        foreach (var port in ports)
        {
            if (await IsTcpOpenAsync(address, port, cancellationToken))
            {
                openPorts.Add(port);
            }
        }

        if (openPorts.Count == 0)
        {
            return null;
        }

        var hostName = await TryReverseDnsAsync(address, cancellationToken);
        var target = NormalizeCandidateTarget(hostName, address);
        return new ServiceTargetCandidate
        {
            Host = target,
            OpenPorts = openPorts,
            Source = $"Service scan {sourceRange}",
            Confidence = string.IsNullOrWhiteSpace(hostName) ? "Medium" : "High"
        };
    }

    private static string NormalizeCandidateTarget(string hostName, IPAddress address)
    {
        var normalizedHost = hostName.Trim().TrimEnd('.');
        return DiagnosticTargetValidator.TryNormalizeHost(normalizedHost, out var safeHost, out _)
            ? safeHost
            : address.ToString();
    }

    private static async Task<bool> IsTcpOpenAsync(
        IPAddress address,
        int port,
        CancellationToken cancellationToken)
    {
        using var client = new TcpClient();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(PortTimeout);
            await client.ConnectAsync(address.ToString(), port, timeout.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<string> TryReverseDnsAsync(
        IPAddress address,
        CancellationToken cancellationToken)
    {
        try
        {
            var entry = await TaskFaultObserver.Observe(Dns.GetHostEntryAsync(address))
                .WaitAsync(ReverseDnsTimeout, cancellationToken);
            return entry.HostName;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static List<int> NormalizePorts(IReadOnlyList<int> ports) =>
        ports
            .Where(port => port is > 0 and <= 65535)
            .Distinct()
            .Take(DiagnosticConstants.MaxTargetedTestPorts)
            .ToList();

    private static LocalRangeResult TryGetLocalScanRange()
    {
        try
        {
            foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (networkInterface.OperationalStatus != OperationalStatus.Up ||
                    networkInterface.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                {
                    continue;
                }

                IPInterfaceProperties properties;
                try { properties = networkInterface.GetIPProperties(); }
                catch { continue; }

                foreach (var unicast in properties.UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily != AddressFamily.InterNetwork ||
                        !NetworkSafetyPolicy.IsScannable(unicast.Address))
                    {
                        continue;
                    }

                    var ip = unicast.Address;
                    var prefix = unicast.PrefixLength;
                    var actualNetwork = NetworkSafetyPolicy.ToUInt32(ip) & NetworkSafetyPolicy.Mask(prefix);
                    var detectedSubnet = $"{NetworkSafetyPolicy.FromUInt32(actualNetwork)}/{prefix}";
                    var detectedHosts = NetworkSafetyPolicy.EstimateUsableHosts(prefix);
                    var safeNetwork = prefix < 24
                        ? NetworkSafetyPolicy.ToUInt32(ip) & NetworkSafetyPolicy.Mask(24)
                        : actualNetwork;
                    var safePrefix = prefix < 24 ? 24 : prefix;
                    var safeRange = $"{NetworkSafetyPolicy.FromUInt32(safeNetwork)}/{safePrefix}";
                    var evidence = new List<string>
                    {
                        $"Detected local IPv4 {ip} on adapter {networkInterface.Name}.",
                        $"Detected subnet is {detectedSubnet} with approximately {detectedHosts:N0} usable hosts.",
                        $"Auto safe service scan range is {safeRange}."
                    };

                    if (prefix < 24)
                    {
                        evidence.Add("Detected network is larger than /24; service discovery will scan only a 254-host safe window around this PC.");
                    }

                    return new LocalRangeResult(safeRange, evidence, string.Empty);
                }
            }
        }
        catch
        {
            // Fall through to the standard no-local-range result.
        }

        return new LocalRangeResult(string.Empty, [], "Local private IPv4 subnet could not be detected.");
    }

    private sealed record LocalRangeResult(
        string Range,
        IReadOnlyList<string> Evidence,
        string Error);
}
