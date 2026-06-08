namespace NetScopeDiagnosticCenter.Core.Models;

public sealed class DhcpInfo
{
    public string AdapterName { get; set; } = "Unknown";
    public bool? DhcpEnabled { get; set; }
    public string DhcpServer { get; set; } = "Unknown";
    public string LeaseObtained { get; set; } = "Unknown";
    public string LeaseExpires { get; set; } = "Unknown";
    public string CollectorStatus { get; set; } = "OK";
}
