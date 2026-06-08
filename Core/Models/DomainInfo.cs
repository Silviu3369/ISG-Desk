namespace NetScopeDiagnosticCenter.Core.Models;

public sealed class DomainInfo
{
    public bool IsDomainJoined { get; set; }
    public string DomainName { get; set; } = "Unknown";
    public string LogonServer { get; set; } = "Unknown";
    public List<string> DnsSuffixes { get; set; } = [];
    public string CollectorStatus { get; set; } = "OK";
}
