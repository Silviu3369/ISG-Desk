namespace NetScopeDiagnosticCenter.Core.Models;

public sealed class DiagnosisVerdict
{
    public string Title { get; set; } = "No diagnosis has been run yet";
    public string Severity { get; set; } = "Unknown";
    public string Confidence { get; set; } = "Low";
    public string AffectedLayer { get; set; } = "Unknown";
    public List<string> Evidence { get; set; } = [];
    public List<string> Limitations { get; set; } = [];
    public List<string> Recommendations { get; set; } = [];
}
