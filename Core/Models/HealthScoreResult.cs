namespace NetScopeDiagnosticCenter.Core.Models;

public sealed class HealthScoreResult
{
    public int Score { get; set; } = 100;
    public string Status { get; set; } = "Healthy";
    public List<string> Penalties { get; set; } = [];
}
