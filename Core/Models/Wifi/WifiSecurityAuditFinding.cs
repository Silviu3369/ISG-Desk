namespace NetScopeDiagnosticCenter.Core.Models.Wifi;

public sealed record WifiSecurityAuditFinding(
    string Severity,
    string Category,
    string Finding,
    string Evidence,
    string Recommendation)
{
    public string SeverityDisplay => string.IsNullOrWhiteSpace(Severity) ? "Info" : Severity;
}

public sealed record WifiSecurityAuditReport(
    string Summary,
    IReadOnlyList<WifiSecurityAuditFinding> Findings,
    DateTimeOffset GeneratedAt)
{
    public bool HasFindings => Findings.Count > 0;

    public static WifiSecurityAuditReport NoData() => new(
        Summary: "No Wi-Fi security audit has run yet.",
        Findings: Array.Empty<WifiSecurityAuditFinding>(),
        GeneratedAt: DateTimeOffset.Now);
}
