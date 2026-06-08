namespace NetScopeDiagnosticCenter.Core.Models.Wifi;

/// <summary>
/// One device discovered on the local Wi-Fi/LAN subnet. The scanner builds it from the
/// OS ARP/neighbour cache, then enriches it with best-effort device name and OUI vendor
/// data.
/// </summary>
public sealed record WifiLanDevice(
    string IpAddress,
    string? MacAddress,
    string? HostName,
    string? Vendor,
    bool IsGateway,
    bool IsThisPc,
    string? NameSource = null,
    bool IsWifiAccessPoint = false,
    bool IsLikelyWifiAccessPoint = false,
    string? AccessPointBssid = null,
    string? RoleSource = null)
{
    /// <summary>Sort key: numeric last octet so .1, .2, .10, .100 stay in natural order.</summary>
    public int LastOctet
    {
        get
        {
            var dot = IpAddress.LastIndexOf('.');
            return dot >= 0 && int.TryParse(IpAddress[(dot + 1)..], out var v) ? v : 0;
        }
    }

    public string RoleLabel => IsThisPc
        ? "This PC"
        : IsGateway && IsWifiAccessPoint
            ? "Internet gateway / Wi-Fi AP"
            : IsGateway && IsLikelyWifiAccessPoint
                ? "Internet gateway / likely AP"
                : IsGateway
                    ? "Internet gateway"
                    : IsWifiAccessPoint
                        ? "Wi-Fi AP"
                        : IsLikelyWifiAccessPoint
                            ? "Likely Wi-Fi AP"
                            : string.Empty;

    public string RoleSourceDisplay => !string.IsNullOrWhiteSpace(RoleSource)
        ? RoleSource!
        : IsThisPc
            ? "Local Wi-Fi adapter."
            : IsGateway
                ? "Default IPv4 gateway for this Wi-Fi connection."
                : IsWifiAccessPoint
                    ? "Device MAC matches the connected Wi-Fi BSSID."
                    : IsLikelyWifiAccessPoint
                        ? "Device MAC is related to the connected Wi-Fi BSSID."
                        : string.Empty;

    public bool HasResolvedName => !string.IsNullOrWhiteSpace(HostName);

    public string HostDisplay => HasResolvedName ? HostName! : "No advertised name";
    public string NameSourceDisplay => !string.IsNullOrWhiteSpace(NameSource)
        ? NameSource!
        : HasResolvedName
            ? "Resolved"
            : "No DNS, NetBIOS, or Windows cache name was advertised by this device.";
    public string MacDisplay => string.IsNullOrWhiteSpace(MacAddress) ? "-" : MacAddress!;
    public string VendorDisplay => string.IsNullOrWhiteSpace(Vendor) ? "-" : Vendor!;
}
