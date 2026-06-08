namespace NetScopeDiagnosticCenter.Infrastructure;

public sealed class PowerShellExecutionResult
{
    public bool Success { get; init; }
    public string Output { get; init; } = string.Empty;
    public string Error { get; init; } = string.Empty;
    public int ExitCode { get; init; }
    public bool TimedOut { get; init; }
}
