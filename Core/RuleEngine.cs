using NetScopeDiagnosticCenter.Core.Models;

namespace NetScopeDiagnosticCenter.Core;

public sealed class RuleEngine
{
    public DiagnosisVerdict Evaluate(NetworkDiagnosisResult result)
    {
        var adapter = result.Adapter;
        var tests = result.Tests;
        var wifi = result.Wifi;
        var isEthernet = adapter.ConnectionType.Equals("Ethernet", StringComparison.OrdinalIgnoreCase);
        var isWifi = adapter.ConnectionType.Equals("Wi-Fi", StringComparison.OrdinalIgnoreCase);
        var linkIsSlow = adapter.LinkSpeedMbps.HasValue && adapter.LinkSpeedMbps.Value < result.Profile.ExpectedMinimumEthernetMbps;
        var hasAdapterErrors = adapter.Errors > 0 || adapter.Discards > 0;
        var hasIncreasingAdapterErrors = (adapter.ErrorsPerSecond ?? 0) > 0 || (adapter.DiscardsPerSecond ?? 0) > 0;
        var gatewayOk = tests.Gateway.IsOk || tests.Gateway.IsWarning;
        var dnsOk = tests.DnsResolution.IsOk || tests.InternetDnsResolution.IsOk;
        var internetIpOk = AnyInternetIpPingOk(tests);
        var internetOk = internetIpOk || tests.InternetDnsResolution.IsOk || tests.HttpsGet.IsOk;
        var packetLossDetected = IsPacketLossProblem(tests.GatewayPacketLoss) || IsPacketLossProblem(tests.InternetPacketLoss);

        if (!result.IpConfiguration.HasValidIp)
        {
            return new DiagnosisVerdict
            {
                Title = "No valid IP / possible DHCP issue",
                Severity = "Critical",
                Confidence = result.IpConfiguration.IsApipa ? "High" : "Medium",
                Evidence =
                [
                    $"IPv4 address: {result.IpConfiguration.IpAddress}.",
                    result.IpConfiguration.IsApipa ? "APIPA address detected (169.254.x.x)." : "No usable IPv4 address was detected.",
                    $"Gateway: {result.IpConfiguration.Gateway}."
                ],
                Limitations =
                [
                    "The app does not renew DHCP leases or change adapter settings in v1.0."
                ],
                Recommendations =
                [
                    "Check cable, Wi-Fi association, docking station and VLAN access.",
                    "Check DHCP server/scope availability, then renew the IP address manually if needed."
                ]
            };
        }

        if (!result.IpConfiguration.HasGateway)
        {
            return new DiagnosisVerdict
            {
                Title = "No default gateway configured",
                Severity = "Critical",
                Confidence = "High",
                Evidence =
                [
                    $"IP address: {result.IpConfiguration.IpAddress}.",
                    "No default gateway was found on the active adapter."
                ],
                Limitations =
                [
                    "Internet and routed internal network tests cannot be trusted without a default gateway."
                ],
                Recommendations =
                [
                    "Check DHCP options, static IP configuration, VLAN assignment and adapter profile.",
                    "Compare with a known-good PC on the same network."
                ]
            };
        }

        // Captive portal: L1-L3 all look fine (gateway pings, DNS may resolve) but a
        // portal is intercepting traffic. High priority + very specific/actionable —
        // a pure ping/DNS check would wrongly say "internet down / WAN issue".
        if (tests.CaptivePortal.IsCritical && gatewayOk)
        {
            return new DiagnosisVerdict
            {
                Title = "Captive portal detected — sign-in required",
                Severity = "Warning",
                Confidence = "High",
                Evidence =
                [
                    "Gateway is reachable and the local network is up.",
                    tests.CaptivePortal.Details,
                    "Traffic is being intercepted by a sign-in / terms page (hotel, guest Wi-Fi, public hotspot)."
                ],
                Limitations = ["The app cannot complete the portal sign-in for you."],
                Recommendations =
                [
                    "Open a browser and load any HTTP site (e.g. http://neverssl.com) — the portal page should appear.",
                    "Complete the sign-in / accept the terms, then re-run the diagnosis.",
                    "If no portal page appears, the network may be filtering outbound traffic — contact the network owner."
                ]
            };
        }

        if (!result.IpConfiguration.HasDnsServers || tests.DnsServer.IsCritical)
        {
            return new DiagnosisVerdict
            {
                Title = "DNS servers missing or unreachable",
                Severity = "Critical",
                Confidence = !result.IpConfiguration.HasDnsServers ? "High" : "Medium",
                Evidence =
                [
                    result.IpConfiguration.HasDnsServers
                        ? $"Configured DNS servers: {string.Join(", ", result.IpConfiguration.DnsServers)}."
                        : "No DNS servers are configured on the active adapter.",
                    StatusEvidence("DNS server", tests.DnsServer)
                ],
                Limitations =
                [
                    "The app does not change DNS client settings in v1.0."
                ],
                Recommendations =
                [
                    "Check DHCP DNS options or static DNS configuration.",
                    "Check DNS server reachability and firewall policy."
                ]
            };
        }

        if (packetLossDetected)
        {
            return new DiagnosisVerdict
            {
                Title = "Packet loss / unstable local network path",
                Severity = "Critical",
                Confidence = tests.GatewayPacketLoss.IsCritical ? "High" : "Medium",
                Evidence =
                [
                    PacketLossEvidence("Gateway", tests.GatewayPacketLoss),
                    PacketLossEvidence("Internet", tests.InternetPacketLoss),
                    StatusEvidence("Gateway", tests.Gateway)
                ],
                Limitations =
                [
                    "Packet loss is measured from this PC with a small sample in v1.0."
                ],
                Recommendations =
                [
                    "Check cable, Wi-Fi quality, switch port errors, VLAN path and gateway health.",
                    "Repeat the test and compare with another PC on the same network."
                ]
            };
        }

        if (isEthernet && hasIncreasingAdapterErrors)
        {
            return new DiagnosisVerdict
            {
                Title = "Likely active physical link quality issue",
                Severity = "Critical",
                Confidence = "High",
                Evidence =
                [
                    $"Adapter counters increased during sampling: errors/s {FormatNullable(adapter.ErrorsPerSecond)}, discards/s {FormatNullable(adapter.DiscardsPerSecond)}.",
                    $"Ethernet link speed is {adapter.LinkSpeed}.",
                    StatusEvidence("Gateway", tests.Gateway),
                    StatusEvidence("DNS", tests.DnsResolution)
                ],
                Limitations =
                [
                    "Switch port configuration still requires SNMP or direct switch access for confirmation."
                ],
                Recommendations =
                [
                    "Check cable, wall socket, docking station, patch panel and switch port errors.",
                    "Compare with a known-good cable and check switch interface counters."
                ]
            };
        }

        if (isEthernet && linkIsSlow && hasAdapterErrors)
        {
            return new DiagnosisVerdict
            {
                Title = "Likely physical link quality issue",
                Severity = "Warning",
                Confidence = "High",
                Evidence =
                [
                    $"Ethernet link speed is {adapter.LinkSpeed}; expected minimum is {result.Profile.ExpectedMinimumEthernetMbps} Mbps.",
                    $"Adapter counters show {adapter.Errors} errors and {adapter.Discards} discards.",
                    StatusEvidence("Gateway", tests.Gateway),
                    StatusEvidence("DNS", tests.DnsResolution)
                ],
                Limitations =
                [
                    "Switch port configuration cannot be confirmed from this PC without SNMP or direct switch access."
                ],
                Recommendations =
                [
                    "Test another cable, wall socket, docking station and switch port.",
                    "Check switch interface speed, duplex, errors and discards."
                ]
            };
        }

        if (isEthernet && linkIsSlow && gatewayOk && dnsOk && internetOk)
        {
            return new DiagnosisVerdict
            {
                Title = "Possible Ethernet link speed / cable / switch port issue",
                Severity = "Warning",
                Confidence = "Medium",
                Evidence =
                [
                    $"Ethernet link speed is {adapter.LinkSpeed}; expected minimum is {result.Profile.ExpectedMinimumEthernetMbps} Mbps.",
                    StatusEvidence("Gateway", tests.Gateway),
                    StatusEvidence("DNS", tests.DnsResolution),
                    StatusEvidence("Internet", tests.InternetPing)
                ],
                Limitations =
                [
                    "This PC can see the negotiated local link speed, but cannot confirm the real switch-side configuration without SNMP or switch access."
                ],
                Recommendations =
                [
                    "Check cable, wall socket, docking station, patch panel or switch port speed/duplex.",
                    "If this device should be 1 Gbps, test with a known-good cable first."
                ]
            };
        }

        if (isWifi && wifi?.SignalPercent.HasValue == true && WifiSignalClassifier.GetSeverity(wifi.SignalPercent) == "Critical")
        {
            return new DiagnosisVerdict
            {
                Title = "Weak Wi-Fi signal or local access point issue",
                Severity = "Critical",
                Confidence = "High",
                Evidence =
                [
                    $"Wi-Fi signal is {wifi.SignalPercent}%.",
                    StatusEvidence("Gateway", tests.Gateway),
                    $"SSID is {wifi.Ssid}, channel is {wifi.Channel?.ToString() ?? "Unknown"}."
                ],
                Limitations =
                [
                    "Nearby channel congestion is estimated from local Wi-Fi information only in v1.0."
                ],
                Recommendations =
                [
                    "Move closer to the access point or test with Ethernet.",
                    "Check AP placement, band selection and channel utilization."
                ]
            };
        }

        if (isWifi && wifi?.SignalPercent.HasValue == true && WifiSignalClassifier.GetSeverity(wifi.SignalPercent) == "Warning")
        {
            return new DiagnosisVerdict
            {
                Title = "Moderate Wi-Fi signal may affect performance",
                Severity = "Warning",
                Confidence = "Medium",
                Evidence =
                [
                    $"Wi-Fi signal is {wifi.SignalPercent}%.",
                    $"SSID is {wifi.Ssid}, radio type is {wifi.RadioType}, channel is {wifi.Channel?.ToString() ?? "Unknown"}."
                ],
                Limitations =
                [
                    "Nearby channel congestion and roaming history require deeper WLAN report analysis."
                ],
                Recommendations =
                [
                    "Test from another location or compare with Ethernet.",
                    "Prefer 5 GHz/6 GHz if available and check access point placement."
                ]
            };
        }

        if (gatewayOk && internetIpOk && IsDnsIssue(tests))
        {
            return new DiagnosisVerdict
            {
                Title = "Likely DNS issue",
                Severity = "Critical",
                Confidence = "High",
                Evidence =
                [
                    StatusEvidence("Gateway", tests.Gateway),
                    InternetPingEvidence(tests),
                    StatusEvidence("DNS lookup", tests.DnsResolution),
                    StatusEvidence("Internet DNS lookup", tests.InternetDnsResolution)
                ],
                Limitations =
                [
                    "The app does not change DNS settings in v1.0."
                ],
                Recommendations =
                [
                    "Check configured DNS servers, DNS service health and conditional forwarding.",
                    "Compare internal and external DNS resolution."
                ]
            };
        }

        if (gatewayOk && HasInternetIpTargets(tests) && AllInternetIpPingsFailed(tests))
        {
            return new DiagnosisVerdict
            {
                Title = "Possible WAN, firewall or upstream internet issue",
                Severity = "Warning",
                Confidence = "Medium",
                Evidence =
                [
                    StatusEvidence("Gateway", tests.Gateway),
                    InternetPingEvidence(tests),
                    StatusEvidence("External TCP 443", tests.ExternalTcp443),
                    StatusEvidence("HTTPS GET", tests.HttpsGet)
                ],
                Limitations =
                [
                    "External ICMP can be blocked by policy, so TCP 443 and HTTPS results should be reviewed with the ping results."
                ],
                Recommendations =
                [
                    "Check firewall WAN status, upstream provider connectivity and outbound policy.",
                    "Compare HTTPS access from another PC on the same network."
                ]
            };
        }

        if (gatewayOk && HasPartialInternetIpFailure(tests))
        {
            return new DiagnosisVerdict
            {
                Title = "Partial upstream or filtering issue suspected",
                Severity = "Warning",
                Confidence = "Medium",
                Evidence =
                [
                    StatusEvidence("Gateway", tests.Gateway),
                    InternetPingEvidence(tests),
                    StatusEvidence("External TCP 443", tests.ExternalTcp443),
                    StatusEvidence("HTTPS GET", tests.HttpsGet)
                ],
                Limitations =
                [
                    "Different public targets may respond differently to ICMP. Treat this as a path/filtering suspicion, not proof of ISP failure."
                ],
                Recommendations =
                [
                    "Check firewall rules, DNS filtering, upstream routing and compare with another internet target.",
                    "If HTTPS works, prioritize application-specific testing."
                ]
            };
        }

        if (gatewayOk && internetIpOk && dnsOk && (tests.ExternalTcp443.IsCritical || tests.HttpsGet.IsCritical))
        {
            return new DiagnosisVerdict
            {
                Title = "Browsing or HTTPS access issue suspected",
                Severity = "Warning",
                Confidence = "Medium",
                Evidence =
                [
                    StatusEvidence("Gateway", tests.Gateway),
                    InternetPingEvidence(tests),
                    StatusEvidence("External TCP 443", tests.ExternalTcp443),
                    StatusEvidence("HTTPS GET", tests.HttpsGet)
                ],
                Limitations =
                [
                    "HTTPS may fail because of proxy, TLS inspection, captive portal or endpoint-specific blocking."
                ],
                Recommendations =
                [
                    "Check proxy settings, firewall HTTPS policy, TLS inspection and captive portal conditions.",
                    "Test a browser session and compare with another PC."
                ]
            };
        }

        if ((tests.Gateway.IsCritical || tests.Gateway.IsWarning) &&
            (tests.DnsResolution.IsCritical || tests.InternetPing.IsCritical))
        {
            return new DiagnosisVerdict
            {
                Title = "Possible local network, VLAN or gateway issue",
                Severity = tests.Gateway.IsCritical ? "Critical" : "Warning",
                Confidence = "Medium",
                Evidence =
                [
                    StatusEvidence("Gateway", tests.Gateway),
                    StatusEvidence("DNS", tests.DnsResolution),
                    StatusEvidence("Internet", tests.InternetPing)
                ],
                Limitations =
                [
                    "The app cannot inspect router, VLAN or firewall configuration from the local PC."
                ],
                Recommendations =
                [
                    "Check gateway availability, VLAN assignment, DHCP scope and firewall path.",
                    "Compare with another device on the same wall port or Wi-Fi network."
                ]
            };
        }

        if (gatewayOk && dnsOk && tests.InternetPing.IsCritical)
        {
            return new DiagnosisVerdict
            {
                Title = "Possible WAN, firewall or upstream internet issue",
                Severity = "Warning",
                Confidence = "Medium",
                Evidence =
                [
                    StatusEvidence("Gateway", tests.Gateway),
                    StatusEvidence("DNS", tests.DnsResolution),
                    StatusEvidence("Internet IP", tests.InternetPing)
                ],
                Limitations =
                [
                    "External ICMP may be blocked, so internet reachability should also be validated with browser or HTTPS checks."
                ],
                Recommendations =
                [
                    "Check firewall policy, WAN status and upstream provider connectivity.",
                    "Test HTTPS 443 to a known external target."
                ]
            };
        }

        if (result.LastPortTest is { PingSucceeded: true, TcpSucceeded: false } portTest)
        {
            return new DiagnosisVerdict
            {
                Title = "Host reachable but service port is closed or blocked",
                Severity = "Warning",
                Confidence = "High",
                Evidence =
                [
                    $"Target {portTest.Target} replied to ping.",
                    $"TCP port {portTest.Port} failed.",
                    portTest.Verdict
                ],
                Limitations =
                [
                    "ICMP and TCP results can be affected by firewall policy, so service status should be checked on the target."
                ],
                Recommendations =
                [
                    "Check Windows Firewall, network firewall rules, service status and VLAN restrictions.",
                    "Confirm the service is listening on the expected port."
                ]
            };
        }

        // "Could not measure" is NOT "healthy". If we reach the fallback but the
        // connectivity collector did not complete (timeout/failure), say so explicitly
        // instead of implying the network is fine.
        var connectivityIncomplete =
            !tests.CollectorStatus.Equals("OK", StringComparison.OrdinalIgnoreCase);
        if (connectivityIncomplete)
        {
            return new DiagnosisVerdict
            {
                Title = "Diagnosis incomplete — connectivity could not be measured",
                Severity = "Warning",
                Confidence = "Low",
                Evidence =
                [
                    $"Active adapter: {adapter.Name} ({adapter.ConnectionType}).",
                    $"IP address: {result.IpConfiguration.IpAddress}.",
                    $"Connectivity collector: {tests.CollectorStatus}",
                    "Gateway / DNS / internet checks did not return results, so the network state is unknown."
                ],
                Limitations =
                [
                    "This is NOT a clean bill of health — the connectivity probe timed out or failed before completing.",
                    .. result.CollectorWarnings
                ],
                Recommendations =
                [
                    "Re-run the diagnosis (a one-off timeout is common on a busy or slow link).",
                    "If it keeps timing out, the path to the gateway/DNS is likely blocked or extremely slow — check cabling, Wi-Fi association and local firewall.",
                    "Ensure the Windows PowerShell engine is available (run the app as a normal user; some hardened machines block it)."
                ]
            };
        }

        return new DiagnosisVerdict
        {
            Title = "No clear network fault detected",
            Severity = result.CollectorWarnings.Count > 0 ? "Warning" : "OK",
            Confidence = result.CollectorWarnings.Count > 0 ? "Medium" : "High",
            Evidence =
            [
                $"Active adapter: {adapter.Name} ({adapter.ConnectionType}).",
                $"Health score: {result.HealthScore.Score}/100 ({result.HealthScore.Status}).",
                $"IP address: {result.IpConfiguration.IpAddress}.",
                StatusEvidence("Gateway", tests.Gateway),
                StatusEvidence("DNS", tests.DnsResolution),
                StatusEvidence("Internet", tests.InternetPing)
            ],
            Limitations = result.CollectorWarnings.Count > 0
                ? [.. result.CollectorWarnings]
                : ["Only local PC and manually selected targets are diagnosed in v1.0."],
            Recommendations =
            [
                "If the user still reports slowness, test the specific internal server or service port.",
                "Compare with another PC on the same network segment."
            ]
        };
    }

    private static string StatusEvidence(string name, TestResult result)
    {
        var latency = result.LatencyMs.HasValue ? $" ({result.LatencyMs.Value:N1} ms)" : string.Empty;
        return $"{name}: {result.Status}{latency}. {result.Details}".Trim();
    }

    private static bool IsPacketLossProblem(PacketLossResult result)
    {
        return result.LossPercent.HasValue && result.LossPercent.Value > 5;
    }

    private static string PacketLossEvidence(string name, PacketLossResult result)
    {
        return result.LossPercent.HasValue
            ? $"{name} packet loss: {result.LossPercent:N1}% ({result.Received}/{result.Sent} replies)."
            : $"{name} packet loss: {result.Status}. {result.Details}";
    }

    private static bool IsDnsIssue(ConnectivityTests tests)
    {
        return tests.DnsResolution.IsCritical ||
            tests.InternetDnsResolution.IsCritical ||
            tests.InternetPingResults.Any(result =>
                DiagnosticConstants.DnsTestHostnames.Contains(result.Target, StringComparer.OrdinalIgnoreCase) &&
                result.IsCritical);
    }

    private static bool HasInternetIpTargets(ConnectivityTests tests) =>
        tests.InternetPingResults.Any(result => !DiagnosticConstants.DnsTestHostnames.Contains(result.Target, StringComparer.OrdinalIgnoreCase));

    private static bool AnyInternetIpPingOk(ConnectivityTests tests)
    {
        var ipTargets = tests.InternetPingResults
            .Where(result => !DiagnosticConstants.DnsTestHostnames.Contains(result.Target, StringComparer.OrdinalIgnoreCase))
            .ToList();

        return ipTargets.Count > 0
            ? ipTargets.Any(result => result.IsOk || result.IsWarning)
            : tests.InternetPing.IsOk || tests.InternetPing.IsWarning;
    }

    private static bool AllInternetIpPingsFailed(ConnectivityTests tests)
    {
        var ipTargets = tests.InternetPingResults
            .Where(result => !DiagnosticConstants.DnsTestHostnames.Contains(result.Target, StringComparer.OrdinalIgnoreCase))
            .ToList();

        return ipTargets.Count > 0
            ? ipTargets.All(result => result.IsCritical)
            : tests.InternetPing.IsCritical;
    }

    private static bool HasPartialInternetIpFailure(ConnectivityTests tests)
    {
        var ipTargets = tests.InternetPingResults
            .Where(result => !DiagnosticConstants.DnsTestHostnames.Contains(result.Target, StringComparer.OrdinalIgnoreCase))
            .ToList();

        return ipTargets.Any(result => result.IsCritical) &&
            ipTargets.Any(result => result.IsOk || result.IsWarning);
    }

    private static string InternetPingEvidence(ConnectivityTests tests)
    {
        if (tests.InternetPingResults.Count == 0)
        {
            return StatusEvidence("Internet pings", tests.InternetPing);
        }

        return "Internet pings: " + string.Join("; ", tests.InternetPingResults.Select(result =>
        {
            var latency = result.LatencyMs.HasValue ? $" {result.LatencyMs.Value:N1} ms" : string.Empty;
            return $"{result.Target}={result.Status}{latency}";
        })) + ".";
    }

    private static string FormatNullable(double? value) => DiagnosticHelpers.FormatNullable(value);
}
