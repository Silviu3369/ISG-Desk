namespace NetScopeDiagnosticCenter.Core.Models;

/// <summary>
/// Result of a bounded traceroute to a fixed public beacon. Lets a tech see WHERE the
/// path breaks (local switch / gateway vs ISP vs beyond) instead of just "internet down"
/// — the #1 capability professional tools (NetAlly, PingPlotter) have that a plain
/// ping/DNS check lacks.
/// </summary>
public sealed class TraceRouteResult
{
    public string Target { get; set; } = string.Empty;

    /// <summary>True if the final hop reached the target.</summary>
    public bool ReachedTarget { get; set; }

    public List<TraceHop> Hops { get; set; } = [];

    /// <summary>"OK" / "Warning" / "Critical" / "Unknown" — drives the timeline step.</summary>
    public string Status { get; set; } = "Unknown";

    /// <summary>One-line plain-language summary for the verdict/step.</summary>
    public string Summary { get; set; } = "Traceroute not run.";

    /// <summary>"OK" when the collector completed; otherwise a failure/timeout message.</summary>
    public string CollectorStatus { get; set; } = "OK";
}

public sealed class TraceHop
{
    public int Hop { get; set; }
    public string Address { get; set; } = "*";

    /// <summary>"local" (RFC-1918 / gateway), "isp/internet" (public), or "timeout".</summary>
    public string Scope { get; set; } = "unknown";
}
