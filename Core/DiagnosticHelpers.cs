namespace NetScopeDiagnosticCenter.Core;

/// <summary>
/// Shared helper methods previously duplicated across DiagnosticEngine, ScenarioEngine,
/// RuleEngine, LinkQualityAnalyzer, TextSummaryBuilder and HtmlReportBuilder.
/// </summary>
public static class DiagnosticHelpers
{
    /// <summary>
    /// Returns <c>true</c> when the collector status indicates success.
    /// A <c>null</c>, empty or "OK" status is treated as successful.
    /// </summary>
    public static bool IsCollectorOk(string? collectorStatus) =>
        string.IsNullOrWhiteSpace(collectorStatus) ||
        collectorStatus.Equals("OK", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// One-line "previous vs current" Quick Diagnosis comparison for the verdict card,
    /// e.g. "Previous run 14:02:11: 92/100 (Warning — DNS failure) · score +8 — improved".
    /// Empty when either run is missing.
    /// </summary>
    public static string BuildDiagnosisComparison(
        Models.NetworkDiagnosisResult? previous,
        Models.NetworkDiagnosisResult? current)
    {
        if (previous is null || current is null) return string.Empty;

        var delta = current.HealthScore.Score - previous.HealthScore.Score;
        var trend = delta > 0
            ? $"score +{delta} — improved"
            : delta < 0
                ? $"score {delta} — degraded"
                : "score unchanged";

        return $"Previous run {previous.CreatedAt:HH:mm:ss}: {previous.HealthScore.Score}/100 " +
               $"({previous.Verdict.Severity} — {previous.Verdict.Title}) · {trend}";
    }

    /// <summary>
    /// Formats a nullable double for display, returning "Unknown" when the value is absent.
    /// </summary>
    public static string FormatNullable(double? value) =>
        value.HasValue ? value.Value.ToString("N2") : "Unknown";

    /// <summary>
    /// Formats a nullable boolean as "Yes", "No" or "Unknown".
    /// </summary>
    public static string FormatBool(bool? value) =>
        value.HasValue ? (value.Value ? "Yes" : "No") : "Unknown";

    /// <summary>
    /// Returns the worst (most severe) status from a set of status strings.
    /// Order: Critical > Warning > OK > Unknown.
    /// </summary>
    public static string WorstStatus(params string[] statuses)
    {
        if (statuses.Any(s => string.Equals(s, "Critical", StringComparison.OrdinalIgnoreCase)))
            return "Critical";
        if (statuses.Any(s => string.Equals(s, "Warning", StringComparison.OrdinalIgnoreCase)))
            return "Warning";
        if (statuses.Any(s => string.Equals(s, "OK", StringComparison.OrdinalIgnoreCase)))
            return "OK";
        return "Unknown";
    }

    /// <summary>
    /// Two-argument overload for use with Aggregate / LINQ.
    /// </summary>
    public static string WorstStatus(string first, string second) =>
        WorstStatus(new[] { first, second });

    /// <summary>
    /// Returns the first non-null, non-whitespace value from the list, or <see cref="string.Empty"/>.
    /// </summary>
    public static string FirstNonEmpty(params string[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? string.Empty;

    /// <summary>
    /// Returns the details from the first test result flagged as critical,
    /// or <see cref="string.Empty"/> if none is critical.
    /// </summary>
    public static string FirstCriticalDetails(params Core.Models.TestResult[] results) =>
        results.FirstOrDefault(r => r.IsCritical)?.Details ?? string.Empty;

    /// <summary>
    /// Returns the first collector status string that is NOT OK, or <see cref="string.Empty"/>.
    /// </summary>
    public static string FirstCollectorError(params string[] statuses) =>
        statuses.FirstOrDefault(s => !IsCollectorOk(s)) ?? string.Empty;
}
