using System.Reflection;
using NetScopeDiagnosticCenter.Core;
using NetScopeDiagnosticCenter.Core.Models;

namespace NetScopeDiagnosticCenter.Tests.Core;

public sealed class DiagnosticEngineDnsStepTests
{
    [Fact]
    public void BuildDnsStep_IncludesLiveDnsServersAndInterface()
    {
        var tests = new ConnectivityTests
        {
            DnsServer = new TestResult { Target = "DNS servers", Status = "OK", Details = "1 OK, 0 warning, 0 critical." },
            DnsResolution = new TestResult { Target = "External DNS lookup", Status = "OK", LatencyMs = 22.4, Details = "Resolved www.microsoft.com using system resolver." },
            InternetDnsResolution = new TestResult { Target = "Internet DNS lookup", Status = "OK", LatencyMs = 18.9, Details = "Resolved www.google.com using system resolver." },
            DnsServerResults =
            [
                new TestResult { Target = "DNS server 192.168.1.1", Status = "OK", LatencyMs = 11.2, Details = "Resolved www.microsoft.com using DNS server 192.168.1.1." }
            ]
        };
        var ip = new IpConfigurationInfo
        {
            InterfaceAlias = "Wi-Fi",
            DnsServers = ["192.168.1.1"]
        };

        var step = BuildDnsStep(tests, ip);

        step.Status.Should().Be("OK");
        step.Evidence.Should().Contain("Source: live DNS server list from the current IP configuration.");
        step.Evidence.Should().Contain("Interface: Wi-Fi; configured DNS servers: 192.168.1.1.");
    }

    [Fact]
    public void BuildDnsStep_WithoutConfiguredDnsServers_IsCriticalEvenIfSystemLookupWorks()
    {
        var tests = new ConnectivityTests
        {
            DnsServer = TestResult.Unknown("DNS servers"),
            DnsResolution = new TestResult { Target = "External DNS lookup", Status = "OK", Details = "Resolved www.microsoft.com using system resolver." },
            InternetDnsResolution = new TestResult { Target = "Internet DNS lookup", Status = "OK", Details = "Resolved www.google.com using system resolver." }
        };
        var ip = new IpConfigurationInfo
        {
            InterfaceAlias = "Ethernet",
            DnsServers = []
        };

        var step = BuildDnsStep(tests, ip);

        step.Status.Should().Be("Critical");
        step.Error.Should().Be("No configured DNS servers on the active adapter.");
        step.Recommendation.Should().Contain("DHCP DNS options");
    }

    [Fact]
    public void BuildDnsStep_WhenConnectivityCollectorFails_SurfacesCollectorError()
    {
        var tests = new ConnectivityTests
        {
            CollectorStatus = "DNS/internet collector timed out.",
            DnsServer = TestResult.Unknown("DNS servers"),
            DnsResolution = TestResult.Unknown("External DNS lookup"),
            InternetDnsResolution = TestResult.Unknown("Internet DNS lookup")
        };
        var ip = new IpConfigurationInfo
        {
            InterfaceAlias = "Ethernet",
            DnsServers = ["192.168.1.1"]
        };

        var step = BuildDnsStep(tests, ip);

        step.Status.Should().Be("Warning");
        step.Error.Should().Be("DNS/internet collector timed out.");
    }

    private static DiagnosisStepResult BuildDnsStep(ConnectivityTests tests, IpConfigurationInfo ip)
    {
        var method = typeof(DiagnosticEngine).GetMethod(
            "BuildDnsStep",
            BindingFlags.NonPublic | BindingFlags.Static);

        method.Should().NotBeNull();
        return (DiagnosisStepResult)method!.Invoke(null, [tests, ip, 10d])!;
    }
}
