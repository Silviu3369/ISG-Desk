namespace NetScopeDiagnosticCenter.Core.Models;

public sealed class LinkQualityResult
{
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public string Source { get; set; } = "No data";
    public string Summary { get; set; } = "No Link Quality test has been run yet.";
    public string Severity { get; set; } = "Unknown";
    public string Confidence { get; set; } = "Low";
    public string AffectedLayer { get; set; } = "Unknown";
    public string AdapterName { get; set; } = "Unknown";
    public string InterfaceDescription { get; set; } = "Unknown";
    public string ConnectionType { get; set; } = "Unknown";
    public string MacAddress { get; set; } = "Unknown";
    public string DriverInformation { get; set; } = "Unknown";
    public string LinkSpeed { get; set; } = "Unknown";
    public double? LinkSpeedMbps { get; set; }
    public int ExpectedMinimumEthernetMbps { get; set; }
    public string SpeedDuplex { get; set; } = "Unknown";
    public long Errors { get; set; }
    public long Discards { get; set; }
    public double? ErrorsPerSecond { get; set; }
    public double? DiscardsPerSecond { get; set; }
    public double? BytesPerSecond { get; set; }
    public string SamplingStatus { get; set; } = "Not sampled";
    public LinkQualityThresholds Thresholds { get; set; } = LinkQualityThresholds.Default;
    public List<LinkQualityPingResult> PingResults { get; set; } = [];
    public List<LinkQualityDnsResult> DnsResults { get; set; } = [];
    public List<string> Evidence { get; set; } = [];
    public List<string> Recommendations { get; set; } = [];
    public List<string> Limitations { get; set; } = [];
}
