namespace NetScopeDiagnosticCenter.Core.Models.Wifi;

public sealed record WifiChannelBeforeAfterSummary(
    string Severity,
    string Title,
    string Before,
    string After,
    string Delta,
    string Detail,
    DateTimeOffset GeneratedAt)
{
    public string SeverityDisplay => string.IsNullOrWhiteSpace(Severity) ? "Info" : Severity;

    public bool IsReady => !string.IsNullOrWhiteSpace(Title);

    public string GeneratedText => IsReady ? $"Updated {GeneratedAt:HH:mm:ss}" : string.Empty;

    public static WifiChannelBeforeAfterSummary NoData() => new(
        Severity: "Info",
        Title: string.Empty,
        Before: string.Empty,
        After: string.Empty,
        Delta: string.Empty,
        Detail: string.Empty,
        GeneratedAt: DateTimeOffset.Now);
}
