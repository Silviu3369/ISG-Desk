using System.Diagnostics;
using NetScopeDiagnosticCenter.Collectors;
using NetScopeDiagnosticCenter.Core.Models;
using NetScopeDiagnosticCenter.Infrastructure;

namespace NetScopeDiagnosticCenter.Core;

public sealed class DiagnosticEngine
{
    private readonly NetworkAdapterCollector _networkAdapterCollector;
    private readonly IpConfigurationCollector _ipConfigurationCollector;
    private readonly DnsCollector _dnsCollector;
    private readonly TraceRouteCollector _traceRouteCollector;
    private readonly WifiCollector _wifiCollector;
    private readonly DomainCollector _domainCollector;
    private readonly DhcpCollector _dhcpCollector;
    private readonly PcContextCollector _pcContextCollector;
    private readonly RuleEngine _ruleEngine;
    private readonly HealthScoreCalculator _healthScoreCalculator;
    private readonly AppStorageService _appStorage;
    private readonly LoggingService _loggingService;

    public DiagnosticEngine(
        NetworkAdapterCollector networkAdapterCollector,
        IpConfigurationCollector ipConfigurationCollector,
        DnsCollector dnsCollector,
        TraceRouteCollector traceRouteCollector,
        WifiCollector wifiCollector,
        DomainCollector domainCollector,
        DhcpCollector dhcpCollector,
        PcContextCollector pcContextCollector,
        RuleEngine ruleEngine,
        HealthScoreCalculator healthScoreCalculator,
        AppStorageService appStorage,
        LoggingService loggingService)
    {
        _networkAdapterCollector = networkAdapterCollector;
        _ipConfigurationCollector = ipConfigurationCollector;
        _dnsCollector = dnsCollector;
        _traceRouteCollector = traceRouteCollector;
        _wifiCollector = wifiCollector;
        _domainCollector = domainCollector;
        _dhcpCollector = dhcpCollector;
        _pcContextCollector = pcContextCollector;
        _ruleEngine = ruleEngine;
        _healthScoreCalculator = healthScoreCalculator;
        _appStorage = appStorage;
        _loggingService = loggingService;
    }

    public async Task<NetworkDiagnosisResult> RunQuickDiagnosisAsync(CancellationToken cancellationToken = default)
    {
        var result = new NetworkDiagnosisResult();

        try
        {
            result.Profile = _appStorage.LoadProfile();
            var stepWatch = Stopwatch.StartNew();
            result.Adapter = await _networkAdapterCollector.GetActiveAdapterAsync(cancellationToken).ConfigureAwait(false);
            stepWatch.Stop();
            AddWarningIfNeeded(result, result.Adapter.CollectorStatus);
            result.Steps.Add(BuildAdapterStep(result.Adapter, stepWatch.Elapsed.TotalMilliseconds));

            stepWatch.Restart();
            result.IpConfiguration = await _ipConfigurationCollector.GetIpConfigurationAsync(result.Adapter.Name, cancellationToken).ConfigureAwait(false);
            result.Dhcp = await _dhcpCollector.GetDhcpInfoAsync(result.Adapter.Name, cancellationToken).ConfigureAwait(false);
            stepWatch.Stop();
            AddWarningIfNeeded(result, result.IpConfiguration.CollectorStatus);
            AddWarningIfNeeded(result, result.Dhcp.CollectorStatus);
            result.Steps.Add(BuildIpDhcpStep(result.IpConfiguration, result.Dhcp, stepWatch.Elapsed.TotalMilliseconds));

            if (result.Adapter.ConnectionType == "Wi-Fi" ||
                result.Adapter.InterfaceDescription.Contains("wireless", StringComparison.OrdinalIgnoreCase) ||
                result.Adapter.Name.Contains("wi-fi", StringComparison.OrdinalIgnoreCase) ||
                result.Adapter.Name.Contains("wifi", StringComparison.OrdinalIgnoreCase))
            {
                result.Adapter.ConnectionType = "Wi-Fi";
                stepWatch.Restart();
                result.Wifi = await _wifiCollector.GetWifiInfoAsync(
                    result.Adapter.Name,
                    result.Adapter.InterfaceDescription,
                    cancellationToken).ConfigureAwait(false);
                stepWatch.Stop();
                if (result.Wifi is not null)
                {
                    AddWarningIfNeeded(result, result.Wifi.CollectorStatus);
                }
                result.Steps.Add(BuildWifiStep(result.Wifi, stepWatch.Elapsed.TotalMilliseconds));
            }
            else
            {
                result.Steps.Add(new DiagnosisStepResult
                {
                    Name = "Wi-Fi",
                    Layer = "Wi-Fi",
                    Status = "Unknown",
                    DurationMs = 0,
                    Evidence = ["Active adapter is not Wi-Fi."],
                    Recommendation = "Use Wi-Fi Analyzer only when the active connection is wireless."
                });
            }

            stepWatch.Restart();
            result.Tests = await _dnsCollector.RunBasicTestsAsync(
                result.IpConfiguration.Gateway,
                result.IpConfiguration.DnsServers,
                cancellationToken).ConfigureAwait(false);
            stepWatch.Stop();
            // CRITICAL: a timed-out/failed connectivity collector must register a warning
            // — otherwise the rule engine's fallback reports a hung network as "OK".
            AddWarningIfNeeded(result, result.Tests.CollectorStatus);
            result.Steps.Add(BuildGatewayStep(result.Tests, result.IpConfiguration, stepWatch.Elapsed.TotalMilliseconds));
            result.Steps.Add(BuildDnsStep(result.Tests, result.IpConfiguration, stepWatch.Elapsed.TotalMilliseconds));
            result.Steps.Add(BuildInternetStep(result.Tests, stepWatch.Elapsed.TotalMilliseconds));

            // Path visibility: trace a public beacon so we can attribute a fault to LOCAL
            // vs ISP. Only worth running when the internet is actually unreachable — a
            // healthy path adds ~5-15 s of hops for no diagnostic value.
            var internetReachable = result.Tests.InternetPing.IsOk
                                    || result.Tests.InternetDnsResolution.IsOk
                                    || result.Tests.HttpsGet.IsOk;
            if (!internetReachable && result.IpConfiguration.HasValidIp)
            {
                stepWatch.Restart();
                result.TraceRoute = await _traceRouteCollector.TraceAsync("1.1.1.1", cancellationToken).ConfigureAwait(false);
                stepWatch.Stop();
                AddWarningIfNeeded(result, result.TraceRoute.CollectorStatus);
                result.Steps.Add(BuildTraceRouteStep(result.TraceRoute, stepWatch.Elapsed.TotalMilliseconds));
            }

            result.Steps.Add(BuildLinkQualityStep(result.Adapter, result.Profile));

            stepWatch.Restart();
            result.Domain = await _domainCollector.GetDomainInfoAsync(cancellationToken).ConfigureAwait(false);
            result.PcContext = await _pcContextCollector.GetContextAsync(cancellationToken).ConfigureAwait(false);
            stepWatch.Stop();
            AddWarningIfNeeded(result, result.Domain.CollectorStatus);
            AddWarningIfNeeded(result, result.PcContext.CollectorStatus);
            result.Steps.Add(BuildPcContextStep(result.Domain, result.PcContext, stepWatch.Elapsed.TotalMilliseconds));

            result.HealthScore = _healthScoreCalculator.Calculate(result);
            result.Verdict = _ruleEngine.Evaluate(result);
            result.Verdict.AffectedLayer = InferAffectedLayer(result.Verdict.Title);
            result.Steps.Add(BuildVerdictStep(result));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _loggingService.Error("Quick diagnosis failed.", ex);
            result.CollectorWarnings.Add(ex.Message);
            result.Verdict = new DiagnosisVerdict
            {
                Title = "Diagnosis failed",
                Severity = "Critical",
                Confidence = "Low",
                AffectedLayer = "Diagnostic Engine",
                Evidence = ["One or more collectors failed unexpectedly."],
                Limitations = ["The result is incomplete because data collection did not finish."],
                Recommendations = ["Check the application log and run again as administrator."]
            };
            result.HealthScore = _healthScoreCalculator.Calculate(result);
            result.Steps.Add(new DiagnosisStepResult
            {
                Name = "Verdict",
                Layer = "Diagnostic Engine",
                Status = "Critical",
                Evidence = ["Quick diagnosis did not complete."],
                Error = ex.Message,
                Recommendation = "Check the application log and run again as administrator."
            });
        }

        return result;
    }

    private static void AddWarningIfNeeded(NetworkDiagnosisResult result, string? collectorStatus)
    {
        if (!string.IsNullOrWhiteSpace(collectorStatus) &&
            !collectorStatus.Equals("OK", StringComparison.OrdinalIgnoreCase))
        {
            result.CollectorWarnings.Add(collectorStatus);
        }
    }

    private static DiagnosisStepResult BuildAdapterStep(AdapterInfo adapter, double durationMs)
    {
        var status = IsCollectorOk(adapter.CollectorStatus) ? "OK" : "Critical";
        return new DiagnosisStepResult
        {
            Name = "Adapter",
            Layer = "PC",
            Status = status,
            DurationMs = durationMs,
            Evidence =
            [
                "Source: live Windows adapter snapshot collected during this Quick Diagnosis run.",
                $"Adapter: {adapter.Name}; description: {adapter.InterfaceDescription}.",
                $"MAC: {adapter.MacAddress}; driver: {adapter.DriverInformation}.",
                $"Type: {adapter.ConnectionType}; status: {adapter.Status}.",
                $"Link speed: {adapter.LinkSpeed}; speed/duplex: {adapter.SpeedDuplex}.",
                $"Counters: {adapter.Errors} errors, {adapter.Discards} discards.",
                $"Sample: {adapter.SamplingStatus}; errors/s {FormatNullable(adapter.ErrorsPerSecond)}, discards/s {FormatNullable(adapter.DiscardsPerSecond)}."
            ],
            Error = status == "OK" ? string.Empty : adapter.CollectorStatus,
            Recommendation = status == "OK"
                ? "Adapter was detected and can be used for local diagnosis."
                : "Check adapter state, driver and administrator permissions."
        };
    }

    private static DiagnosisStepResult BuildIpDhcpStep(IpConfigurationInfo ip, DhcpInfo dhcp, double durationMs)
    {
        var status = !ip.HasValidIp || !ip.HasGateway
            ? "Critical"
            : !ip.HasDnsServers || !IsCollectorOk(ip.CollectorStatus) || !IsCollectorOk(dhcp.CollectorStatus)
                ? "Warning"
                : "OK";
        return new DiagnosisStepResult
        {
            Name = "IP / DHCP",
            Layer = "PC / DHCP",
            Status = status,
            DurationMs = durationMs,
            Evidence =
            [
                "Source: live Windows IP/DHCP snapshot collected during this Quick Diagnosis run.",
                $"Interface: {ip.InterfaceAlias}; DHCP adapter: {dhcp.AdapterName}.",
                $"IPv4: {ip.IpAddress}; gateway: {ip.Gateway}.",
                $"DNS servers: {(ip.DnsServers.Count == 0 ? "None" : string.Join(", ", ip.DnsServers))}.",
                $"DHCP enabled: {FormatBool(dhcp.DhcpEnabled)}; DHCP server: {dhcp.DhcpServer}.",
                $"Lease: {dhcp.LeaseObtained} -> {dhcp.LeaseExpires}."
            ],
            Error = FirstCollectorError(ip.CollectorStatus, dhcp.CollectorStatus),
            Recommendation = status switch
            {
                "Critical" => "Check DHCP/static IP configuration, VLAN, cable/Wi-Fi association and default gateway.",
                "Warning" => "Validate DNS/DHCP options and compare with a known-good PC on the same network.",
                _ => "IP, gateway and DNS configuration look usable."
            }
        };
    }

    private static DiagnosisStepResult BuildGatewayStep(ConnectivityTests tests, IpConfigurationInfo ip, double durationMs)
    {
        var status = WorstStatus(tests.Gateway.Status, tests.GatewayPacketLoss.Status);
        return new DiagnosisStepResult
        {
            Name = "Gateway",
            Layer = "Gateway",
            Status = status,
            DurationMs = durationMs,
            Evidence =
            [
                "Source: live gateway target from the current IP configuration.",
                $"Gateway target: {ip.Gateway}; interface: {ip.InterfaceAlias}.",
                FormatTestEvidence("Gateway ping", tests.Gateway),
                FormatPacketLossEvidence("Gateway packet loss", tests.GatewayPacketLoss),
                $"Connectivity collector: {tests.CollectorStatus}."
            ],
            Error = status == "Critical"
                ? DiagnosticHelpers.FirstNonEmpty(tests.Gateway.Details, tests.GatewayPacketLoss.Details)
                : IsCollectorOk(tests.CollectorStatus) ? string.Empty : tests.CollectorStatus,
            Recommendation = status switch
            {
                "Critical" => "Check local gateway, VLAN, default route, switch path or local firewall.",
                "Warning" => "Repeat the test and compare latency/loss with another PC on the same network.",
                "OK" => "Gateway path is responding.",
                _ => "Gateway check could not complete; re-run Quick Diagnosis and verify the default gateway on the active adapter."
            }
        };
    }

    private static DiagnosisStepResult BuildDnsStep(ConnectivityTests tests, IpConfigurationInfo ip, double durationMs)
    {
        var status = !ip.HasDnsServers
            ? "Critical"
            : !IsCollectorOk(tests.CollectorStatus)
                ? "Warning"
                : WorstStatus(tests.DnsServer.Status, tests.DnsResolution.Status, tests.InternetDnsResolution.Status);
        var evidence = new List<string>
        {
            "Source: live DNS server list from the current IP configuration.",
            $"Interface: {ip.InterfaceAlias}; configured DNS servers: {(ip.DnsServers.Count == 0 ? "None" : string.Join(", ", ip.DnsServers))}.",
            FormatTestEvidence("DNS server aggregate", tests.DnsServer),
            FormatTestEvidence("External DNS lookup", tests.DnsResolution),
            FormatTestEvidence("Internet DNS lookup", tests.InternetDnsResolution),
            $"Connectivity collector: {tests.CollectorStatus}."
        };
        evidence.AddRange(tests.DnsServerResults.Select(result => FormatTestEvidence(result.Target, result)));

        return new DiagnosisStepResult
        {
            Name = "DNS",
            Layer = "DNS",
            Status = status,
            DurationMs = durationMs,
            Evidence = evidence,
            Error = status == "Critical"
                ? DiagnosticHelpers.FirstNonEmpty(
                    !ip.HasDnsServers ? "No configured DNS servers on the active adapter." : string.Empty,
                    FirstCriticalDetails(tests.DnsServer, tests.DnsResolution, tests.InternetDnsResolution))
                : IsCollectorOk(tests.CollectorStatus) ? string.Empty : tests.CollectorStatus,
            Recommendation = status switch
            {
                "Critical" => "Check DNS server reachability, DHCP DNS options, DNS service health and forwarders.",
                "Warning" => "Inspect the DNS server with warning/critical latency or failures.",
                "OK" => "DNS resolution is responding.",
                _ => "DNS check could not complete; re-run Quick Diagnosis and verify DNS servers on the active adapter."
            }
        };
    }

    private static DiagnosisStepResult BuildInternetStep(ConnectivityTests tests, double durationMs)
    {
        var status = WorstStatus(tests.InternetPing.Status, tests.ExternalTcp443.Status, tests.HttpsGet.Status, tests.InternetPacketLoss.Status);
        var evidence = new List<string>
        {
            FormatTestEvidence("Internet IP aggregate", tests.InternetPing),
            FormatPacketLossEvidence("Internet packet loss", tests.InternetPacketLoss),
            FormatTestEvidence("External TCP 443", tests.ExternalTcp443),
            FormatTestEvidence("HTTPS GET", tests.HttpsGet)
        };
        evidence.AddRange(tests.InternetPingResults.Select(result => FormatTestEvidence(result.Target, result)));

        return new DiagnosisStepResult
        {
            Name = "Internet",
            Layer = "Firewall / WAN / Internet",
            Status = status,
            DurationMs = durationMs,
            Evidence = evidence,
            Error = status == "Critical" ? FirstCriticalDetails(tests.InternetPing, tests.ExternalTcp443, tests.HttpsGet) : string.Empty,
            Recommendation = status switch
            {
                "Critical" => "Check firewall/WAN path, upstream internet and outbound HTTPS policy.",
                "Warning" => "Investigate partial upstream filtering, high latency, packet loss or HTTPS inspection.",
                _ => "Internet reachability and HTTPS path look usable."
            }
        };
    }

    private static DiagnosisStepResult BuildTraceRouteStep(TraceRouteResult trace, double durationMs)
    {
        var evidence = new List<string> { trace.Summary };
        evidence.AddRange(trace.Hops.Select(h => $"Hop {h.Hop}: {h.Address} ({h.Scope})"));
        if (!trace.CollectorStatus.Equals("OK", StringComparison.OrdinalIgnoreCase))
            evidence.Add(trace.CollectorStatus);

        return new DiagnosisStepResult
        {
            Name = "Path (traceroute)",
            Layer = "Routing / WAN path",
            Status = trace.Status,
            DurationMs = durationMs,
            Evidence = evidence,
            Error = trace.Status == "Critical" ? trace.Summary : string.Empty,
            Recommendation = trace.Status switch
            {
                "Critical" => "The break is on the LOCAL side — check cabling, switch port, VLAN and the default gateway.",
                "Warning" => "The break is upstream of the LAN — this is an ISP / provider issue, escalate to them with the last responding hop.",
                "OK" => "The full path to the internet beacon is intact.",
                _ => "Path could not be traced (network may be black-holing packets)."
            }
        };
    }

    private static DiagnosisStepResult BuildLinkQualityStep(AdapterInfo adapter, NetworkProfile profile)
    {
        var slowEthernet = adapter.ConnectionType.Equals("Ethernet", StringComparison.OrdinalIgnoreCase) &&
            adapter.LinkSpeedMbps.HasValue &&
            adapter.LinkSpeedMbps.Value < profile.ExpectedMinimumEthernetMbps;
        var increasingCounters = (adapter.ErrorsPerSecond ?? 0) > 0 || (adapter.DiscardsPerSecond ?? 0) > 0;
        var cumulativeCounters = adapter.Errors > 0 || adapter.Discards > 0;
        var status = increasingCounters ? "Critical" : slowEthernet || cumulativeCounters ? "Warning" : "OK";

        return new DiagnosisStepResult
        {
            Name = "Link Quality",
            Layer = adapter.ConnectionType.Equals("Wi-Fi", StringComparison.OrdinalIgnoreCase) ? "Wi-Fi" : "Cable / Switch Port",
            Status = status,
            DurationMs = 0,
            Evidence =
            [
                $"Link speed: {adapter.LinkSpeed}; expected minimum Ethernet speed: {profile.ExpectedMinimumEthernetMbps} Mbps.",
                $"Speed/duplex: {adapter.SpeedDuplex}.",
                $"Counters: {adapter.Errors} errors, {adapter.Discards} discards.",
                $"Sample: {adapter.SamplingStatus}; errors/s {FormatNullable(adapter.ErrorsPerSecond)}, discards/s {FormatNullable(adapter.DiscardsPerSecond)}."
            ],
            Recommendation = status switch
            {
                "Critical" => "Check cable, dock, wall socket, switch port and switch-side interface counters.",
                "Warning" => "Treat cumulative counters as evidence to investigate; confirm with switch-side counters or repeated sampling.",
                _ => "No obvious local link quality issue detected."
            }
        };
    }

    private static DiagnosisStepResult BuildWifiStep(WifiInfo? wifi, double durationMs)
    {
        if (wifi is null)
        {
            return new DiagnosisStepResult
            {
                Name = "Wi-Fi",
                Layer = "Wi-Fi",
                Status = "Unknown",
                DurationMs = durationMs,
                Evidence = ["Wi-Fi collector returned no data."],
                Error = "Wi-Fi information unavailable.",
                Recommendation = "Run Wi-Fi Analyzer if the active connection should be wireless."
            };
        }

        var status = !IsCollectorOk(wifi.CollectorStatus)
            ? "Warning"
            : wifi.SignalPercent.HasValue
                ? WifiSignalClassifier.GetSeverity(wifi.SignalPercent)
                : "OK";
        return new DiagnosisStepResult
        {
            Name = "Wi-Fi",
            Layer = "Wi-Fi",
            Status = status,
            DurationMs = durationMs,
            Evidence =
            [
                "Source: live Windows WLAN snapshot collected during this Quick Diagnosis run.",
                $"Interface: {wifi.InterfaceName}; description: {wifi.InterfaceDescription}.",
                $"SSID: {wifi.Ssid}; BSSID: {wifi.Bssid}.",
                $"Signal: {(wifi.SignalPercent.HasValue ? wifi.SignalPercent + "%" : "Unknown")}; channel: {wifi.Channel?.ToString() ?? "Unknown"}.",
                $"Radio: {wifi.RadioType}; rates: Rx {wifi.ReceiveRateMbps} Mbps / Tx {wifi.TransmitRateMbps} Mbps.",
                $"Authentication: {wifi.Authentication}."
            ],
            Error = IsCollectorOk(wifi.CollectorStatus) ? string.Empty : wifi.CollectorStatus,
            Recommendation = status switch
            {
                "Critical" => "Move closer to the AP, test Ethernet and inspect AP placement/channel load.",
                "Warning" => "Compare another location and prefer 5 GHz/6 GHz where available.",
                _ => "Wi-Fi session information looks usable."
            }
        };
    }

    private static DiagnosisStepResult BuildPcContextStep(DomainInfo domain, PcContextInfo context, double durationMs)
    {
        var status = !IsCollectorOk(domain.CollectorStatus) || !IsCollectorOk(context.CollectorStatus)
            ? "Warning"
            : context.HasMultipleDefaultRoutes || context.HasVpn || context.ProxyEnabled == true
                ? "Warning"
                : "OK";

        return new DiagnosisStepResult
        {
            Name = "Context PC",
            Layer = "PC / Policy / Routing",
            Status = status,
            DurationMs = durationMs,
            Evidence =
            [
                $"Admin: {FormatBool(context.IsAdministrator)}.",
                $"Domain joined: {domain.IsDomainJoined}; domain: {domain.DomainName}; logon server: {domain.LogonServer}.",
                $"DNS suffixes: {(domain.DnsSuffixes.Count == 0 ? "None" : string.Join(", ", domain.DnsSuffixes))}.",
                $"VPN adapters: {(context.VpnAdapters.Count == 0 ? "None detected" : string.Join("; ", context.VpnAdapters))}.",
                $"Proxy enabled: {FormatBool(context.ProxyEnabled)}; proxy server: {context.ProxyServer}.",
                $"Default routes: {context.DefaultRoutes.Count}; IPv6 enabled: {FormatBool(context.Ipv6Enabled)}; MTU: {context.Mtu?.ToString() ?? "Unknown"}."
            ],
            Error = FirstCollectorError(domain.CollectorStatus, context.CollectorStatus),
            Recommendation = status == "Warning"
                ? "Review VPN/proxy/default route context if symptoms affect only internal or web traffic."
                : "PC context does not show obvious policy/routing complications."
        };
    }

    private static DiagnosisStepResult BuildVerdictStep(NetworkDiagnosisResult result)
    {
        return new DiagnosisStepResult
        {
            Name = "Verdict",
            Layer = "Diagnosis",
            Status = result.Verdict.Severity,
            DurationMs = 0,
            Evidence = [.. result.Verdict.Evidence],
            Recommendation = result.Verdict.Recommendations.FirstOrDefault() ?? "Review evidence and run a targeted test if needed."
        };
    }

    private static string FormatTestEvidence(string label, TestResult result)
    {
        var latency = result.LatencyMs.HasValue ? $" ({result.LatencyMs.Value:N1} ms)" : string.Empty;
        return $"{label}: {result.Status}{latency}. {result.Details}";
    }

    private static string FormatPacketLossEvidence(string label, PacketLossResult result)
    {
        return result.LossPercent.HasValue
            ? $"{label}: {result.Status}; {result.LossPercent:N1}% loss ({result.Received}/{result.Sent})."
            : $"{label}: {result.Status}. {result.Details}";
    }

    private static string WorstStatus(params string[] statuses) => DiagnosticHelpers.WorstStatus(statuses);
    private static string FirstCriticalDetails(params TestResult[] results) => DiagnosticHelpers.FirstCriticalDetails(results);
    private static string FirstCollectorError(params string[] statuses) => DiagnosticHelpers.FirstCollectorError(statuses);
    private static bool IsCollectorOk(string? collectorStatus) => DiagnosticHelpers.IsCollectorOk(collectorStatus);
    private static string FormatBool(bool? value) => DiagnosticHelpers.FormatBool(value);
    private static string FormatNullable(double? value) => DiagnosticHelpers.FormatNullable(value);

    private static string InferAffectedLayer(string title)
    {
        var value = title.ToLowerInvariant();
        if (value.Contains("dhcp", StringComparison.Ordinal) || value.Contains("ip", StringComparison.Ordinal))
            return "PC / DHCP";
        if (value.Contains("gateway", StringComparison.Ordinal) || value.Contains("vlan", StringComparison.Ordinal))
            return "Gateway / Local Network";
        if (value.Contains("dns", StringComparison.Ordinal))
            return "DNS";
        if (value.Contains("ethernet", StringComparison.Ordinal) || value.Contains("link", StringComparison.Ordinal) || value.Contains("cable", StringComparison.Ordinal))
            return "Cable / Switch Port";
        if (value.Contains("wi-fi", StringComparison.Ordinal) || value.Contains("wifi", StringComparison.Ordinal))
            return "Wi-Fi";
        if (value.Contains("wan", StringComparison.Ordinal) || value.Contains("internet", StringComparison.Ordinal) || value.Contains("https", StringComparison.Ordinal))
            return "Firewall / WAN / Internet";
        if (value.Contains("port", StringComparison.Ordinal) || value.Contains("service", StringComparison.Ordinal))
            return "Server / Firewall";
        return "PC / Network";
    }
}
