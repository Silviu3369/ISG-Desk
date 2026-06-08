using NetScopeDiagnosticCenter.Core.Models;

namespace NetScopeDiagnosticCenter.Core;

public sealed class HealthScoreCalculator
{
    public HealthScoreResult Calculate(NetworkDiagnosisResult result)
    {
        var score = 100;
        var penalties = new List<string>();

        ApplyIf(!result.IpConfiguration.HasValidIp, 50, "No valid IPv4 address or APIPA detected.");
        ApplyIf(!result.IpConfiguration.HasGateway, 40, "No default gateway configured.");
        ApplyIf(!result.IpConfiguration.HasDnsServers, 25, "No DNS servers configured.");
        ApplyIf(result.Tests.Gateway.IsCritical, 40, "Gateway unreachable.");
        ApplyIf(result.Tests.DnsResolution.IsCritical || result.Tests.InternetDnsResolution.IsCritical, 30, "DNS lookup failed.");
        ApplyIf(InternetIpFailed(result), 25, "Internet IP tests failed.");
        ApplyIf(result.Tests.ExternalTcp443.IsCritical, 15, "External TCP 443 test failed.");
        ApplyIf(result.Tests.HttpsGet.IsCritical, 15, "HTTPS GET test failed.");
        ApplyIf(IsSlowEthernet(result), 20, $"Ethernet link speed is below expected minimum ({result.Profile.ExpectedMinimumEthernetMbps} Mbps).");
        var wifiSeverity = WifiSignalClassifier.GetSeverity(result.Wifi?.SignalPercent);
        ApplyIf(result.Wifi?.SignalPercent.HasValue == true && wifiSeverity == "Critical", 30, "Wi-Fi signal is critical.");
        ApplyIf(result.Wifi?.SignalPercent.HasValue == true && wifiSeverity == "Warning", 15, "Wi-Fi signal is moderate.");
        ApplyIf((result.Tests.GatewayPacketLoss.LossPercent ?? 0) > 5 || (result.Tests.InternetPacketLoss.LossPercent ?? 0) > 5, 35, "Packet loss is above 5%.");
        ApplyIf(result.Tests.Gateway.IsWarning || result.Tests.InternetPing.IsWarning, 25, "High latency detected.");
        ApplyIf((result.Adapter.ErrorsPerSecond ?? 0) > 0 || (result.Adapter.DiscardsPerSecond ?? 0) > 0, 30, "Adapter errors or discards increased during sampling.");
        ApplyIf(result.Adapter.Errors > 0 || result.Adapter.Discards > 0, 15, "Adapter cumulative errors or discards detected.");
        ApplyIf(result.LastPortTest is { TcpSucceeded: false }, 20, "Important TCP port test failed.");

        // "Could not measure" must not score as healthy. If the connectivity collector
        // didn't complete, every test above is "Unknown" (no penalty fired) → cap the
        // score into the Warning band so the gauge can't show a green 100 on a network
        // we never actually measured.
        if (!result.Tests.CollectorStatus.Equals("OK", StringComparison.OrdinalIgnoreCase))
        {
            penalties.Add("Connectivity could not be measured (collector timeout/failure) — score capped.");
            score = Math.Min(score, 55);
        }

        score = Math.Clamp(score, 0, 100);
        return new HealthScoreResult
        {
            Score = score,
            Status = ToStatus(score),
            Penalties = penalties
        };

        void ApplyIf(bool condition, int points, string reason)
        {
            if (!condition)
            {
                return;
            }

            score -= points;
            penalties.Add($"-{points}: {reason}");
        }
    }

    private static bool IsSlowEthernet(NetworkDiagnosisResult result)
    {
        return result.Adapter.ConnectionType.Equals("Ethernet", StringComparison.OrdinalIgnoreCase) &&
            result.Adapter.LinkSpeedMbps.HasValue &&
            result.Adapter.LinkSpeedMbps.Value < result.Profile.ExpectedMinimumEthernetMbps;
    }

    private static string ToStatus(int score)
    {
        return score switch
        {
            >= 90 => "Healthy",
            >= 70 => "Good",
            >= 40 => "Warning",
            _ => "Critical"
        };
    }

    private static bool InternetIpFailed(NetworkDiagnosisResult result)
    {
        var ipTargets = result.Tests.InternetPingResults
            .Where(test => !DiagnosticConstants.DnsTestHostnames.Contains(test.Target, StringComparer.OrdinalIgnoreCase))
            .ToList();

        return ipTargets.Count > 0
            ? ipTargets.All(test => test.IsCritical)
            : result.Tests.InternetPing.IsCritical;
    }
}
