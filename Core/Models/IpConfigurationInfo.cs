namespace NetScopeDiagnosticCenter.Core.Models;

public sealed class IpConfigurationInfo
{
    public string InterfaceAlias { get; set; } = "Unknown";
    public string IpAddress { get; set; } = "Unknown";
    public string Gateway { get; set; } = "Unknown";
    public List<string> DnsServers { get; set; } = [];
    public string CollectorStatus { get; set; } = "OK";

    public bool HasValidIp =>
        !string.IsNullOrWhiteSpace(IpAddress) &&
        !IpAddress.Equals("Unknown", StringComparison.OrdinalIgnoreCase) &&
        !IpAddress.StartsWith("169.254.", StringComparison.OrdinalIgnoreCase);

    public bool IsApipa => IpAddress.StartsWith("169.254.", StringComparison.OrdinalIgnoreCase);

    public bool HasGateway =>
        !string.IsNullOrWhiteSpace(Gateway) &&
        !Gateway.Equals("Unknown", StringComparison.OrdinalIgnoreCase);

    public bool HasDnsServers => DnsServers.Count > 0;
}
