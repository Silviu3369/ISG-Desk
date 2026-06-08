namespace NetScopeDiagnosticCenter.Core.Models;

public sealed class TargetProbeResult
{
    public DiagnosticTarget Target { get; set; } = new();
    public string ResolvedAddress { get; set; } = "Unknown";
    public string DnsStatus { get; set; } = "Unknown";
    public string PingStatus { get; set; } = "Unknown";
    public double? AverageLatencyMs { get; set; }
    public PacketLossResult PacketLoss { get; set; } = PacketLossResult.Unknown("Target");
    public List<PortProbeResult> Ports { get; set; } = [];
    public string ShareStatus { get; set; } = "Not tested";
    public string ShareDetails { get; set; } = string.Empty;
    public string Verdict { get; set; } = "Not evaluated";
    public string OwnerSuggestion { get; set; } = "Local Support";
    public string CollectorStatus { get; set; } = "OK";
    public string Details { get; set; } = string.Empty;

    public string PortSummary =>
        Ports.Count == 0
            ? "Not tested"
            : string.Join(", ", Ports.Select(port => $"{port.Port}:{port.StatusText}"));

    public string LossText =>
        PacketLoss.LossPercent.HasValue ? $"{PacketLoss.LossPercent.Value:N1}%" : "Unknown";

    public string LatencyText =>
        AverageLatencyMs.HasValue ? $"{AverageLatencyMs.Value:N1} ms" : "Unknown";
}
