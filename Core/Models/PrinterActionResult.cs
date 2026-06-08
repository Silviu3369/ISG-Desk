namespace NetScopeDiagnosticCenter.Core.Models;

/// <summary>Result of a one-shot printer action (e.g. "set as default printer").</summary>
public sealed class PrinterActionResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
}
