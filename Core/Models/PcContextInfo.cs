namespace NetScopeDiagnosticCenter.Core.Models;

public sealed class PcContextInfo
{
    public bool? IsAdministrator { get; set; }
    public bool? ProxyEnabled { get; set; }
    public string ProxyServer { get; set; } = "Unknown";
    public List<string> VpnAdapters { get; set; } = [];
    public List<string> DefaultRoutes { get; set; } = [];
    public bool? Ipv6Enabled { get; set; }
    public int? Mtu { get; set; }
    public string CollectorStatus { get; set; } = "OK";

    public bool HasVpn => VpnAdapters.Count > 0;
    public bool HasMultipleDefaultRoutes => DefaultRoutes.Count > 1;
}
