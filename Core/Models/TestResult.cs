namespace NetScopeDiagnosticCenter.Core.Models;

public sealed class TestResult
{
    public string Target { get; set; } = string.Empty;
    public string Status { get; set; } = "Unknown";
    public double? LatencyMs { get; set; }
    public string Details { get; set; } = string.Empty;

    public bool IsOk => string.Equals(Status, "OK", StringComparison.OrdinalIgnoreCase);
    public bool IsWarning => string.Equals(Status, "Warning", StringComparison.OrdinalIgnoreCase);
    public bool IsCritical => string.Equals(Status, "Critical", StringComparison.OrdinalIgnoreCase);

    public static TestResult Unknown(string target) => new()
    {
        Target = target,
        Status = "Unknown",
        Details = "Not tested or unavailable."
    };
}
