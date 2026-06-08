namespace NetScopeDiagnosticCenter.Core.Models;

public sealed class RecentTargets
{
    public List<string> ServerTargets { get; set; } = [];
    public List<string> ServiceTargets { get; set; } = [];
    public List<string> PrinterTargets { get; set; } = [];
    public string LastPrintServer { get; set; } = string.Empty;
    public string DomainControllerOverride { get; set; } = string.Empty;
}
