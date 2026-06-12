using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using NetScopeDiagnosticCenter.Collectors.Shared;
using NetScopeDiagnosticCenter.Core;
using NetScopeDiagnosticCenter.Core.Models;

namespace NetScopeDiagnosticCenter.Collectors;

public class TargetShareDiscoveryCollector
{
    private const int SmbPort = 445;
    private const int DefaultMaxHosts = DiagnosticConstants.MaxSmbDiscoveryHosts;
    private static readonly TimeSpan PortTimeout = TimeSpan.FromMilliseconds(320);
    private static readonly TimeSpan ReverseDnsTimeout = TimeSpan.FromMilliseconds(350);

    public virtual async Task<ShareTargetDiscoveryResult> ScanLocalSubnetAsync(
        CancellationToken cancellationToken = default)
    {
        var local = TryGetLocalScanRange();
        if (!string.IsNullOrWhiteSpace(local.Error))
        {
            return new ShareTargetDiscoveryResult
            {
                Verdict = local.Error,
                Severity = "Warning",
                MaxHosts = DefaultMaxHosts,
                SkippedReason = local.Error,
                Evidence = [local.Error],
                Limitations = ["SMB discovery did not run because no usable private local IPv4 configuration was available."]
            };
        }

        return await ScanRangeAsync(local.Range, cancellationToken, local.Evidence);
    }

    public virtual async Task<ShareTargetDiscoveryResult> ScanRangeAsync(
        string rangeInput,
        CancellationToken cancellationToken = default)
    {
        return await ScanRangeAsync(rangeInput, cancellationToken, []);
    }

    private static async Task<ShareTargetDiscoveryResult> ScanRangeAsync(
        string rangeInput,
        CancellationToken cancellationToken,
        IReadOnlyList<string> initialEvidence)
    {
        var evidence = new List<string>(initialEvidence);
        var parse = IpRangeParser.TryResolveHosts(rangeInput, DefaultMaxHosts);
        evidence.AddRange(parse.Evidence);

        if (!string.IsNullOrWhiteSpace(parse.Error))
        {
            return new ShareTargetDiscoveryResult
            {
                Source = rangeInput,
                Verdict = parse.Error,
                Severity = "Warning",
                ScannedHosts = 0,
                MaxHosts = DefaultMaxHosts,
                SkippedReason = parse.Error,
                Evidence = evidence,
                Limitations =
                [
                    "Scan was not started because the range is invalid, public, or larger than the production safety limit.",
                    $"SMB discovery is limited to {DefaultMaxHosts} private IPv4 addresses."
                ]
            };
        }

        evidence.Add($"Scanning {parse.Source} for TCP 445 (SMB).");
        evidence.Add($"Scan host count: {parse.Hosts.Count}; safety limit: {DefaultMaxHosts}.");

        var candidates = new List<(IPAddress Address, ShareTargetCandidate Candidate)>();
        using var concurrency = new SemaphoreSlim(64);
        var tasks = parse.Hosts.Select(async address =>
        {
            await concurrency.WaitAsync(cancellationToken);
            try
            {
                var candidate = await ScanHostAsync(address, parse.Source, cancellationToken);
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

        return new ShareTargetDiscoveryResult
        {
            Source = parse.Source,
            Verdict = ordered.Count > 0
                ? $"Found {ordered.Count} host(s) with SMB TCP 445 open."
                : $"No SMB TCP 445 hosts found in {parse.Source}.",
            Severity = ordered.Count > 0 ? "OK" : "Warning",
            ScannedHosts = parse.Hosts.Count,
            MaxHosts = DefaultMaxHosts,
            Candidates = ordered,
            Evidence = evidence,
            Limitations =
            [
                "SMB discovery only checks TCP 445 reachability; it does not authenticate or enumerate remote shares.",
                "Only private IPv4 ranges up to 254 hosts are scanned.",
                "Use mapped drives, profile targets, or a manual UNC path when the exact share name is required."
            ]
        };
    }

    private static async Task<ShareTargetCandidate?> ScanHostAsync(
        IPAddress address,
        string sourceRange,
        CancellationToken cancellationToken)
    {
        if (!await IsTcpOpenAsync(address, SmbPort, cancellationToken))
        {
            return null;
        }

        var hostName = await TryReverseDnsAsync(address, cancellationToken);
        var target = NormalizeCandidateTarget(hostName, address);
        return new ShareTargetCandidate
        {
            Target = target,
            Source = $"SMB scan {sourceRange}",
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
                        $"Auto safe SMB scan range is {safeRange}."
                    };

                    if (prefix < 24)
                    {
                        evidence.Add("Detected network is larger than /24; SMB discovery will scan only a 254-host safe window around this PC.");
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
