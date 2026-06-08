namespace NetScopeDiagnosticCenter.Core;

public sealed class ShareTargetCandidate
{
    public string Target { get; init; } = string.Empty;
    public string Source { get; init; } = string.Empty;
    public string Confidence { get; init; } = "Medium";
}
