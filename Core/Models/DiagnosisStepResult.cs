using System.Globalization;

namespace NetScopeDiagnosticCenter.Core.Models;

public sealed class DiagnosisStepResult
{
    public string Name { get; set; } = string.Empty;
    public string Layer { get; set; } = string.Empty;
    public string Status { get; set; } = "Unknown";
    public double DurationMs { get; set; }
    public string DurationDisplayText => DurationMs <= 0
        ? "not timed"
        : DurationMs >= 1000
            ? string.Format(CultureInfo.InvariantCulture, "run {0:0.0} s", DurationMs / 1000d)
            : string.Format(CultureInfo.InvariantCulture, "run {0:0} ms", DurationMs);
    public List<string> Evidence { get; set; } = [];
    public string Error { get; set; } = string.Empty;
    public string Recommendation { get; set; } = string.Empty;
}
