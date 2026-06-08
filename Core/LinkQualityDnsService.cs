using System.Diagnostics;
using System.Net;
using NetScopeDiagnosticCenter.Core.Models;

namespace NetScopeDiagnosticCenter.Core;

public sealed class LinkQualityDnsService
{
    public Task<LinkQualityDnsResult> ResolveAsync(
        string name,
        CancellationToken cancellationToken = default) =>
        ResolveAsync(name, LinkQualityThresholds.Default, cancellationToken);

    public async Task<LinkQualityDnsResult> ResolveAsync(
        string name,
        LinkQualityThresholds? thresholds = null,
        CancellationToken cancellationToken = default)
    {
        thresholds ??= LinkQualityThresholds.Default;
        name = name.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            return new LinkQualityDnsResult
            {
                Name = string.Empty,
                ThresholdProfile = BuildThresholdProfile(thresholds),
                Status = "Warning",
                Details = "Enter a DNS name before running the lookup."
            };
        }

        try
        {
            var stopwatch = Stopwatch.StartNew();
            var addresses = await Dns.GetHostAddressesAsync(name, cancellationToken)
                .WaitAsync(TimeSpan.FromMilliseconds(thresholds.DnsTimeoutMs), cancellationToken);
            stopwatch.Stop();

            var latency = Math.Round(stopwatch.Elapsed.TotalMilliseconds, 1);
            var addressList = addresses
                .Where(address => address.AddressFamily is System.Net.Sockets.AddressFamily.InterNetwork or System.Net.Sockets.AddressFamily.InterNetworkV6)
                .Select(address => address.ToString())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            return new LinkQualityDnsResult
            {
                Name = name,
                ThresholdProfile = BuildThresholdProfile(thresholds),
                Status = Classify(latency, addressList.Count, thresholds),
                LatencyMs = latency,
                Addresses = addressList,
                Details = addressList.Count > 0
                    ? $"Resolved {name} to {addressList.Count} address(es)."
                    : $"DNS query completed but returned no usable IP addresses for {name}."
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (TimeoutException)
        {
            return new LinkQualityDnsResult
            {
                Name = name,
                ThresholdProfile = BuildThresholdProfile(thresholds),
                Status = "Critical",
                Details = $"DNS lookup timed out after {thresholds.DnsTimeoutMs / 1000d:N0} seconds."
            };
        }
        catch (Exception ex)
        {
            return new LinkQualityDnsResult
            {
                Name = name,
                ThresholdProfile = BuildThresholdProfile(thresholds),
                Status = "Critical",
                Details = ex.Message
            };
        }
    }

    private static string Classify(double latencyMs, int addressCount, LinkQualityThresholds thresholds)
    {
        if (addressCount == 0 || latencyMs > thresholds.DnsCriticalLatencyMs)
        {
            return "Critical";
        }

        if (latencyMs > thresholds.DnsWarningLatencyMs)
        {
            return "Warning";
        }

        return "OK";
    }

    private static string BuildThresholdProfile(LinkQualityThresholds thresholds) =>
        $"DNS: warn > {thresholds.DnsWarningLatencyMs:N0} ms; critical > {thresholds.DnsCriticalLatencyMs:N0} ms or no usable address.";
}
