namespace NetScopeDiagnosticCenter.Core.Models;

public sealed class LinkQualityPingResult
{
    public string Target { get; set; } = string.Empty;
    public string Category { get; set; } = "Target";
    public string Status { get; set; } = "Unknown";
    public string ThresholdProfile { get; set; } = "Default";
    public int Sent { get; set; }
    public int Received { get; set; }
    public double? LossPercent { get; set; }
    public double? MinimumLatencyMs { get; set; }
    public double? MaximumLatencyMs { get; set; }
    public double? AverageLatencyMs { get; set; }
    public double? JitterMs { get; set; }
    public string Details { get; set; } = "Not tested.";

    public string LossText => LossPercent.HasValue ? $"{LossPercent.Value:N1}%" : "Unknown";
    public string MinimumLatencyText => MinimumLatencyMs.HasValue ? $"{MinimumLatencyMs.Value:N1} ms" : "Unknown";
    public string MaximumLatencyText => MaximumLatencyMs.HasValue ? $"{MaximumLatencyMs.Value:N1} ms" : "Unknown";
    public string AverageLatencyText => AverageLatencyMs.HasValue ? $"{AverageLatencyMs.Value:N1} ms" : "Unknown";
    public string JitterText => JitterMs.HasValue ? $"{JitterMs.Value:N1} ms" : "Unknown";
}
