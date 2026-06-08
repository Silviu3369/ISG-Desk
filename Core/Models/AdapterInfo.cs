namespace NetScopeDiagnosticCenter.Core.Models;

public sealed class AdapterInfo
{
    public string Name { get; set; } = "Unknown";
    public string InterfaceDescription { get; set; } = "Unknown";
    public string Status { get; set; } = "Unknown";
    public string ConnectionType { get; set; } = "Unknown";
    public string LinkSpeed { get; set; } = "Unknown";
    public double? LinkSpeedMbps { get; set; }
    public string MacAddress { get; set; } = "Unknown";
    public string SpeedDuplex { get; set; } = "Unknown";
    public string DriverInformation { get; set; } = "Unknown";
    public long Errors { get; set; }
    public long Discards { get; set; }
    public long BytesSent { get; set; }
    public long BytesReceived { get; set; }
    public double? ErrorsPerSecond { get; set; }
    public double? DiscardsPerSecond { get; set; }
    public double? BytesPerSecond { get; set; }
    public string SamplingStatus { get; set; } = "Not sampled";
    public string CollectorStatus { get; set; } = "OK";
}
