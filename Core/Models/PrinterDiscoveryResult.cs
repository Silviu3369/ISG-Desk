namespace NetScopeDiagnosticCenter.Core.Models;

public sealed class PrinterDiscoveryResult
{
    public string Mode { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public string Verdict { get; set; } = "Not run";
    public string Severity { get; set; } = "Unknown";
    public int EstimatedHosts { get; set; }
    public int MaxHosts { get; set; } = 254;
    public string SkippedReason { get; set; } = string.Empty;
    public string SnmpCommunityMasked { get; set; } = string.Empty;
    public string LocalIpAddress { get; set; } = string.Empty;
    public string LocalAdapterName { get; set; } = string.Empty;
    public int? LocalPrefixLength { get; set; }
    public string Gateway { get; set; } = string.Empty;
    public int RouteMetric { get; set; }
    public int InterfaceMetric { get; set; }
    public string DetectedSubnet { get; set; } = string.Empty;
    public int DetectedSubnetHosts { get; set; }
    public string SafeScanRange { get; set; } = string.Empty;
    public string LocalSpoolerStatus { get; set; } = string.Empty;
    public string PrintServerSpoolerStatus { get; set; } = string.Empty;
    public string LastInstallResult { get; set; } = string.Empty;
    public string SelectedTarget { get; set; } = string.Empty;
    public string SelectedTargetSource { get; set; } = string.Empty;
    public string SnmpProtocol { get; set; } = string.Empty;
    public PrinterInstallResult? LastInstall { get; set; }
    public List<string> Evidence { get; set; } = [];
    public List<string> Limitations { get; set; } = [];
    public List<PrinterInfo> LocalPrinters { get; set; } = [];
    public List<PrinterInfo> Printers { get; set; } = [];
    public List<PrinterScanResult> ScanResults { get; set; } = [];
}
