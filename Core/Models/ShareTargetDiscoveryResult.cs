using NetScopeDiagnosticCenter.Core;

namespace NetScopeDiagnosticCenter.Core.Models;

public sealed class ShareTargetDiscoveryResult
{
    public string Source { get; set; } = string.Empty;
    public string Verdict { get; set; } = string.Empty;
    public string Severity { get; set; } = "Unknown";
    public int ScannedHosts { get; set; }
    public int MaxHosts { get; set; } = DiagnosticConstants.MaxSmbDiscoveryHosts;
    public string SkippedReason { get; set; } = string.Empty;
    public List<ShareTargetCandidate> Candidates { get; set; } = [];
    public List<string> Evidence { get; set; } = [];
    public List<string> Limitations { get; set; } = [];
}
