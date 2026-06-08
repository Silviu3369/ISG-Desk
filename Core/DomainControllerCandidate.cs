namespace NetScopeDiagnosticCenter.Core;

public sealed class DomainControllerCandidate
{
    public string Host { get; init; } = string.Empty;
    public string DomainName { get; init; } = string.Empty;
    public string Source { get; init; } = string.Empty;
    public string Confidence { get; init; } = "Medium";
}
