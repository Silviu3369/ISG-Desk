namespace NetScopeDiagnosticCenter.Core.Models;

public sealed class PortProbeResult
{
    public int Port { get; set; }
    public bool TcpSucceeded { get; set; }
    public double? LatencyMs { get; set; }
    public string Details { get; set; } = string.Empty;

    public string StatusText => TcpSucceeded ? "Open" : "Failed";
    public string LatencyText => LatencyMs.HasValue ? $"{LatencyMs.Value:N1} ms" : "Unknown";
}
