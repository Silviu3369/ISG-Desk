using NetScopeDiagnosticCenter.Core;
using NetScopeDiagnosticCenter.Core.Models;

namespace NetScopeDiagnosticCenter.Tests.Core;

public class RuleEngineTests
{
    private static NetworkDiagnosisResult HealthyResult() => new()
    {
        Adapter = new AdapterInfo
        {
            ConnectionType = "Ethernet",
            Status = "Up",
            LinkSpeedMbps = 1000,
            CollectorStatus = "OK"
        },
        IpConfiguration = new IpConfigurationInfo
        {
            IpAddress = "192.168.1.10",
            Gateway = "192.168.1.1",
            DnsServers = ["192.168.1.1"]
        },
        Tests = new ConnectivityTests
        {
            Gateway = new TestResult { Status = "OK" },
            DnsResolution = new TestResult { Status = "OK" },
            InternetDnsResolution = new TestResult { Status = "OK" },
            InternetPing = new TestResult { Status = "OK" },
            ExternalTcp443 = new TestResult { Status = "OK" },
            HttpsGet = new TestResult { Status = "OK" },
            GatewayPacketLoss = new PacketLossResult { LossPercent = 0 }
        }
    };

    [Fact]
    public void Evaluate_EthernetHealthy_ReturnsOk()
    {
        var verdict = new RuleEngine().Evaluate(HealthyResult());
        verdict.Severity.Should().Be("OK");
    }

    [Fact]
    public void Evaluate_NoValidIp_FlagsAsCritical()
    {
        var result = HealthyResult();
        result.IpConfiguration.IpAddress = "Unknown";

        var verdict = new RuleEngine().Evaluate(result);
        verdict.Severity.Should().Be("Critical");
    }

    [Fact]
    public void Evaluate_ApipaAddress_FlagsAsCritical()
    {
        var result = HealthyResult();
        result.IpConfiguration.IpAddress = "169.254.10.10";

        var verdict = new RuleEngine().Evaluate(result);
        verdict.Severity.Should().Be("Critical");
    }

    [Fact]
    public void Evaluate_GatewayUnreachableAndPacketLoss_IsNotOk()
    {
        var result = HealthyResult();
        result.Tests.Gateway = new TestResult { Status = "Critical" };
        result.Tests.GatewayPacketLoss = new PacketLossResult { Status = "Critical", LossPercent = 100 };
        result.Tests.InternetPing = new TestResult { Status = "Critical" };

        var verdict = new RuleEngine().Evaluate(result);
        verdict.Severity.Should().BeOneOf("Critical", "Warning");
    }

    [Fact]
    public void Evaluate_DnsCritical_FlagsAsCritical()
    {
        var result = HealthyResult();
        result.Tests.DnsResolution = new TestResult { Status = "Critical" };
        result.Tests.InternetDnsResolution = new TestResult { Status = "Critical" };

        var verdict = new RuleEngine().Evaluate(result);
        verdict.Severity.Should().Be("Critical");
    }

    [Fact]
    public void Evaluate_WeakWifiSignal_FlagsAsCritical()
    {
        var result = HealthyResult();
        result.Adapter.ConnectionType = "Wi-Fi";
        result.Wifi = new WifiInfo { SignalPercent = 25 };

        var verdict = new RuleEngine().Evaluate(result);
        verdict.Severity.Should().Be("Critical");
        verdict.Title.Should().Contain("Wi-Fi");
    }

    [Fact]
    public void Evaluate_ModerateWifiSignal_FlagsAsWarning()
    {
        var result = HealthyResult();
        result.Adapter.ConnectionType = "Wi-Fi";
        result.Wifi = new WifiInfo { SignalPercent = 50 };
        result.Tests.GatewayPacketLoss = new PacketLossResult { LossPercent = 0 };

        var verdict = new RuleEngine().Evaluate(result);
        verdict.Severity.Should().Be("Warning");
    }

    [Fact]
    public void Evaluate_SlowEthernet_FlagsAsWarning()
    {
        var result = HealthyResult();
        result.Adapter.LinkSpeedMbps = 100;
        result.Profile.ExpectedMinimumEthernetMbps = 1000;

        var verdict = new RuleEngine().Evaluate(result);
        verdict.Severity.Should().BeOneOf("Warning", "Critical");
    }

    [Fact]
    public void Evaluate_NullWifiSignal_DoesNotFlagAsWifiIssue()
    {
        var result = HealthyResult();
        result.Adapter.ConnectionType = "Wi-Fi";
        result.Wifi = new WifiInfo { SignalPercent = null };

        var verdict = new RuleEngine().Evaluate(result);
        // Null signal means we don't have data — should not be flagged as critical/warning Wi-Fi issue
        verdict.Title.Should().NotContain("Weak Wi-Fi signal");
    }

    [Fact]
    public void Evaluate_StrongWifi_DoesNotFlagAsWifiIssue()
    {
        var result = HealthyResult();
        result.Adapter.ConnectionType = "Wi-Fi";
        result.Wifi = new WifiInfo { SignalPercent = 85 };

        var verdict = new RuleEngine().Evaluate(result);
        verdict.Severity.Should().Be("OK");
    }

    [Fact]
    public void Evaluate_CaptivePortal_ReportsPortalNotWanIssue()
    {
        var result = HealthyResult();
        result.Tests.CaptivePortal = new TestResult
        {
            Status = "Critical",
            Details = "Captive portal / interception likely (HTTP 200, unexpected body).",
        };

        var verdict = new RuleEngine().Evaluate(result);

        verdict.Title.Should().Contain("Captive portal");
        verdict.Severity.Should().Be("Warning");
    }

    [Fact]
    public void Evaluate_ConnectivityCollectorTimedOut_ReportsIncompleteNotOk()
    {
        // No concrete fault, but every connectivity test is Unknown because the collector
        // timed out. This must NOT read as a healthy "OK" network.
        var result = HealthyResult();
        result.Tests = new ConnectivityTests
        {
            CollectorStatus = "DNS/internet collector timed out.",
            Gateway = new TestResult { Status = "Unknown" },
            DnsResolution = new TestResult { Status = "Unknown" },
            InternetDnsResolution = new TestResult { Status = "Unknown" },
            InternetPing = new TestResult { Status = "Unknown" },
            ExternalTcp443 = new TestResult { Status = "Unknown" },
            HttpsGet = new TestResult { Status = "Unknown" },
        };

        var verdict = new RuleEngine().Evaluate(result);

        verdict.Severity.Should().NotBe("OK");
        verdict.Title.Should().Contain("incomplete");
    }
}
