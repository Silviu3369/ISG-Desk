namespace NetScopeDiagnosticCenter.Core.Models;

public sealed class NetworkDeviceResult
{
    public string Target { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public string Verdict { get; set; } = "Not run";
    public string Severity { get; set; } = "Unknown";
    public string DeviceType { get; set; } = "Unknown";
    public string ClassificationConfidence { get; set; } = "Low";
    public string ConfirmationStatus { get; set; } = "Unknown / unsupported";
    public string AffectedLayer { get; set; } = "Unknown";
    public string OwnerSuggestion { get; set; } = "Network";
    public string Confidence { get; set; } = "Low";
    public string SnmpProtocol { get; set; } = "SNMP v2c";
    public string SnmpStatus { get; set; } = "Not tested";
    public string SnmpCommunityMasked { get; set; } = string.Empty;
    public string DnsStatus { get; set; } = "Unknown";
    public string ReverseDnsName { get; set; } = string.Empty;
    public string PingStatus { get; set; } = "Unknown";
    public double? AverageLatencyMs { get; set; }
    public PacketLossResult PacketLoss { get; set; } = PacketLossResult.Unknown("Network device");
    public string MacAddress { get; set; } = string.Empty;
    public string MacVendor { get; set; } = string.Empty;
    public string NeighborState { get; set; } = string.Empty;
    public string NeighborInterface { get; set; } = string.Empty;
    public bool PingReachable { get; set; }
    public SnmpDeviceInfo? Identity { get; set; }
    public List<PortProbeResult> Ports { get; set; } = [];
    public List<NetworkDeviceInterfaceInfo> Interfaces { get; set; } = [];
    public List<NetworkDeviceInterfaceInfo> PortsNeedingAttention { get; set; } = [];
    public List<string> ConfirmedFindings { get; set; } = [];
    public List<string> ProbableFindings { get; set; } = [];
    public List<string> UnknownFindings { get; set; } = [];
    public List<string> Evidence { get; set; } = [];
    public List<string> Limitations { get; set; } = [];
    public List<string> Recommendations { get; set; } = [];

    public string LossText =>
        PacketLoss.LossPercent.HasValue ? $"{PacketLoss.LossPercent.Value:N1}%" : "Unknown";

    public string LatencyText =>
        AverageLatencyMs.HasValue ? $"{AverageLatencyMs.Value:N1} ms" : "Unknown";

    public string ReverseDnsNameDisplay => ValueOrUnknown(ReverseDnsName);

    public string TargetDisplay => ValueOrUnknown(Target);

    public string AddressDisplay => ValueOrUnknown(Address);

    public string VerdictDisplay => ValueOrUnknown(Verdict);

    public string SeverityDisplay => ValueOrUnknown(Severity);

    public string DeviceTypeDisplay => ValueOrUnknown(DeviceType);

    public string ClassificationConfidenceDisplay => ValueOrUnknown(ClassificationConfidence);

    public string AffectedLayerDisplay => ValueOrUnknown(AffectedLayer);

    public string OwnerSuggestionDisplay => ValueOrUnknown(OwnerSuggestion);

    public string ConfidenceDisplay => ValueOrUnknown(Confidence);

    public string ConfirmationStatusDisplay => ValueOrUnknown(ConfirmationStatus);

    public string SnmpProtocolDisplay => ValueOrUnknown(SnmpProtocol);

    public string SnmpStatusDisplay => ValueOrUnknown(SnmpStatus);

    public string DnsStatusDisplay => ValueOrUnknown(DnsStatus);

    public string PingStatusDisplay => ValueOrUnknown(PingStatus);

    public string PingReachableText =>
        PingReachable
            ? "Yes"
            : string.IsNullOrWhiteSpace(PingStatus) || PingStatus.Equals("Unknown", StringComparison.OrdinalIgnoreCase)
                ? "Unknown"
                : "No";

    public string IdentityNameDisplay => Identity?.SysNameDisplay ?? "Unknown";

    public string IdentityDescriptionDisplay => Identity?.SysDescrDisplay ?? "Unknown";

    public string MacAddressDisplay => ValueOrUnknown(MacAddress);

    public string MacVendorDisplay => ValueOrUnknown(MacVendor);

    public string NeighborStateDisplay => ValueOrUnknown(NeighborState);

    public string NeighborInterfaceDisplay => ValueOrUnknown(NeighborInterface);

    public string OpenPortSummary =>
        Ports.Count == 0
            ? "Not tested"
            : Ports.Any(port => port.TcpSucceeded)
                ? string.Join(", ", Ports.Where(port => port.TcpSucceeded).Select(port => port.Port))
                : "None open";

    private static string ValueOrUnknown(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "Unknown" : value;
}
