namespace NetScopeDiagnosticCenter.Core.Models;

public sealed class NetworkProfile
{
    public string ProfileName { get; set; } = "Default Network";
    public string DomainName { get; set; } = string.Empty;
    public string Gateway { get; set; } = string.Empty;
    public int ExpectedMinimumEthernetMbps { get; set; } = 1000;
    public string ExpectedGateway { get; set; } = string.Empty;
    public List<string> ExpectedDnsServers { get; set; } = [];
    public List<string> DnsServers { get; set; } = [];
    public List<DiagnosticTarget> DomainControllers { get; set; } = [];
    public List<DiagnosticTarget> FileServers { get; set; } = [];
    public List<DiagnosticTarget> PrintTargets { get; set; } = [];
    public List<DiagnosticTarget> ImportantServers { get; set; } = [];
    public List<int> ImportantPorts { get; set; } = [445, 3389, 80, 443, 9100, 161];
}
