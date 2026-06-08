namespace NetScopeDiagnosticCenter.Core.Models.Wifi;

/// <summary>
/// One client row reported by the router/AP through a read-only management source.
/// For SNMP ipNetToMedia this is the router's ARP/IP table: IP + MAC + interface index.
/// </summary>
public sealed record WifiRouterClient(
    string IpAddress,
    string? MacAddress,
    string? HostName,
    string? Vendor,
    string InterfaceIndex,
    bool IsAlsoDetectedLocally,
    string Source,
    bool IsWirelessAssociation = false)
{
    public string HostDisplay => string.IsNullOrWhiteSpace(HostName) ? "Name not reported" : HostName!;
    public string MacDisplay => string.IsNullOrWhiteSpace(MacAddress) ? "-" : MacAddress!;
    public string VendorDisplay => string.IsNullOrWhiteSpace(Vendor) ? "-" : Vendor!;
    public string LocalMatchLabel => IsAlsoDetectedLocally ? "Matched local scan" : "Router only";
    public string SourceDisplay => string.IsNullOrWhiteSpace(Source) ? "Router/AP reported" : Source;
    public string WirelessAssociationDisplay => IsWirelessAssociation ? "Confirmed Wi-Fi association" : "ARP/IP evidence only";
}

public sealed record WifiRouterClientScanResult(
    string GatewayIp,
    string Source,
    string Status,
    IReadOnlyList<WifiRouterClient> Clients,
    bool IsSuccess)
{
    public static WifiRouterClientScanResult Skipped(string status) => new(
        GatewayIp: string.Empty,
        Source: "SNMP ipNetToMedia",
        Status: status,
        Clients: Array.Empty<WifiRouterClient>(),
        IsSuccess: false);
}
