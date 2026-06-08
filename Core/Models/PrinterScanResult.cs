namespace NetScopeDiagnosticCenter.Core.Models;

public sealed class PrinterScanResult
{
    public string Address { get; set; } = string.Empty;
    public string HostName { get; set; } = string.Empty;
    public List<int> OpenPorts { get; set; } = [];
    public string Classification { get; set; } = "Unknown device";
    public string Confidence { get; set; } = "Low";
    public string Reason { get; set; } = string.Empty;
    public string Details { get; set; } = string.Empty;
    public string SnmpStatus { get; set; } = "Not checked";
    public SnmpDeviceInfo? SnmpInfo { get; set; }
}
