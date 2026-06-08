namespace NetScopeDiagnosticCenter.Core.Models;

public sealed class LinkQualityDnsResult
{
    public string Name { get; set; } = string.Empty;
    public string Status { get; set; } = "Unknown";
    public string ThresholdProfile { get; set; } = "Default DNS";
    public double? LatencyMs { get; set; }
    public List<string> Addresses { get; set; } = [];
    public string Details { get; set; } = "Not tested.";

    public string LatencyText => LatencyMs.HasValue ? $"{LatencyMs.Value:N1} ms" : "Unknown";
    public string AddressSummary => Addresses.Count == 0 ? "None" : string.Join(", ", Addresses);
}
