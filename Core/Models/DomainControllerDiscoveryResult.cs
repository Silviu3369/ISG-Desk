using NetScopeDiagnosticCenter.Core;

namespace NetScopeDiagnosticCenter.Core.Models;

public sealed class DomainControllerDiscoveryResult
{
    public List<string> DomainNames { get; set; } = [];
    public string Verdict { get; set; } = string.Empty;
    public string Severity { get; set; } = "Unknown";
    public string SkippedReason { get; set; } = string.Empty;
    public List<DomainControllerCandidate> Candidates { get; set; } = [];
    public List<string> Evidence { get; set; } = [];
    public List<string> Limitations { get; set; } = [];
}
