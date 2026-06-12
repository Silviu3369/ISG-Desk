namespace NetScopeDiagnosticCenter.Core.Models;

public sealed class PortProbeResult
{
    public int Port { get; set; }
    public bool TcpSucceeded { get; set; }
    public double? LatencyMs { get; set; }
    public string Details { get; set; } = string.Empty;

    // ---- Application-layer HTTP probe (web ports 80/443/8080/8443 only) ----

    /// <summary>HTTP status code returned by the service, when the port answered HTTP.</summary>
    public int? HttpStatusCode { get; set; }

    /// <summary>"HTTP 401 Unauthorized" / "HTTP probe failed: …" — empty when not probed.</summary>
    public string HttpDetails { get; set; } = string.Empty;

    public bool HasHttpProbe => !string.IsNullOrWhiteSpace(HttpDetails);

    /// <summary>
    /// "Open" / "Open (HTTP 200)" / "Failed" — a TCP-open web port whose service
    /// actually answers HTTP is much stronger evidence than the socket alone.
    /// </summary>
    public string StatusText =>
        !TcpSucceeded ? "Failed"
        : HttpStatusCode.HasValue ? $"Open (HTTP {HttpStatusCode.Value})"
        : "Open";

    public string LatencyText => LatencyMs.HasValue ? $"{LatencyMs.Value:N1} ms" : "Unknown";
}
