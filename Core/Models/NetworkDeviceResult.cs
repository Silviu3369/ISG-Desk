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

    /// <summary>Windows machine name from NetBIOS adapter status (empty for non-Windows hosts).</summary>
    public string NetBiosName { get; set; } = string.Empty;

    /// <summary>True when this address is the scan adapter's default gateway.</summary>
    public bool IsGateway { get; set; }

    /// <summary>Technician-assigned label ("Imprimanta etaj 2") persisted per MAC/IP.</summary>
    public string FriendlyLabel { get; set; } = string.Empty;
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

    /// <summary>
    /// Best human name for the device row: technician label > SNMP sysName > NetBIOS >
    /// reverse DNS > MAC vendor — always something more useful than a bare IP when known.
    /// </summary>
    public string DisplayName
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(FriendlyLabel)) return FriendlyLabel.Trim();
            var sysName = Identity?.SysName;
            if (!string.IsNullOrWhiteSpace(sysName)) return sysName.Trim();
            if (!string.IsNullOrWhiteSpace(NetBiosName)) return NetBiosName.Trim();
            if (!string.IsNullOrWhiteSpace(ReverseDnsName)) return ReverseDnsName.Trim();
            if (!string.IsNullOrWhiteSpace(MacVendor)) return $"{MacVendor.Trim()} device";
            return "Unknown";
        }
    }

    /// <summary>SNMP sysLocation when populated by the admin; otherwise em dash.</summary>
    public string LocationDisplay
    {
        get
        {
            var location = Identity?.SysLocation;
            return string.IsNullOrWhiteSpace(location) ? "—" : location.Trim();
        }
    }

    public string FriendlyLabelDisplay =>
        string.IsNullOrWhiteSpace(FriendlyLabel) ? "—" : FriendlyLabel.Trim();

    private static string ValueOrUnknown(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "Unknown" : value;
}
