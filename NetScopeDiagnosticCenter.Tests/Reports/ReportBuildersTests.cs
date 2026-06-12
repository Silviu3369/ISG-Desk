using NetScopeDiagnosticCenter.Core.Models;
using NetScopeDiagnosticCenter.Reports;

namespace NetScopeDiagnosticCenter.Tests.Reports;

/// <summary>
/// Pins the report sections added for the new session data: traceroute hops,
/// published shares on targeted-test probes, and discovery-era device rows
/// (name precedence, MAC vendor, technician label) in the LAN-scan table.
/// </summary>
public class ReportBuildersTests
{
    private static NetworkDiagnosisResult BuildRichDiagnosis() => new()
    {
        TraceRoute = new TraceRouteResult
        {
            Target = "1.1.1.1",
            Status = "Warning",
            Summary = "Break is upstream of the LAN.",
            ReachedTarget = false,
            Hops =
            [
                new TraceHop { Hop = 1, Address = "192.168.0.1", Scope = "local" },
                new TraceHop { Hop = 2, Address = "*", Scope = "timeout" }
            ]
        },
        LastScenario = new ScenarioDiagnosisResult
        {
            WorkflowKey = ScenarioWorkflow.InternalShare,
            WorkflowName = "Internal Server or Share Access",
            Title = "Target connectivity looks healthy for tested targets",
            Severity = "OK",
            TargetResults =
            [
                new TargetProbeResult
                {
                    Target = new DiagnosticTarget { Host = "srv01", Purpose = "File server" },
                    DnsStatus = "OK",
                    PingStatus = "OK",
                    ShareStatus = "OK",
                    ShareDetails = "UNC share path is reachable.",
                    ShareEnumStatus = "OK",
                    VisibleShares = ["Public", "Scans$ (hidden)"]
                }
            ]
        },
        LastNetworkDeviceScan = new NetworkDeviceScanResult
        {
            Source = "192.168.0.0/24",
            Verdict = "Found 1 device(s) in 192.168.0.0/24; 0 answered SNMP.",
            Severity = "OK",
            Devices =
            [
                new NetworkDeviceResult
                {
                    Address = "192.168.0.50",
                    NetBiosName = "DESKTOP-LAB",
                    MacAddress = "AA:BB:CC:DD:EE:50",
                    MacVendor = "Contoso Networks",
                    FriendlyLabel = "Imprimanta etaj 2",
                    DeviceType = "Printer",
                    ClassificationConfidence = "High",
                    ConfirmationStatus = "Heuristic (ports / vendor / names)",
                    PingReachable = true,
                    Ports = [new PortProbeResult { Port = 9100, TcpSucceeded = true }]
                }
            ]
        }
    };

    [Fact]
    public void Html_IncludesTraceRouteSectionWithHops()
    {
        var html = new HtmlReportBuilder().Build(BuildRichDiagnosis());

        html.Should().Contain("Path (traceroute)");
        html.Should().Contain("Break is upstream of the LAN.");
        html.Should().Contain("192.168.0.1");
        html.Should().Contain("timeout");
    }

    [Fact]
    public void Html_TargetedTestTable_IncludesPublishedShares()
    {
        var html = new HtmlReportBuilder().Build(BuildRichDiagnosis());

        html.Should().Contain("Published shares");
        html.Should().Contain("Public, Scans$ (hidden)");
    }

    [Fact]
    public void Html_ScanTable_UsesDiscoveryColumns()
    {
        var html = new HtmlReportBuilder().Build(BuildRichDiagnosis());

        html.Should().Contain("<h3>Devices Found</h3>");
        html.Should().NotContain("SNMP Devices Found");
        html.Should().Contain("Imprimanta etaj 2", "the technician label is the device's display name");
        html.Should().Contain("Contoso Networks");
        html.Should().Contain("AA:BB:CC:DD:EE:50");
    }

    [Fact]
    public void Text_IncludesTraceRouteSharesAndDiscoveryRows()
    {
        var text = new TextSummaryBuilder().Build(BuildRichDiagnosis());

        text.Should().Contain("Path (traceroute):");
        text.Should().Contain("Hop 1: 192.168.0.1 (local)");
        text.Should().Contain("Published shares: 2 share(s) published: Public, Scans$ (hidden).");
        text.Should().Contain("Name=Imprimanta etaj 2");
        text.Should().Contain("Vendor=Contoso Networks");
        text.Should().Contain("Open ports=9100");
    }
}
