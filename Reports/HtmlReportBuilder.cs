using System.Net;
using System.Text;
using NetScopeDiagnosticCenter.Core;
using NetScopeDiagnosticCenter.Core.Models;

namespace NetScopeDiagnosticCenter.Reports;

public sealed class HtmlReportBuilder
{
    public string Build(NetworkDiagnosisResult result)
    {
        var html = new StringBuilder();
        html.AppendLine("<!doctype html>");
        html.AppendLine("<html lang=\"en\"><head><meta charset=\"utf-8\"><title>ISG Desk Diagnostic Report</title>");
        html.AppendLine("<style>");
        html.AppendLine("""
body{font-family:Segoe UI,Arial,sans-serif;margin:32px;color:#17212b;background:#f5f7fa}
.page{max-width:980px;margin:0 auto;background:#fff;border:1px solid #d8dee6;padding:28px}
h1{margin:0 0 8px;font-size:26px}.meta{color:#536171;margin-bottom:24px}
.verdict{border-left:6px solid #2563eb;background:#eef4ff;padding:16px;margin:18px 0}
.Critical{border-left-color:#dc2626;background:#fff1f2}.Warning{border-left-color:#d97706;background:#fffbeb}.OK{border-left-color:#16a34a;background:#f0fdf4}
.Healthy{border-left-color:#16a34a;background:#f0fdf4}.Good{border-left-color:#2563eb;background:#eef4ff}
.grid{display:grid;grid-template-columns:1fr 1fr;gap:14px}.card{border:1px solid #d8dee6;padding:14px;background:#fbfcfe}
dt{font-weight:600;color:#334155}dd{margin:0 0 8px}li{margin-bottom:6px}code{background:#eef2f7;padding:2px 5px}
""");
        html.AppendLine("</style></head><body><main class=\"page\">");
        html.AppendLine("<h1>ISG Desk Diagnostic Report</h1>");
        html.AppendLine($"<div class=\"meta\">Computer: {E(result.ComputerName)} | User: {E(result.UserName)} | Date: {E(result.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss zzz"))}</div>");

        html.AppendLine($"<section class=\"verdict {E(result.Verdict.Severity)}\">");
        html.AppendLine($"<h2>{E(result.Verdict.Title)}</h2>");
        html.AppendLine($"<p><strong>Severity:</strong> {E(result.Verdict.Severity)} &nbsp; <strong>Confidence:</strong> {E(result.Verdict.Confidence)} &nbsp; <strong>Affected layer:</strong> {E(result.Verdict.AffectedLayer)} &nbsp; <strong>Health:</strong> {result.HealthScore.Score}/100 ({E(result.HealthScore.Status)})</p>");
        html.AppendLine("</section>");

        html.AppendLine("<section class=\"grid\">");
        html.AppendLine("<div class=\"card\"><h3>Connection</h3><dl>");
        html.AppendLine(Dt("Type", result.Adapter.ConnectionType));
        html.AppendLine(Dt("Adapter", result.Adapter.Name));
        html.AppendLine(Dt("Link speed", result.Adapter.LinkSpeed));
        html.AppendLine(Dt("MAC", result.Adapter.MacAddress));
        html.AppendLine("</dl></div>");
        html.AppendLine("<div class=\"card\"><h3>Network</h3><dl>");
        html.AppendLine(Dt("IP", result.IpConfiguration.IpAddress));
        html.AppendLine(Dt("Gateway", result.IpConfiguration.Gateway));
        html.AppendLine(Dt("DNS", string.Join(", ", result.IpConfiguration.DnsServers)));
        html.AppendLine("</dl></div>");
        html.AppendLine("</section>");

        AppendList(html, "Evidence", result.Verdict.Evidence);
        AppendList(html, "Limitations", result.Verdict.Limitations);
        AppendList(html, "Recommended Next Steps", result.Verdict.Recommendations);
        AppendList(html, "Health Score Penalties", result.HealthScore.Penalties);
        AppendDiagnosisSteps(html, result.Steps);
        AppendTraceRoute(html, result.TraceRoute);

        if (result.LastScenario is not null)
        {
            html.AppendLine($"<section class=\"verdict {E(result.LastScenario.Severity)}\">");
            html.AppendLine($"<h2>Targeted Test: {E(result.LastScenario.WorkflowName)}</h2>");
            html.AppendLine($"<p><strong>Verdict:</strong> {E(result.LastScenario.Title)}</p>");
            html.AppendLine($"<p><strong>Affected layer:</strong> {E(result.LastScenario.AffectedLayer)} &nbsp; <strong>Owner:</strong> {E(result.LastScenario.OwnerSuggestion)} &nbsp; <strong>Confidence:</strong> {E(result.LastScenario.Confidence)}</p>");
            html.AppendLine($"<p><strong>Input:</strong> {E(result.LastScenario.InputSummary)}<br/><strong>Baseline:</strong> {E(result.LastScenario.BaselineSummary)}</p>");
            html.AppendLine("</section>");
            AppendTestSteps(html, result.LastScenario.Steps);
            AppendList(html, "Targeted Test Evidence", result.LastScenario.Evidence);
            AppendList(html, "Targeted Test Next Checks", result.LastScenario.NextChecks);
            AppendList(html, "Targeted Test Limitations", result.LastScenario.Limitations);
            AppendTargetResults(html, result.LastScenario.TargetResults);
        }

        AppendLinkQuality(html, result.LastLinkQuality);
        AppendPrinterDiscovery(html, result.LastPrinterDiscovery);
        AppendNetworkDevice(html, result.LastNetworkDevice);
        AppendNetworkDeviceScan(html, result.LastNetworkDeviceScan);

        html.AppendLine("<h2>Technical Details</h2><section class=\"grid\">");
        html.AppendLine("<div class=\"card\"><h3>Tests</h3><dl>");
        html.AppendLine(Dt("Gateway", FormatTest(result.Tests.Gateway)));
        html.AppendLine(Dt("Gateway packet loss", FormatPacketLoss(result.Tests.GatewayPacketLoss)));
        html.AppendLine(Dt("DNS server", FormatTest(result.Tests.DnsServer)));
        foreach (var dnsServer in result.Tests.DnsServerResults)
        {
            html.AppendLine(Dt($"DNS {dnsServer.Target}", FormatTest(dnsServer)));
        }
        html.AppendLine(Dt("DNS resolution", FormatTest(result.Tests.DnsResolution)));
        html.AppendLine(Dt("Internet IP", FormatTest(result.Tests.InternetPing)));
        foreach (var ping in result.Tests.InternetPingResults)
        {
            html.AppendLine(Dt($"Ping {ping.Target}", FormatTest(ping)));
        }
        html.AppendLine(Dt("External TCP 443", FormatTest(result.Tests.ExternalTcp443)));
        html.AppendLine(Dt("HTTPS GET", FormatTest(result.Tests.HttpsGet)));
        html.AppendLine(Dt("Internet packet loss", FormatPacketLoss(result.Tests.InternetPacketLoss)));
        html.AppendLine("</dl></div>");
        html.AppendLine("<div class=\"card\"><h3>Adapter Counters</h3><dl>");
        html.AppendLine(Dt("Speed/Duplex", result.Adapter.SpeedDuplex));
        html.AppendLine(Dt("Errors", result.Adapter.Errors.ToString()));
        html.AppendLine(Dt("Discards", result.Adapter.Discards.ToString()));
        html.AppendLine(Dt("Sampling", result.Adapter.SamplingStatus));
        html.AppendLine(Dt("Errors/s", result.Adapter.ErrorsPerSecond?.ToString("N2") ?? "Unknown"));
        html.AppendLine(Dt("Discards/s", result.Adapter.DiscardsPerSecond?.ToString("N2") ?? "Unknown"));
        html.AppendLine(Dt("Bytes received", result.Adapter.BytesReceived.ToString("N0")));
        html.AppendLine(Dt("Bytes sent", result.Adapter.BytesSent.ToString("N0")));
        html.AppendLine("</dl></div></section>");

        html.AppendLine("<section class=\"card\"><h3>PC Context</h3><dl>");
        html.AppendLine(Dt("Administrator", FormatBool(result.PcContext.IsAdministrator)));
        html.AppendLine(Dt("Domain joined", result.Domain?.IsDomainJoined.ToString() ?? "Unknown"));
        html.AppendLine(Dt("Domain", result.Domain?.DomainName ?? "Unknown"));
        html.AppendLine(Dt("Logon server", result.Domain?.LogonServer ?? "Unknown"));
        html.AppendLine(Dt("DNS suffixes", result.Domain is null ? "Unknown" : string.Join(", ", result.Domain.DnsSuffixes)));
        html.AppendLine(Dt("DHCP enabled", FormatBool(result.Dhcp?.DhcpEnabled)));
        html.AppendLine(Dt("DHCP server", result.Dhcp?.DhcpServer ?? "Unknown"));
        html.AppendLine(Dt("VPN adapters", result.PcContext.VpnAdapters.Count == 0 ? "None detected" : string.Join("; ", result.PcContext.VpnAdapters)));
        html.AppendLine(Dt("Proxy enabled", FormatBool(result.PcContext.ProxyEnabled)));
        html.AppendLine(Dt("Proxy server", result.PcContext.ProxyServer));
        html.AppendLine(Dt("Default routes", result.PcContext.DefaultRoutes.Count == 0 ? "Unknown" : string.Join("; ", result.PcContext.DefaultRoutes)));
        html.AppendLine(Dt("IPv6 enabled", FormatBool(result.PcContext.Ipv6Enabled)));
        html.AppendLine(Dt("MTU", result.PcContext.Mtu?.ToString() ?? "Unknown"));
        html.AppendLine("</dl></section>");

        if (result.Wifi is not null)
        {
            html.AppendLine("<section class=\"card\"><h3>Wi-Fi</h3><dl>");
            html.AppendLine(Dt("SSID", result.Wifi.Ssid));
            html.AppendLine(Dt("BSSID", result.Wifi.Bssid));
            html.AppendLine(Dt("Signal", result.Wifi.SignalPercent.HasValue ? $"{result.Wifi.SignalPercent}%" : "Unknown"));
            html.AppendLine(Dt("Radio type", result.Wifi.RadioType));
            html.AppendLine(Dt("Channel", result.Wifi.Channel?.ToString() ?? "Unknown"));
            html.AppendLine(Dt("Rates", $"Rx {result.Wifi.ReceiveRateMbps} Mbps / Tx {result.Wifi.TransmitRateMbps} Mbps"));
            html.AppendLine("</dl></section>");
        }

        if (result.LastPortTest is not null)
        {
            html.AppendLine("<section class=\"card\"><h3>Last Port Test</h3><dl>");
            html.AppendLine(Dt("Target", result.LastPortTest.Target));
            html.AppendLine(Dt("Port", result.LastPortTest.Port.ToString()));
            html.AppendLine(Dt("Verdict", result.LastPortTest.Verdict));
            html.AppendLine("</dl></section>");
        }

        html.AppendLine("</main></body></html>");
        return html.ToString();
    }

    private static void AppendList(StringBuilder html, string title, IEnumerable<string> items)
    {
        html.AppendLine($"<h2>{E(title)}</h2><ul>");
        foreach (var item in items)
        {
            html.AppendLine($"<li>{E(item)}</li>");
        }
        html.AppendLine("</ul>");
    }

    private static void AppendDiagnosisSteps(StringBuilder html, IReadOnlyList<DiagnosisStepResult> steps)
    {
        if (steps.Count == 0)
        {
            return;
        }

        html.AppendLine("<h2>Quick Diagnosis Timeline</h2><table style=\"width:100%;border-collapse:collapse\">");
        html.AppendLine("<tr><th align=\"left\">Step</th><th align=\"left\">Layer</th><th align=\"left\">Status</th><th align=\"left\">Duration</th><th align=\"left\">Recommendation</th></tr>");
        foreach (var step in steps)
        {
            html.AppendLine("<tr>");
            html.AppendLine($"<td>{E(step.Name)}</td>");
            html.AppendLine($"<td>{E(step.Layer)}</td>");
            html.AppendLine($"<td>{E(step.Status)}</td>");
            html.AppendLine($"<td>{step.DurationMs:N0} ms</td>");
            html.AppendLine($"<td>{E(step.Recommendation)}</td>");
            html.AppendLine("</tr>");
        }
        html.AppendLine("</table>");
    }

    private static void AppendTestSteps(StringBuilder html, IReadOnlyList<DiagnosisStepResult> steps)
    {
        if (steps.Count == 0)
        {
            return;
        }

        html.AppendLine("<h2>Test Steps</h2><table style=\"width:100%;border-collapse:collapse\">");
        html.AppendLine("<tr><th align=\"left\">Step</th><th align=\"left\">Layer</th><th align=\"left\">Status</th><th align=\"left\">Duration</th><th align=\"left\">Recommendation</th><th align=\"left\">Error</th></tr>");
        foreach (var step in steps)
        {
            html.AppendLine("<tr>");
            html.AppendLine($"<td>{E(step.Name)}</td>");
            html.AppendLine($"<td>{E(step.Layer)}</td>");
            html.AppendLine($"<td>{E(step.Status)}</td>");
            html.AppendLine($"<td>{step.DurationMs:N0} ms</td>");
            html.AppendLine($"<td>{E(step.Recommendation)}</td>");
            html.AppendLine($"<td>{E(step.Error)}</td>");
            html.AppendLine("</tr>");
        }
        html.AppendLine("</table>");
    }

    private static string Dt(string term, string value) => $"<dt>{E(term)}</dt><dd>{E(value)}</dd>";

    private static string FormatTest(TestResult result)
    {
        var latency = result.LatencyMs.HasValue ? $" - {result.LatencyMs.Value:N1} ms" : string.Empty;
        return $"{result.Status}{latency}: {result.Details}";
    }

    private static string FormatPacketLoss(PacketLossResult result)
    {
        return result.LossPercent.HasValue
            ? $"{result.Status}: {result.LossPercent.Value:N1}% loss ({result.Received}/{result.Sent} replies)"
            : $"{result.Status}: {result.Details}";
    }

    private static string E(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);

    private static string FormatBool(bool? value) => DiagnosticHelpers.FormatBool(value);

    private static void AppendTargetResults(StringBuilder html, IReadOnlyList<TargetProbeResult> results)
    {
        if (results.Count == 0)
        {
            return;
        }

        html.AppendLine("<h2>Targeted Test Target Results</h2><table style=\"width:100%;border-collapse:collapse\">");
        html.AppendLine("<tr><th align=\"left\">Target</th><th align=\"left\">DNS</th><th align=\"left\">Ping</th><th align=\"left\">Latency</th><th align=\"left\">Loss</th><th align=\"left\">Ports</th><th align=\"left\">Share</th><th align=\"left\">Published shares</th><th align=\"left\">Verdict</th><th align=\"left\">Owner</th><th align=\"left\">Collector</th></tr>");
        foreach (var result in results)
        {
            html.AppendLine("<tr>");
            html.AppendLine($"<td>{E(result.Target.Host)}</td>");
            html.AppendLine($"<td>{E(result.DnsStatus)}</td>");
            html.AppendLine($"<td>{E(result.PingStatus)}</td>");
            html.AppendLine($"<td>{E(result.LatencyText)}</td>");
            html.AppendLine($"<td>{E(result.LossText)}</td>");
            html.AppendLine($"<td>{E(result.PortSummary)}</td>");
            html.AppendLine($"<td>{E(result.ShareStatus)}</td>");
            html.AppendLine($"<td>{E(result.VisibleSharesText)}</td>");
            html.AppendLine($"<td>{E(result.Verdict)}</td>");
            html.AppendLine($"<td>{E(result.OwnerSuggestion)}</td>");
            html.AppendLine($"<td>{E(result.CollectorStatus)}</td>");
            html.AppendLine("</tr>");
        }

        html.AppendLine("</table>");
    }

    private static void AppendTraceRoute(StringBuilder html, TraceRouteResult? trace)
    {
        if (trace is null)
        {
            return;
        }

        html.AppendLine($"<section class=\"verdict {E(trace.Status)}\">");
        html.AppendLine("<h2>Path (traceroute)</h2>");
        html.AppendLine($"<p><strong>{E(trace.Summary)}</strong></p>");
        html.AppendLine($"<p><strong>Target:</strong> {E(trace.Target)} &nbsp; <strong>Status:</strong> {E(trace.Status)} &nbsp; <strong>Reached:</strong> {(trace.ReachedTarget ? "Yes" : "No")}</p>");
        html.AppendLine("</section>");

        if (trace.Hops.Count == 0)
        {
            return;
        }

        html.AppendLine("<table style=\"width:100%;border-collapse:collapse\">");
        html.AppendLine("<tr><th align=\"left\">Hop</th><th align=\"left\">Address</th><th align=\"left\">Scope</th></tr>");
        foreach (var hop in trace.Hops)
        {
            html.AppendLine($"<tr><td>{hop.Hop}</td><td>{E(hop.Address)}</td><td>{E(hop.Scope)}</td></tr>");
        }
        html.AppendLine("</table>");
    }

    private static void AppendPrinterDiscovery(StringBuilder html, PrinterDiscoveryResult? discovery)
    {
        if (discovery is null)
        {
            return;
        }

        html.AppendLine("<h2>Printer Tools</h2>");
        html.AppendLine($"<p><strong>{E(discovery.Verdict)}</strong></p>");
        html.AppendLine("<section class=\"card\"><dl>");
        html.AppendLine(Dt("Mode", discovery.Mode));
        html.AppendLine(Dt("Source", discovery.Source));
        html.AppendLine(Dt("Selected target", discovery.SelectedTarget));
        html.AppendLine(Dt("Selected target source", discovery.SelectedTargetSource));
        html.AppendLine(Dt("SNMP protocol", discovery.SnmpProtocol));
        html.AppendLine(Dt("Detected IP", discovery.LocalIpAddress));
        html.AppendLine(Dt("Adapter", discovery.LocalAdapterName));
        html.AppendLine(Dt("Gateway", discovery.Gateway));
        html.AppendLine(Dt("Route metric", discovery.RouteMetric.ToString()));
        html.AppendLine(Dt("Interface metric", discovery.InterfaceMetric.ToString()));
        html.AppendLine(Dt("Detected subnet", discovery.DetectedSubnet));
        html.AppendLine(Dt("Detected subnet hosts", discovery.DetectedSubnetHosts.ToString()));
        html.AppendLine(Dt("Safe scan range", discovery.SafeScanRange));
        html.AppendLine(Dt("Estimated hosts", discovery.EstimatedHosts.ToString()));
        html.AppendLine(Dt("Max hosts", discovery.MaxHosts.ToString()));
        html.AppendLine(Dt("Local spooler", discovery.LocalSpoolerStatus));
        html.AppendLine(Dt("Print server spooler", discovery.PrintServerSpoolerStatus));
        html.AppendLine(Dt("Last install", discovery.LastInstallResult));
        html.AppendLine(Dt("Skipped reason", discovery.SkippedReason));
        html.AppendLine(Dt("SNMP community", discovery.SnmpCommunityMasked));
        html.AppendLine("</dl></section>");
        AppendList(html, "Printer Discovery Evidence", discovery.Evidence);
        AppendList(html, "Printer Discovery Limitations", discovery.Limitations);

        if (discovery.LocalPrinters.Count > 0)
        {
            html.AppendLine("<h3>Installed Printers On This PC</h3><table style=\"width:100%;border-collapse:collapse\">");
            html.AppendLine("<tr><th align=\"left\">Name</th><th align=\"left\">Default</th><th align=\"left\">Driver</th><th align=\"left\">Port</th><th align=\"left\">Port IP</th><th align=\"left\">Status</th></tr>");
            foreach (var printer in discovery.LocalPrinters)
            {
                html.AppendLine($"<tr><td>{E(printer.Name)}</td><td>{E(printer.IsDefault ? "Yes" : "No")}</td><td>{E(printer.DriverName)}</td><td>{E(printer.PortName)}</td><td>{E(printer.PortHostAddress)}</td><td>{E(printer.PrinterStatus)}</td></tr>");
            }
            html.AppendLine("</table>");
        }

        if (discovery.Printers.Count > 0)
        {
            html.AppendLine("<h3>Print Server Printers</h3><table style=\"width:100%;border-collapse:collapse\">");
            html.AppendLine("<tr><th align=\"left\">Name</th><th align=\"left\">Share</th><th align=\"left\">Driver</th><th align=\"left\">Port</th><th align=\"left\">Port IP</th><th align=\"left\">Installable</th><th align=\"left\">Install status</th><th align=\"left\">Status</th></tr>");
            foreach (var printer in discovery.Printers)
            {
                html.AppendLine($"<tr><td>{E(printer.Name)}</td><td>{E(printer.ShareName)}</td><td>{E(printer.DriverName)}</td><td>{E(printer.PortName)}</td><td>{E(printer.PortHostAddress)}</td><td>{E(printer.Installable ? "Yes" : "No")}</td><td>{E(printer.InstallStatus)}</td><td>{E(printer.PrinterStatus)}</td></tr>");
            }
            html.AppendLine("</table>");
        }

        if (discovery.ScanResults.Count > 0)
        {
            html.AppendLine("<h3>Subnet Scan Results</h3><table style=\"width:100%;border-collapse:collapse\">");
            html.AppendLine("<tr><th align=\"left\">Address</th><th align=\"left\">Host</th><th align=\"left\">Classification</th><th align=\"left\">Confidence</th><th align=\"left\">SNMP Status</th><th align=\"left\">SNMP Name</th><th align=\"left\">Printer Name</th><th align=\"left\">Serial</th><th align=\"left\">Model / Description</th><th align=\"left\">Supplies</th><th align=\"left\">Details</th></tr>");
            foreach (var scan in discovery.ScanResults)
            {
                html.AppendLine($"<tr><td>{E(scan.Address)}</td><td>{E(scan.HostName)}</td><td>{E(scan.Classification)}</td><td>{E(scan.Confidence)}</td><td>{E(scan.SnmpStatus)}</td><td>{E(scan.SnmpInfo?.SysName)}</td><td>{E(scan.SnmpInfo?.PrinterName)}</td><td>{E(scan.SnmpInfo?.PrinterSerialNumber)}</td><td>{E(scan.SnmpInfo?.SysDescr)}</td><td>{E(scan.SnmpInfo?.SuppliesSummary)}</td><td>{E(scan.Details)}</td></tr>");
            }
            html.AppendLine("</table>");
        }

        if (discovery.LastInstall is not null)
        {
            html.AppendLine("<h3>Last Printer Install</h3><section class=\"card\"><dl>");
            html.AppendLine(Dt("Verdict", discovery.LastInstall.Verdict));
            html.AppendLine(Dt("Connection", discovery.LastInstall.ConnectionName));
            html.AppendLine(Dt("Printer", discovery.LastInstall.PrinterName));
            html.AppendLine(Dt("Category", discovery.LastInstall.Category));
            html.AppendLine(Dt("Error", discovery.LastInstall.Error));
            html.AppendLine("</dl></section>");
        }
    }

    private static void AppendLinkQuality(StringBuilder html, LinkQualityResult? linkQuality)
    {
        if (linkQuality is null)
        {
            return;
        }

        html.AppendLine($"<section class=\"verdict {E(linkQuality.Severity)}\">");
        html.AppendLine("<h2>Link Quality</h2>");
        html.AppendLine($"<p><strong>{E(linkQuality.Summary)}</strong></p>");
        html.AppendLine($"<p><strong>Severity:</strong> {E(linkQuality.Severity)} &nbsp; <strong>Confidence:</strong> {E(linkQuality.Confidence)} &nbsp; <strong>Affected layer:</strong> {E(linkQuality.AffectedLayer)}</p>");
        html.AppendLine($"<p><strong>Source:</strong> {E(linkQuality.Source)} &nbsp; <strong>Created:</strong> {E(linkQuality.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss zzz"))}</p>");
        html.AppendLine("</section>");

        html.AppendLine("<section class=\"grid\">");
        html.AppendLine("<div class=\"card\"><h3>Adapter / Counters</h3><dl>");
        html.AppendLine(Dt("Adapter", linkQuality.AdapterName));
        html.AppendLine(Dt("Connection type", linkQuality.ConnectionType));
        html.AppendLine(Dt("Link speed", linkQuality.LinkSpeed));
        html.AppendLine(Dt("Expected Ethernet minimum", linkQuality.ExpectedMinimumEthernetMbps > 0 ? $"{linkQuality.ExpectedMinimumEthernetMbps} Mbps" : "Unknown"));
        html.AppendLine(Dt("Speed / Duplex", linkQuality.SpeedDuplex));
        html.AppendLine(Dt("Errors", linkQuality.Errors.ToString("N0")));
        html.AppendLine(Dt("Discards", linkQuality.Discards.ToString("N0")));
        html.AppendLine(Dt("Sampling", linkQuality.SamplingStatus));
        html.AppendLine(Dt("Errors/s", linkQuality.ErrorsPerSecond?.ToString("N2") ?? "Unknown"));
        html.AppendLine(Dt("Discards/s", linkQuality.DiscardsPerSecond?.ToString("N2") ?? "Unknown"));
        html.AppendLine("</dl></div>");
        html.AppendLine("<div class=\"card\"><h3>Interpretation Thresholds</h3><dl>");
        html.AppendLine(Dt("Gateway", linkQuality.Thresholds.GatewaySummary));
        html.AppendLine(Dt("Internet", linkQuality.Thresholds.InternetSummary));
        html.AppendLine(Dt("Target", linkQuality.Thresholds.TargetSummary));
        html.AppendLine(Dt("DNS", linkQuality.Thresholds.DnsSummary));
        html.AppendLine(Dt("Stability", linkQuality.Thresholds.StabilitySummary));
        html.AppendLine("</dl></div>");
        html.AppendLine("</section>");

        if (linkQuality.PingResults.Count > 0)
        {
            html.AppendLine("<h3>Link Quality Ping Results</h3><table style=\"width:100%;border-collapse:collapse\">");
            html.AppendLine("<tr><th align=\"left\">Target</th><th align=\"left\">Type</th><th align=\"left\">Status</th><th align=\"left\">Sent</th><th align=\"left\">Received</th><th align=\"left\">Loss</th><th align=\"left\">Avg</th><th align=\"left\">Jitter</th><th align=\"left\">Threshold</th><th align=\"left\">Details</th></tr>");
            foreach (var ping in linkQuality.PingResults)
            {
                html.AppendLine($"<tr><td>{E(ping.Target)}</td><td>{E(ping.Category)}</td><td>{E(ping.Status)}</td><td>{ping.Sent}</td><td>{ping.Received}</td><td>{E(ping.LossText)}</td><td>{E(ping.AverageLatencyText)}</td><td>{E(ping.JitterText)}</td><td>{E(ping.ThresholdProfile)}</td><td>{E(ping.Details)}</td></tr>");
            }
            html.AppendLine("</table>");
        }

        if (linkQuality.DnsResults.Count > 0)
        {
            html.AppendLine("<h3>Link Quality DNS Results</h3><table style=\"width:100%;border-collapse:collapse\">");
            html.AppendLine("<tr><th align=\"left\">Name</th><th align=\"left\">Status</th><th align=\"left\">Latency</th><th align=\"left\">Addresses</th><th align=\"left\">Threshold</th><th align=\"left\">Details</th></tr>");
            foreach (var dns in linkQuality.DnsResults)
            {
                html.AppendLine($"<tr><td>{E(dns.Name)}</td><td>{E(dns.Status)}</td><td>{E(dns.LatencyText)}</td><td>{E(dns.AddressSummary)}</td><td>{E(dns.ThresholdProfile)}</td><td>{E(dns.Details)}</td></tr>");
            }
            html.AppendLine("</table>");
        }

        AppendList(html, "Link Quality Evidence", linkQuality.Evidence);
        AppendList(html, "Link Quality Recommended Next Steps", linkQuality.Recommendations);
        AppendList(html, "Link Quality Limitations", linkQuality.Limitations);
    }

    private static void AppendNetworkDevice(StringBuilder html, NetworkDeviceResult? device)
    {
        if (device is null)
        {
            return;
        }

        html.AppendLine("<h2>Network Devices</h2>");
        html.AppendLine($"<p><strong>{E(device.Verdict)}</strong></p>");
        html.AppendLine("<section class=\"card\"><dl>");
        html.AppendLine(Dt("Target", device.Target));
        html.AppendLine(Dt("Address", device.Address));
        html.AppendLine(Dt("Device type", device.DeviceType));
        html.AppendLine(Dt("Type confidence", device.ClassificationConfidence));
        html.AppendLine(Dt("Confirmation", device.ConfirmationStatus));
        html.AppendLine(Dt("Affected layer", device.AffectedLayer));
        html.AppendLine(Dt("Owner suggestion", device.OwnerSuggestion));
        html.AppendLine(Dt("Diagnostic confidence", device.Confidence));
        html.AppendLine(Dt("Severity", device.Severity));
        html.AppendLine(Dt("SNMP protocol", device.SnmpProtocol));
        html.AppendLine(Dt("SNMP status", device.SnmpStatus));
        html.AppendLine(Dt("SNMP credential", device.SnmpCommunityMasked));
        html.AppendLine(Dt("Ping reachable", device.PingReachable ? "Yes" : "No / blocked"));
        html.AppendLine(Dt("DNS status", device.DnsStatus));
        html.AppendLine(Dt("Reverse DNS", device.ReverseDnsName));
        html.AppendLine(Dt("Packet loss", device.LossText));
        html.AppendLine(Dt("Average latency", device.LatencyText));
        html.AppendLine(Dt("MAC address", device.MacAddress));
        html.AppendLine(Dt("MAC vendor", device.MacVendor));
        html.AppendLine(Dt("Neighbor state", device.NeighborState));
        html.AppendLine(Dt("Open ports", device.OpenPortSummary));
        if (device.Identity is not null)
        {
            html.AppendLine(Dt("SNMP name", device.Identity.SysName));
            html.AppendLine(Dt("Description", device.Identity.SysDescr));
            html.AppendLine(Dt("Location", device.Identity.SysLocation));
            html.AppendLine(Dt("Uptime", device.Identity.SysUpTime));
        }
        html.AppendLine("</dl></section>");
        AppendList(html, "Network Device Confirmed Findings", device.ConfirmedFindings);
        AppendList(html, "Network Device Probable Findings", device.ProbableFindings);
        AppendList(html, "Network Device Unknowns / Limits", device.UnknownFindings);
        AppendList(html, "Network Device Evidence", device.Evidence);
        AppendList(html, "Network Device Recommended Next Checks", device.Recommendations);
        AppendList(html, "Network Device Limitations", device.Limitations);

        if (device.Ports.Count > 0)
        {
            html.AppendLine("<h3>Common TCP Management / Service Ports</h3><table style=\"width:100%;border-collapse:collapse\">");
            html.AppendLine("<tr><th align=\"left\">Port</th><th align=\"left\">Status</th><th align=\"left\">Latency</th><th align=\"left\">Details</th></tr>");
            foreach (var port in device.Ports)
            {
                html.AppendLine($"<tr><td>{port.Port}</td><td>{E(port.StatusText)}</td><td>{E(port.LatencyText)}</td><td>{E(port.Details)}</td></tr>");
            }
            html.AppendLine("</table>");
        }

        if (device.Interfaces.Count == 0)
        {
            return;
        }

        if (device.PortsNeedingAttention.Count > 0)
        {
            html.AppendLine("<h3>Ports Needing Attention</h3><table style=\"width:100%;border-collapse:collapse\">");
            html.AppendLine("<tr><th align=\"left\">Index</th><th align=\"left\">Name</th><th align=\"left\">Description</th><th align=\"left\">Oper</th><th align=\"left\">Speed</th><th align=\"left\">Errors/s</th><th align=\"left\">Discards/s</th><th align=\"left\">Traffic/s</th><th align=\"left\">Reason</th><th align=\"left\">Next check</th></tr>");
            foreach (var item in device.PortsNeedingAttention)
            {
                html.AppendLine($"<tr><td>{item.Index}</td><td>{E(item.Name)}</td><td>{E(item.Description)}</td><td>{E(item.OperStatus)}</td><td>{E(item.SpeedText)}</td><td>{item.ErrorsPerSecond:N2}</td><td>{item.DiscardsPerSecond:N2}</td><td>{E(item.TrafficRateText)}</td><td>{E(item.AttentionReason)}</td><td>{E(item.RecommendedNextCheck)}</td></tr>");
            }
            html.AppendLine("</table>");
        }

        html.AppendLine("<h3>Interfaces / Ports</h3><table style=\"width:100%;border-collapse:collapse\">");
        html.AppendLine("<tr><th align=\"left\">Index</th><th align=\"left\">Name</th><th align=\"left\">Description</th><th align=\"left\">Admin</th><th align=\"left\">Oper</th><th align=\"left\">Speed</th><th align=\"left\">Errors</th><th align=\"left\">Discards</th><th align=\"left\">Traffic</th><th align=\"left\">Traffic/s</th><th align=\"left\">Errors/s</th><th align=\"left\">Discards/s</th><th align=\"left\">Verdict</th><th align=\"left\">Next check</th></tr>");
        foreach (var item in device.Interfaces)
        {
            html.AppendLine($"<tr><td>{item.Index}</td><td>{E(item.Name)}</td><td>{E(item.Description)}</td><td>{E(item.AdminStatus)}</td><td>{E(item.OperStatus)}</td><td>{E(item.SpeedText)}</td><td>{item.Errors}</td><td>{item.Discards}</td><td>{E(item.TrafficText)}</td><td>{E(item.TrafficRateText)}</td><td>{item.ErrorsPerSecond:N2}</td><td>{item.DiscardsPerSecond:N2}</td><td>{E(item.Verdict)}</td><td>{E(item.RecommendedNextCheck)}</td></tr>");
        }
        html.AppendLine("</table>");
    }

    private static void AppendNetworkDeviceScan(StringBuilder html, NetworkDeviceScanResult? scan)
    {
        if (scan is null)
        {
            return;
        }

        html.AppendLine("<h2>Network Device LAN Scan</h2>");
        html.AppendLine($"<p><strong>{E(scan.Verdict)}</strong></p>");
        html.AppendLine("<section class=\"card\"><dl>");
        html.AppendLine(Dt("Source", scan.Source));
        html.AppendLine(Dt("Estimated hosts", scan.EstimatedHosts.ToString()));
        html.AppendLine(Dt("Max hosts", scan.MaxHosts.ToString()));
        html.AppendLine(Dt("Scanned hosts", scan.ScannedHosts.ToString()));
        html.AppendLine(Dt("Found devices", scan.FoundDevices.ToString()));
        html.AppendLine(Dt("SNMP timeouts", scan.TimedOutHosts.ToString()));
        html.AppendLine(Dt("Other failures", scan.FailedHosts.ToString()));
        html.AppendLine(Dt("Progress", scan.ProgressText));
        html.AppendLine(Dt("Skipped reason", scan.SkippedReason));
        html.AppendLine(Dt("SNMP protocol", scan.SnmpProtocol));
        html.AppendLine(Dt("SNMP credential", scan.SnmpCommunityMasked));
        html.AppendLine(Dt("Local IP", scan.LocalIpAddress));
        html.AppendLine(Dt("Adapter", scan.AdapterName));
        html.AppendLine(Dt("Gateway", scan.Gateway));
        html.AppendLine(Dt("Detected subnet", scan.DetectedSubnet));
        html.AppendLine(Dt("Safe scan range", scan.SafeScanRange));
        html.AppendLine("</dl></section>");
        AppendList(html, "Network Device Scan Evidence", scan.Evidence);
        AppendList(html, "Network Device Scan Limitations", scan.Limitations);

        if (scan.Devices.Count == 0)
        {
            return;
        }

        html.AppendLine("<h3>Devices Found</h3><table style=\"width:100%;border-collapse:collapse\">");
            html.AppendLine("<tr><th align=\"left\">Address</th><th align=\"left\">Name</th><th align=\"left\">Type</th><th align=\"left\">Confidence</th><th align=\"left\">Vendor (MAC)</th><th align=\"left\">MAC</th><th align=\"left\">Label / location</th><th align=\"left\">SNMP location</th><th align=\"left\">Ping</th><th align=\"left\">Open ports</th><th align=\"left\">Confirmation</th></tr>");
            foreach (var device in scan.Devices)
            {
                html.AppendLine($"<tr><td>{E(device.Address)}</td><td>{E(device.DisplayName)}</td><td>{E(device.DeviceType)}</td><td>{E(device.ClassificationConfidence)}</td><td>{E(device.MacVendorDisplay)}</td><td>{E(device.MacAddressDisplay)}</td><td>{E(device.FriendlyLabelDisplay)}</td><td>{E(device.LocationDisplay)}</td><td>{E(device.PingReachable ? "Yes" : "No / blocked")}</td><td>{E(device.OpenPortSummary)}</td><td>{E(device.ConfirmationStatus)}</td></tr>");
            }
            html.AppendLine("</table>");
        }
}
