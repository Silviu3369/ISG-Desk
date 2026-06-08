namespace NetScopeDiagnosticCenter.Core.Models.Wifi;

public sealed record WifiSavedProfileHygieneItem(
    string Severity,
    string ProfileName,
    string State,
    string Security,
    string Evidence,
    string Recommendation)
{
    public string SeverityDisplay => string.IsNullOrWhiteSpace(Severity) ? "Info" : Severity;

    public string ProfileDisplay => string.IsNullOrWhiteSpace(ProfileName)
        ? "(unnamed profile)"
        : ProfileName;

    public string StateDisplay => string.IsNullOrWhiteSpace(State) ? "Unknown" : State;

    public string SecurityDisplay => string.IsNullOrWhiteSpace(Security) ? "Not observed" : Security;

    public string EvidenceDisplay => string.IsNullOrWhiteSpace(Evidence) ? "-" : Evidence;

    public string RecommendationDisplay => string.IsNullOrWhiteSpace(Recommendation)
        ? "No action required."
        : Recommendation;
}

public sealed record WifiSavedProfileHygieneReport(
    string Summary,
    IReadOnlyList<WifiSavedProfileHygieneItem> Items,
    DateTimeOffset GeneratedAt)
{
    public bool HasItems => Items.Count > 0;

    public static WifiSavedProfileHygieneReport NoData() => new(
        Summary: "Start Analyzer to audit saved Wi-Fi profiles.",
        Items: Array.Empty<WifiSavedProfileHygieneItem>(),
        GeneratedAt: DateTimeOffset.Now);
}
