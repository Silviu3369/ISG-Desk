namespace NetScopeDiagnosticCenter.Core;

/// <summary>
/// Centralized constants used across the diagnostic engine, scenarios and UI.
/// Replaces the former RecommendationBuilder class.
/// </summary>
public static class DiagnosticConstants
{
    /// <summary>Common TCP ports offered for quick port-test buttons.</summary>
    public static IReadOnlyList<int> CommonPorts { get; } = [445, 3389, 80, 443, 9100, 161];

    /// <summary>Default timeout for a single ICMP ping probe (ms).</summary>
    public const int DefaultPingTimeoutMs = 1200;

    /// <summary>Default SNMP community string for v2c discovery.</summary>
    public const string DefaultSnmpCommunity = "public";

    /// <summary>Maximum hosts allowed by the printer discovery scanner.</summary>
    public const int MaxPrinterScanHosts = 254;

    /// <summary>Maximum scanned printer candidates auto-identified by SNMP in one UI action.</summary>
    public const int MaxPrinterAutoSnmpTargets = 32;

    /// <summary>Maximum hosts allowed by the network device SNMP scanner.</summary>
    public const int MaxNetworkDeviceScanHosts = 256;

    /// <summary>Maximum explicit host/share targets allowed in one targeted-test run.</summary>
    public const int MaxTargetedTestTargets = 10;

    /// <summary>Maximum TCP ports allowed per targeted-test target.</summary>
    public const int MaxTargetedTestPorts = 12;

    /// <summary>Maximum characters accepted in a single targeted-test input box.</summary>
    public const int MaxTargetedTestInputLength = 2048;

    /// <summary>Maximum private IPv4 hosts scanned by Targeted Tests SMB discovery.</summary>
    public const int MaxSmbDiscoveryHosts = 254;

    /// <summary>Maximum private IPv4 hosts scanned by Targeted Tests service-port discovery.</summary>
    public const int MaxServiceDiscoveryHosts = 254;

    /// <summary>Maximum domain names queried during DC DNS SRV discovery.</summary>
    public const int MaxDomainDiscoveryDomains = 4;

    /// <summary>
    /// Printer-protocol TCP ports used during network printer discovery: RAW/JetDirect
    /// 9100, LPR/LPD 515 and IPP 631. Web ports (80/443) are deliberately NOT scanned —
    /// they made every router/NAS/camera with a web UI show up as a "possible printer".
    /// SNMP identification confirms identity afterwards.
    /// </summary>
    public static IReadOnlyList<int> PrinterPorts { get; } = [9100, 515, 631];

    /// <summary>Well-known management TCP ports scanned during network device reachability checks.</summary>
    public static IReadOnlyList<int> ManagementPorts { get; } = [22, 23, 80, 443, 8080, 8443];

    /// <summary>External targets used by the diagnostic engine to test internet connectivity.</summary>
    public static IReadOnlyList<string> InternetPingTargets { get; } = ["1.1.1.1", "8.8.8.8"];

    /// <summary>FQDN hostnames used for DNS resolution tests (full FQDN, no further prefixing needed).</summary>
    public static IReadOnlyList<string> DnsTestHostnames { get; } = ["www.google.com", "www.microsoft.com"];
}
