namespace NetScopeDiagnosticCenter.Core.Models;

/// <summary>
/// Outcome of one network repair action (flush DNS, renew DHCP, reset Winsock,
/// restart adapter). Read-only display model for the Diagnosis page's Repair Actions
/// section and the activity feed.
/// </summary>
public sealed class RepairActionResult
{
    public string ActionName { get; init; } = string.Empty;

    public bool Success { get; init; }

    /// <summary>One-line outcome for the result banner and the activity feed.</summary>
    public string Summary { get; init; } = string.Empty;

    /// <summary>Optional command output lines (already trimmed to a short tail).</summary>
    public IReadOnlyList<string> OutputLines { get; init; } = [];

    /// <summary>True when Windows needs a restart for the repair to fully apply (Winsock reset).</summary>
    public bool RestartRequired { get; init; }

    public DateTimeOffset CompletedAt { get; init; } = DateTimeOffset.Now;

    /// <summary>"OK" / "Warning" / "Critical" — keyed by the StatusBrush converter.</summary>
    public string Severity => !Success ? "Critical" : RestartRequired ? "Warning" : "OK";

    public bool HasOutputLines => OutputLines.Count > 0;

    public string CompletedAtText => CompletedAt.ToString("HH:mm:ss");
}
