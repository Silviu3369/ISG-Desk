namespace NetScopeDiagnosticCenter.Core.Models.Wifi;

public sealed record WifiChannelRecommendation(
    string Severity,
    string Current,
    string Recommended,
    string Reason,
    string Detail,
    DateTimeOffset GeneratedAt)
{
    public string SeverityDisplay => string.IsNullOrWhiteSpace(Severity) ? "Info" : Severity;

    public bool IsReady => !string.IsNullOrWhiteSpace(Current);

    public bool HasMoveRecommendation =>
        IsReady
        && !string.IsNullOrWhiteSpace(Recommended)
        && !Recommended.Equals("No change", StringComparison.OrdinalIgnoreCase)
        && !Recommended.Equals("No recommendation", StringComparison.OrdinalIgnoreCase);

    public string GeneratedText => IsReady ? $"Updated {GeneratedAt:HH:mm:ss}" : string.Empty;

    public static WifiChannelRecommendation NoData() => new(
        Severity: "Info",
        Current: string.Empty,
        Recommended: string.Empty,
        Reason: string.Empty,
        Detail: string.Empty,
        GeneratedAt: DateTimeOffset.Now);
}
