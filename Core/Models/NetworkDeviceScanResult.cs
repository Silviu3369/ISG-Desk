namespace NetScopeDiagnosticCenter.Core.Models;

public sealed class NetworkDeviceScanResult
{
    public string Source { get; set; } = string.Empty;
    public string Verdict { get; set; } = "Not run";
    public string Severity { get; set; } = "Unknown";
    public int EstimatedHosts { get; set; }
    public int MaxHosts { get; set; } = 256;
    public int ScannedHosts { get; set; }
    public int TimedOutHosts { get; set; }
    public int FailedHosts { get; set; }
    public bool IsRunning { get; set; }
    public string SkippedReason { get; set; } = string.Empty;
    public string SnmpProtocol { get; set; } = "SNMP v2c";
    public string SnmpCommunityMasked { get; set; } = string.Empty;
    public string LocalIpAddress { get; set; } = string.Empty;
    public string AdapterName { get; set; } = string.Empty;
    public string Gateway { get; set; } = string.Empty;
    public int RouteMetric { get; set; }
    public int InterfaceMetric { get; set; }
    public string DetectedSubnet { get; set; } = string.Empty;
    public string SafeScanRange { get; set; } = string.Empty;
    public List<NetworkDeviceResult> Devices { get; set; } = [];
    public List<string> Evidence { get; set; } = [];
    public List<string> Limitations { get; set; } = [];

    public int FoundDevices => Devices.Count;

    public string SourceDisplay => ValueOrNone(Source);

    public string VerdictDisplay => ValueOrUnknown(Verdict);

    public string SeverityDisplay => ValueOrUnknown(Severity);

    public string SnmpProtocolDisplay => ValueOrUnknown(SnmpProtocol);

    public string SnmpCommunityMaskedDisplay =>
        string.IsNullOrWhiteSpace(SnmpCommunityMasked) ? "Not used" : SnmpCommunityMasked;

    public string AdapterNameDisplay => ValueOrUnknown(AdapterName);

    public string SkippedReasonDisplay => ValueOrNone(SkippedReason);

    public string ProgressText =>
        EstimatedHosts <= 0
            ? "Not started"
            : $"{ScannedHosts}/{EstimatedHosts} scanned; {FoundDevices} found; {TimedOutHosts} SNMP timeouts";

    public double ProgressPercent =>
        EstimatedHosts <= 0 ? 0 : Math.Round(Math.Min(100, (ScannedHosts / (double)EstimatedHosts) * 100), 1);

    private static string ValueOrUnknown(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "Unknown" : value;

    private static string ValueOrNone(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "None" : value;
}
