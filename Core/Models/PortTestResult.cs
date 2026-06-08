namespace NetScopeDiagnosticCenter.Core.Models;

public sealed class PortTestResult
{
    public string Target { get; set; } = string.Empty;
    public int Port { get; set; }
    public bool PingSucceeded { get; set; }
    public bool TcpSucceeded { get; set; }
    public string RemoteAddress { get; set; } = "Unknown";
    public string Verdict { get; set; } = "Not tested";
    public string Severity { get; set; } = "Unknown";
    public string Details { get; set; } = string.Empty;
}
