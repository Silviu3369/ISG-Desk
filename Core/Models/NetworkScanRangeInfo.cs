namespace NetScopeDiagnosticCenter.Core.Models;

public sealed class NetworkScanRangeInfo
{
    public string LocalIpAddress { get; set; } = string.Empty;
    public string AdapterName { get; set; } = string.Empty;
    public int InterfaceIndex { get; set; }
    public int? PrefixLength { get; set; }
    public string Gateway { get; set; } = string.Empty;
    public int RouteMetric { get; set; }
    public int InterfaceMetric { get; set; }
    public string DetectedSubnet { get; set; } = string.Empty;
    public int DetectedSubnetHosts { get; set; }
    public string SafeScanRange { get; set; } = string.Empty;
    public string ScanRange { get; set; } = string.Empty;
    public string Warning { get; set; } = string.Empty;
    public string Error { get; set; } = string.Empty;
    public List<string> Evidence { get; set; } = [];
}
