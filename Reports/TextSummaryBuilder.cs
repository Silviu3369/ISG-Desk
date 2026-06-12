using System.Text;
using NetScopeDiagnosticCenter.Core;
using NetScopeDiagnosticCenter.Core.Models;

namespace NetScopeDiagnosticCenter.Reports;

public sealed class TextSummaryBuilder
{
    public string Build(NetworkDiagnosisResult result)
    {
        var builder = new StringBuilder();
        builder.AppendLine("ISG Desk Diagnostic Summary");
        builder.AppendLine($"Computer: {result.ComputerName}");
        builder.AppendLine($"User: {result.UserName}");
        builder.AppendLine($"Date: {result.CreatedAt:yyyy-MM-dd HH:mm:ss zzz}");
        builder.AppendLine();
        builder.AppendLine($"Likely issue: {result.Verdict.Title}");
        builder.AppendLine($"Severity: {result.Verdict.Severity}");
        builder.AppendLine($"Confidence: {result.Verdict.Confidence}");
        builder.AppendLine($"Affected layer: {result.Verdict.AffectedLayer}");
        builder.AppendLine($"Health score: {result.HealthScore.Score}/100 ({result.HealthScore.Status})");
        builder.AppendLine();
        AppendList(builder, "Evidence", result.Verdict.Evidence);
        AppendList(builder, "Limitations", result.Verdict.Limitations);
        AppendList(builder, "Recommended action", result.Verdict.Recommendations);
        AppendList(builder, "Health score penalties", result.HealthScore.Penalties);
        AppendDiagnosisSteps(builder, result.Steps);
        AppendQuickDiagnosisDetails(builder, result);
        AppendTraceRoute(builder, result.TraceRoute);

        if (result.LastScenario is not null)
        {
            builder.AppendLine($"Targeted test: {result.LastScenario.WorkflowName}");
            builder.AppendLine($"Targeted test verdict: {result.LastScenario.Title}");
            builder.AppendLine($"Affected layer: {result.LastScenario.AffectedLayer}");
            builder.AppendLine($"Owner suggestion: {result.LastScenario.OwnerSuggestion}");
            builder.AppendLine($"Targeted test confidence: {result.LastScenario.Confidence}");
            builder.AppendLine($"Targeted test input: {result.LastScenario.InputSummary}");
            builder.AppendLine($"Targeted test baseline: {result.LastScenario.BaselineSummary}");
            builder.AppendLine();
            AppendDiagnosisSteps(builder, "Targeted test steps", result.LastScenario.Steps);
            AppendList(builder, "Targeted test evidence", result.LastScenario.Evidence);
            AppendList(builder, "Targeted test next checks", result.LastScenario.NextChecks);
            AppendList(builder, "Targeted test limitations", result.LastScenario.Limitations);
            AppendTargetResults(builder, result.LastScenario.TargetResults);
        }

        AppendLinkQuality(builder, result.LastLinkQuality);
        AppendPrinterDiscovery(builder, result.LastPrinterDiscovery);
        AppendNetworkDevice(builder, result.LastNetworkDevice);
        AppendNetworkDeviceScan(builder, result.LastNetworkDeviceScan);

        return builder.ToString();
    }

    private static void AppendList(StringBuilder builder, string title, IEnumerable<string> items)
    {
        builder.AppendLine(title + ":");
        var hasItems = false;
        foreach (var item in items)
        {
            hasItems = true;
            builder.AppendLine($"- {item}");
        }

        if (!hasItems)
        {
            builder.AppendLine("- None");
        }

        builder.AppendLine();
    }

    private static void AppendTargetResults(StringBuilder builder, IReadOnlyList<TargetProbeResult> results)
    {
        if (results.Count == 0)
        {
            return;
        }

        builder.AppendLine("Targeted test target results:");
        foreach (var result in results)
        {
            builder.AppendLine($"- {result.Target.Host}: DNS={result.DnsStatus}; Ping={result.PingStatus}; Latency={result.LatencyText}; Loss={result.LossText}; Ports={result.PortSummary}; Verdict={result.Verdict}; Owner={result.OwnerSuggestion}; Collector={result.CollectorStatus}");
            if (!string.Equals(result.ShareStatus, "Not tested", StringComparison.OrdinalIgnoreCase))
            {
                builder.AppendLine($"  Share path: {result.ShareStatus}; {result.ShareDetails}");
            }
            if (result.ShareEnumStatus != "Not run")
            {
                builder.AppendLine($"  Published shares: {result.ShareEnumSummary}");
            }
        }
        builder.AppendLine();
    }

    private static void AppendTraceRoute(StringBuilder builder, TraceRouteResult? trace)
    {
        if (trace is null)
        {
            return;
        }

        builder.AppendLine("Path (traceroute):");
        builder.AppendLine($"- Target: {trace.Target}");
        builder.AppendLine($"- Status: {trace.Status}");
        builder.AppendLine($"- Summary: {trace.Summary}");
        foreach (var hop in trace.Hops)
        {
            builder.AppendLine($"- Hop {hop.Hop}: {hop.Address} ({hop.Scope})");
        }
        builder.AppendLine();
    }

    private static void AppendDiagnosisSteps(StringBuilder builder, IReadOnlyList<DiagnosisStepResult> steps)
    {
        AppendDiagnosisSteps(builder, "Quick diagnosis timeline", steps);
    }

    private static void AppendDiagnosisSteps(StringBuilder builder, string title, IReadOnlyList<DiagnosisStepResult> steps)
    {
        if (steps.Count == 0)
        {
            return;
        }

        builder.AppendLine(title + ":");
        foreach (var step in steps)
        {
            builder.AppendLine($"- {step.Name}: {step.Status}; Layer={step.Layer}; Duration={step.DurationMs:N0} ms; Recommendation={step.Recommendation}");
            if (!string.IsNullOrWhiteSpace(step.Error))
            {
                builder.AppendLine($"  Error: {step.Error}");
            }
        }
        builder.AppendLine();
    }

    private static void AppendQuickDiagnosisDetails(StringBuilder builder, NetworkDiagnosisResult result)
    {
        builder.AppendLine("Quick diagnosis technical details:");
        builder.AppendLine($"- Adapter: {result.Adapter.Name}; Type={result.Adapter.ConnectionType}; Link={result.Adapter.LinkSpeed}; Duplex={result.Adapter.SpeedDuplex}");
        builder.AppendLine($"- Link counters: Errors={result.Adapter.Errors}; Discards={result.Adapter.Discards}; Errors/s={FormatNullable(result.Adapter.ErrorsPerSecond)}; Discards/s={FormatNullable(result.Adapter.DiscardsPerSecond)}; Sampling={result.Adapter.SamplingStatus}");
        builder.AppendLine($"- IP: {result.IpConfiguration.IpAddress}; Gateway={result.IpConfiguration.Gateway}; DNS={string.Join(", ", result.IpConfiguration.DnsServers)}");
        builder.AppendLine($"- DHCP: Enabled={FormatBool(result.Dhcp?.DhcpEnabled)}; Server={result.Dhcp?.DhcpServer ?? "Unknown"}; Lease={result.Dhcp?.LeaseObtained ?? "Unknown"} -> {result.Dhcp?.LeaseExpires ?? "Unknown"}");
        builder.AppendLine($"- Domain: Joined={result.Domain?.IsDomainJoined.ToString() ?? "Unknown"}; Domain={result.Domain?.DomainName ?? "Unknown"}; LogonServer={result.Domain?.LogonServer ?? "Unknown"}");
        builder.AppendLine($"- PC context: Admin={FormatBool(result.PcContext.IsAdministrator)}; VPN={result.PcContext.VpnAdapters.Count}; Proxy={FormatBool(result.PcContext.ProxyEnabled)}; DefaultRoutes={result.PcContext.DefaultRoutes.Count}; IPv6={FormatBool(result.PcContext.Ipv6Enabled)}; MTU={result.PcContext.Mtu?.ToString() ?? "Unknown"}");
        foreach (var dns in result.Tests.DnsServerResults)
        {
            builder.AppendLine($"- DNS server test: {dns.Target}; Status={dns.Status}; Latency={FormatNullable(dns.LatencyMs)} ms; {dns.Details}");
        }
        foreach (var ping in result.Tests.InternetPingResults)
        {
            builder.AppendLine($"- Internet ping: {ping.Target}; Status={ping.Status}; Latency={FormatNullable(ping.LatencyMs)} ms; {ping.Details}");
        }
        builder.AppendLine($"- TCP 443: {result.Tests.ExternalTcp443.Status}; {result.Tests.ExternalTcp443.Details}");
        builder.AppendLine($"- HTTPS GET: {result.Tests.HttpsGet.Status}; {result.Tests.HttpsGet.Details}");
        builder.AppendLine();
    }

    private static void AppendPrinterDiscovery(StringBuilder builder, PrinterDiscoveryResult? discovery)
    {
        if (discovery is null)
        {
            return;
        }

        builder.AppendLine("Printer tools:");
        builder.AppendLine($"- Mode: {discovery.Mode}");
        builder.AppendLine($"- Source: {discovery.Source}");
        builder.AppendLine($"- Verdict: {discovery.Verdict}");
        builder.AppendLine($"- Selected target: {discovery.SelectedTarget}");
        builder.AppendLine($"- Selected target source: {discovery.SelectedTargetSource}");
        builder.AppendLine($"- SNMP protocol: {discovery.SnmpProtocol}");
        builder.AppendLine($"- Detected IP: {discovery.LocalIpAddress}");
        builder.AppendLine($"- Adapter: {discovery.LocalAdapterName}");
        builder.AppendLine($"- Gateway: {discovery.Gateway}");
        builder.AppendLine($"- Route metric: {discovery.RouteMetric}; Interface metric: {discovery.InterfaceMetric}");
        builder.AppendLine($"- Detected subnet: {discovery.DetectedSubnet}");
        builder.AppendLine($"- Safe scan range: {discovery.SafeScanRange}");
        builder.AppendLine($"- Estimated hosts: {discovery.EstimatedHosts}");
        builder.AppendLine($"- Max hosts: {discovery.MaxHosts}");
        builder.AppendLine($"- Local spooler: {discovery.LocalSpoolerStatus}");
        builder.AppendLine($"- Print server spooler: {discovery.PrintServerSpoolerStatus}");
        builder.AppendLine($"- Last install: {discovery.LastInstallResult}");
        if (!string.IsNullOrWhiteSpace(discovery.SkippedReason))
        {
            builder.AppendLine($"- Skipped reason: {discovery.SkippedReason}");
        }
        if (!string.IsNullOrWhiteSpace(discovery.SnmpCommunityMasked))
        {
            builder.AppendLine($"- SNMP community: {discovery.SnmpCommunityMasked}");
        }
        foreach (var printer in discovery.LocalPrinters)
        {
            builder.AppendLine($"- Local printer: {printer.Name}; Default={printer.IsDefault}; Driver={printer.DriverName}; Port={printer.PortName}; PortIP={printer.PortHostAddress}; Status={printer.PrinterStatus}");
        }
        foreach (var printer in discovery.Printers)
        {
            builder.AppendLine($"- Print server queue: {printer.Name}; Share={printer.ShareName}; Driver={printer.DriverName}; Port={printer.PortName}; PortIP={printer.PortHostAddress}; Installable={printer.Installable}; InstallStatus={printer.InstallStatus}; Status={printer.PrinterStatus}");
        }
        foreach (var scan in discovery.ScanResults)
        {
            var snmp = scan.SnmpInfo is null
                ? "SNMP=Not checked"
                : scan.SnmpInfo.Success
                    ? $"SNMP={scan.SnmpStatus}; Name={scan.SnmpInfo.SysName}; Printer={scan.SnmpInfo.PrinterName}; Serial={scan.SnmpInfo.PrinterSerialNumber}; Model={scan.SnmpInfo.SysDescr}; Supplies={scan.SnmpInfo.SuppliesSummary}"
                    : $"SNMP=Failed; Error={scan.SnmpInfo.Error}";
            builder.AppendLine($"- Scan: {scan.Address}; Host={scan.HostName}; Classification={scan.Classification}; Confidence={scan.Confidence}; {snmp}; {scan.Details}");
        }
        if (discovery.LastInstall is not null)
        {
            builder.AppendLine($"- Install: {discovery.LastInstall.Verdict}; Connection={discovery.LastInstall.ConnectionName}; Category={discovery.LastInstall.Category}; Error={discovery.LastInstall.Error}");
        }
        AppendList(builder, "Printer discovery limitations", discovery.Limitations);
        builder.AppendLine();
    }

    private static void AppendLinkQuality(StringBuilder builder, LinkQualityResult? linkQuality)
    {
        if (linkQuality is null)
        {
            return;
        }

        builder.AppendLine("Link quality:");
        builder.AppendLine($"- Source: {linkQuality.Source}");
        builder.AppendLine($"- Verdict: {linkQuality.Summary}");
        builder.AppendLine($"- Severity: {linkQuality.Severity}");
        builder.AppendLine($"- Confidence: {linkQuality.Confidence}");
        builder.AppendLine($"- Affected layer: {linkQuality.AffectedLayer}");
        builder.AppendLine($"- Adapter: {linkQuality.AdapterName}; Type={linkQuality.ConnectionType}; Link={linkQuality.LinkSpeed}; Duplex={linkQuality.SpeedDuplex}");
        builder.AppendLine($"- Counters: Errors={linkQuality.Errors}; Discards={linkQuality.Discards}; Errors/s={FormatNullable(linkQuality.ErrorsPerSecond)}; Discards/s={FormatNullable(linkQuality.DiscardsPerSecond)}; Sampling={linkQuality.SamplingStatus}");
        builder.AppendLine($"- Thresholds: {linkQuality.Thresholds.Summary}");

        foreach (var ping in linkQuality.PingResults)
        {
            builder.AppendLine($"- Ping: {ping.Target}; Type={ping.Category}; Status={ping.Status}; Sent={ping.Sent}; Received={ping.Received}; Loss={ping.LossText}; Avg={ping.AverageLatencyText}; Jitter={ping.JitterText}; Threshold={ping.ThresholdProfile}; {ping.Details}");
        }

        foreach (var dns in linkQuality.DnsResults)
        {
            builder.AppendLine($"- DNS: {dns.Name}; Status={dns.Status}; Latency={dns.LatencyText}; Addresses={dns.AddressSummary}; Threshold={dns.ThresholdProfile}; {dns.Details}");
        }

        AppendList(builder, "Link quality evidence", linkQuality.Evidence);
        AppendList(builder, "Link quality recommended next steps", linkQuality.Recommendations);
        AppendList(builder, "Link quality limitations", linkQuality.Limitations);
        builder.AppendLine();
    }

    private static void AppendNetworkDevice(StringBuilder builder, NetworkDeviceResult? device)
    {
        if (device is null)
        {
            return;
        }

        builder.AppendLine("Network devices:");
        builder.AppendLine($"- Target: {device.Target}");
        builder.AppendLine($"- Address: {device.Address}");
        builder.AppendLine($"- Device type: {device.DeviceType}");
        builder.AppendLine($"- Type confidence: {device.ClassificationConfidence}");
        builder.AppendLine($"- Verdict: {device.Verdict}");
        builder.AppendLine($"- Severity: {device.Severity}");
        builder.AppendLine($"- Confirmation: {device.ConfirmationStatus}");
        builder.AppendLine($"- Affected layer: {device.AffectedLayer}");
        builder.AppendLine($"- Owner suggestion: {device.OwnerSuggestion}");
        builder.AppendLine($"- Diagnostic confidence: {device.Confidence}");
        builder.AppendLine($"- SNMP protocol: {device.SnmpProtocol}");
        builder.AppendLine($"- SNMP status: {device.SnmpStatus}");
        builder.AppendLine($"- SNMP credential: {device.SnmpCommunityMasked}");
        builder.AppendLine($"- Ping reachable: {(device.PingReachable ? "Yes" : "No / blocked")}");
        builder.AppendLine($"- DNS status: {device.DnsStatus}");
        builder.AppendLine($"- Reverse DNS: {device.ReverseDnsName}");
        builder.AppendLine($"- Packet loss: {device.LossText}");
        builder.AppendLine($"- Average latency: {device.LatencyText}");
        builder.AppendLine($"- MAC address: {device.MacAddress}");
        builder.AppendLine($"- MAC vendor: {device.MacVendor}");
        builder.AppendLine($"- Neighbor state: {device.NeighborState}");
        builder.AppendLine($"- Open ports: {device.OpenPortSummary}");
        if (device.Identity is not null)
        {
            builder.AppendLine($"- SNMP name: {device.Identity.SysName}");
            builder.AppendLine($"- Description: {device.Identity.SysDescr}");
            builder.AppendLine($"- Location: {device.Identity.SysLocation}");
            builder.AppendLine($"- Uptime: {device.Identity.SysUpTime}");
        }

        foreach (var port in device.Ports)
        {
            builder.AppendLine($"- TCP {port.Port}: {port.StatusText}; latency={port.LatencyText}; {port.Details}");
        }

        foreach (var item in device.PortsNeedingAttention)
        {
            builder.AppendLine($"- Attention: Interface {item.Index} {item.Name}; Oper={item.OperStatus}; Speed={item.SpeedText}; Errors/s={item.ErrorsPerSecond:N2}; Discards/s={item.DiscardsPerSecond:N2}; Traffic/s={item.TrafficRateText}; Reason={item.AttentionReason}; Next check={item.RecommendedNextCheck}");
        }

        foreach (var item in device.Interfaces)
        {
            builder.AppendLine($"- Interface {item.Index} {item.Name}: Oper={item.OperStatus}; Speed={item.SpeedText}; Errors={item.Errors}; Discards={item.Discards}; Traffic={item.TrafficText}; Traffic/s={item.TrafficRateText}; Errors/s={item.ErrorsPerSecond:N2}; Discards/s={item.DiscardsPerSecond:N2}; Verdict={item.Verdict}; Next check={item.RecommendedNextCheck}");
        }

        AppendList(builder, "Network device confirmed findings", device.ConfirmedFindings);
        AppendList(builder, "Network device probable findings", device.ProbableFindings);
        AppendList(builder, "Network device unknowns / limits", device.UnknownFindings);
        AppendList(builder, "Network device evidence", device.Evidence);
        AppendList(builder, "Network device recommended next checks", device.Recommendations);
        AppendList(builder, "Network device limitations", device.Limitations);
        builder.AppendLine();
    }

    private static void AppendNetworkDeviceScan(StringBuilder builder, NetworkDeviceScanResult? scan)
    {
        if (scan is null)
        {
            return;
        }

        builder.AppendLine("Network device LAN scan:");
        builder.AppendLine($"- Source: {scan.Source}");
        builder.AppendLine($"- Verdict: {scan.Verdict}");
        builder.AppendLine($"- Estimated hosts: {scan.EstimatedHosts}");
        builder.AppendLine($"- Max hosts: {scan.MaxHosts}");
        builder.AppendLine($"- Scanned hosts: {scan.ScannedHosts}");
        builder.AppendLine($"- Found devices: {scan.FoundDevices}");
        builder.AppendLine($"- SNMP timeouts: {scan.TimedOutHosts}");
        builder.AppendLine($"- Other failures: {scan.FailedHosts}");
        builder.AppendLine($"- Progress: {scan.ProgressText}");
        builder.AppendLine($"- SNMP protocol: {scan.SnmpProtocol}");
        builder.AppendLine($"- Local IP: {scan.LocalIpAddress}");
        builder.AppendLine($"- Adapter: {scan.AdapterName}");
        builder.AppendLine($"- Gateway: {scan.Gateway}");
        builder.AppendLine($"- Detected subnet: {scan.DetectedSubnet}");
        builder.AppendLine($"- Safe scan range: {scan.SafeScanRange}");
        if (!string.IsNullOrWhiteSpace(scan.SkippedReason))
        {
            builder.AppendLine($"- Skipped reason: {scan.SkippedReason}");
        }
        if (!string.IsNullOrWhiteSpace(scan.SnmpCommunityMasked))
        {
            builder.AppendLine($"- SNMP credential: {scan.SnmpCommunityMasked}");
        }

        foreach (var device in scan.Devices)
        {
            // Discovery-era line: name precedence (label > sysName > NetBIOS > rDNS > vendor),
            // MAC + vendor, technician label and open-port signature all carry signal now.
            builder.AppendLine($"- Device: {device.Address}; Name={device.DisplayName}; Type={device.DeviceType} ({device.ClassificationConfidence}); Vendor={device.MacVendorDisplay}; MAC={device.MacAddressDisplay}; Label={device.FriendlyLabelDisplay}; SNMP location={device.LocationDisplay}; Ping={(device.PingReachable ? "Yes" : "No / blocked")}; Open ports={device.OpenPortSummary}; Confirmation={device.ConfirmationStatus}");
        }

        AppendList(builder, "Network device scan evidence", scan.Evidence);
        AppendList(builder, "Network device scan limitations", scan.Limitations);
        builder.AppendLine();
    }

    private static string FormatBool(bool? value) => DiagnosticHelpers.FormatBool(value);
    private static string FormatNullable(double? value) => DiagnosticHelpers.FormatNullable(value);
}
