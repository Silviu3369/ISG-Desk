namespace NetScopeDiagnosticCenter.Core.Models;

public sealed class NetworkDiagnosisResult
{
    public string ComputerName { get; set; } = Environment.MachineName;
    public string UserName { get; set; } = Environment.UserName;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public AdapterInfo Adapter { get; set; } = new();
    public IpConfigurationInfo IpConfiguration { get; set; } = new();
    public WifiInfo? Wifi { get; set; }
    public ConnectivityTests Tests { get; set; } = new();
    public TraceRouteResult? TraceRoute { get; set; }
    public PortTestResult? LastPortTest { get; set; }
    public ScenarioDiagnosisResult? LastScenario { get; set; }
    public PrinterDiscoveryResult? LastPrinterDiscovery { get; set; }
    public NetworkDeviceResult? LastNetworkDevice { get; set; }
    public NetworkDeviceScanResult? LastNetworkDeviceScan { get; set; }
    public LinkQualityResult? LastLinkQuality { get; set; }
    public DomainInfo? Domain { get; set; }
    public DhcpInfo? Dhcp { get; set; }
    public PcContextInfo PcContext { get; set; } = new();
    public DiagnosisVerdict Verdict { get; set; } = new();
    public HealthScoreResult HealthScore { get; set; } = new();
    public NetworkProfile Profile { get; set; } = new();
    public List<DiagnosisStepResult> Steps { get; set; } = [];
    public List<string> CollectorWarnings { get; set; } = [];
}
