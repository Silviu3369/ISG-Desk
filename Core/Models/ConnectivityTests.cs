namespace NetScopeDiagnosticCenter.Core.Models;

public sealed class ConnectivityTests
{
    public TestResult Gateway { get; set; } = TestResult.Unknown("Gateway");
    public TestResult DnsServer { get; set; } = TestResult.Unknown("DNS server");
    public List<TestResult> DnsServerResults { get; set; } = [];
    public TestResult DnsResolution { get; set; } = TestResult.Unknown("DNS resolution");
    public TestResult InternetPing { get; set; } = TestResult.Unknown("Internet IP");
    public List<TestResult> InternetPingResults { get; set; } = [];
    public TestResult InternetDnsResolution { get; set; } = TestResult.Unknown("Internet DNS");
    public TestResult ExternalTcp443 { get; set; } = TestResult.Unknown("External TCP 443");
    public TestResult HttpsGet { get; set; } = TestResult.Unknown("HTTPS GET");

    /// <summary>
    /// Captive-portal probe (Windows NCSI method): GET the plain-HTTP connect-test URL
    /// and require the EXACT expected body. A different body / redirect / non-200 means a
    /// portal is intercepting traffic — a "Critical" Layer-7 false-positive that a pure
    /// ping/DNS check would miss (the gateway pings fine, DNS resolves, but nothing works
    /// until the user signs in).
    /// </summary>
    public TestResult CaptivePortal { get; set; } = TestResult.Unknown("Captive portal");
    public PacketLossResult GatewayPacketLoss { get; set; } = PacketLossResult.Unknown("Gateway");
    public PacketLossResult InternetPacketLoss { get; set; } = PacketLossResult.Unknown("Internet IP");

    /// <summary>
    /// "OK" when the connectivity collector ran to completion; otherwise a human message
    /// (timeout / failure). Anything other than "OK" must be treated as "diagnosis
    /// incomplete" — never as a healthy network — by the rule engine + health score.
    /// </summary>
    public string CollectorStatus { get; set; } = "OK";
}
