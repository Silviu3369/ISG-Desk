namespace NetScopeDiagnosticCenter.Core.Models;

public sealed class ScenarioDiagnosisResult
{
    public string WorkflowKey { get; set; } = string.Empty;
    public string WorkflowName { get; set; } = string.Empty;
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset? CompletedAt { get; set; }
    public string InputSummary { get; set; } = string.Empty;
    public string BaselineSummary { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Severity { get; set; } = "Unknown";
    public string Confidence { get; set; } = "Low";
    public string AffectedLayer { get; set; } = "Unknown";
    public string OwnerSuggestion { get; set; } = "Local Support";
    public List<DiagnosisStepResult> Steps { get; set; } = [];
    public List<string> Evidence { get; set; } = [];
    public List<string> Limitations { get; set; } = [];
    public List<string> NextChecks { get; set; } = [];
    public List<TargetProbeResult> TargetResults { get; set; } = [];
}
