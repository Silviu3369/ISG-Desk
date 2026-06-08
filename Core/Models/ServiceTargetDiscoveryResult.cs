using NetScopeDiagnosticCenter.Core;

namespace NetScopeDiagnosticCenter.Core.Models;

public sealed class ServiceTargetDiscoveryResult
{
    public string Source { get; set; } = string.Empty;
    public string Verdict { get; set; } = string.Empty;
    public string Severity { get; set; } = "Unknown";
    public int ScannedHosts { get; set; }
    public int MaxHosts { get; set; } = DiagnosticConstants.MaxServiceDiscoveryHosts;
    public string SkippedReason { get; set; } = string.Empty;
    public List<int> Ports { get; set; } = [];
    public List<ServiceTargetCandidate> Candidates { get; set; } = [];
    public List<string> Evidence { get; set; } = [];
    public List<string> Limitations { get; set; } = [];
}
