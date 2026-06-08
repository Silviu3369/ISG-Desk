using NetScopeDiagnosticCenter.Core;
using NetScopeDiagnosticCenter.Core.Models;

namespace NetScopeDiagnosticCenter.Tests.Core;

public class HealthScoreCalculatorTests
{
    private static NetworkDiagnosisResult OkResult() => new()
    {
        IpConfiguration = new IpConfigurationInfo
        {
            IpAddress = "192.168.1.10",
            Gateway = "192.168.1.1",
            DnsServers = ["192.168.1.1"]
        },
        Adapter = new AdapterInfo { ConnectionType = "Ethernet", LinkSpeedMbps = 1000 },
        Tests = new ConnectivityTests
        {
            Gateway = new TestResult { Status = "OK" },
            DnsResolution = new TestResult { Status = "OK" },
            InternetDnsResolution = new TestResult { Status = "OK" },
            InternetPing = new TestResult { Status = "OK" },
            ExternalTcp443 = new TestResult { Status = "OK" },
            HttpsGet = new TestResult { Status = "OK" },
            GatewayPacketLoss = new PacketLossResult { LossPercent = 0 },
            InternetPacketLoss = new PacketLossResult { LossPercent = 0 }
        }
    };

    [Fact]
    public void Calculate_ConnectivityIncomplete_ScoreCappedNotHealthy()
    {
        var result = OkResult();
        result.Tests.CollectorStatus = "DNS/internet collector timed out.";
        // All tests still "OK" in the helper, but the collector didn't complete →
        // must not be a green 100/Healthy.
        var score = new HealthScoreCalculator().Calculate(result);

        score.Score.Should().BeLessThanOrEqualTo(55);
        score.Status.Should().NotBe("Healthy");
    }

    [Fact]
    public void Calculate_PerfectResult_ReturnsScoreHundred()
    {
        var result = OkResult();
        var score = new HealthScoreCalculator().Calculate(result);

        score.Score.Should().Be(100);
        score.Status.Should().Be("Healthy");
        score.Penalties.Should().BeEmpty();
    }

    [Fact]
    public void Calculate_NoValidIp_HeavyPenalty()
    {
        var result = OkResult();
        result.IpConfiguration.IpAddress = "Unknown";

        var score = new HealthScoreCalculator().Calculate(result);
        score.Score.Should().BeLessThan(60);
        score.Penalties.Should().Contain(p => p.Contains("No valid IPv4"));
    }

    [Fact]
    public void Calculate_NoGateway_AppliesPenalty()
    {
        var result = OkResult();
        result.IpConfiguration.Gateway = "Unknown";

        var score = new HealthScoreCalculator().Calculate(result);
        score.Penalties.Should().Contain(p => p.Contains("No default gateway"));
    }

    [Fact]
    public void Calculate_GatewayCritical_AppliesPenalty()
    {
        var result = OkResult();
        result.Tests.Gateway = new TestResult { Status = "Critical" };

        var score = new HealthScoreCalculator().Calculate(result);
        score.Penalties.Should().Contain(p => p.Contains("Gateway unreachable"));
    }

    [Fact]
    public void Calculate_WeakWifi_AppliesCriticalPenalty()
    {
        var result = OkResult();
        result.Wifi = new WifiInfo { SignalPercent = 25 };

        var score = new HealthScoreCalculator().Calculate(result);
        score.Penalties.Should().Contain(p => p.Contains("Wi-Fi signal is critical"));
    }

    [Fact]
    public void Calculate_ModerateWifi_AppliesWarningPenalty()
    {
        var result = OkResult();
        result.Wifi = new WifiInfo { SignalPercent = 50 };

        var score = new HealthScoreCalculator().Calculate(result);
        score.Penalties.Should().Contain(p => p.Contains("Wi-Fi signal is moderate"));
    }

    [Fact]
    public void Calculate_StrongWifi_NoWifiPenalty()
    {
        var result = OkResult();
        result.Wifi = new WifiInfo { SignalPercent = 85 };

        var score = new HealthScoreCalculator().Calculate(result);
        score.Penalties.Should().NotContain(p => p.Contains("Wi-Fi signal"));
    }

    [Fact]
    public void Calculate_HighPacketLoss_AppliesPenalty()
    {
        var result = OkResult();
        result.Tests.GatewayPacketLoss = new PacketLossResult { LossPercent = 10 };

        var score = new HealthScoreCalculator().Calculate(result);
        score.Penalties.Should().Contain(p => p.Contains("Packet loss is above 5%"));
    }

    [Fact]
    public void Calculate_SlowEthernet_AppliesPenalty()
    {
        var result = OkResult();
        result.Adapter.LinkSpeedMbps = 100;
        result.Profile.ExpectedMinimumEthernetMbps = 1000;

        var score = new HealthScoreCalculator().Calculate(result);
        score.Penalties.Should().Contain(p => p.Contains("Ethernet link speed is below"));
    }

    [Fact]
    public void Calculate_AllBroken_ScoreClampedAtZero()
    {
        var result = new NetworkDiagnosisResult
        {
            IpConfiguration = new IpConfigurationInfo { IpAddress = "Unknown", Gateway = "Unknown" },
            Tests = new ConnectivityTests
            {
                Gateway = new TestResult { Status = "Critical" },
                DnsResolution = new TestResult { Status = "Critical" },
                InternetPing = new TestResult { Status = "Critical" },
                ExternalTcp443 = new TestResult { Status = "Critical" },
                HttpsGet = new TestResult { Status = "Critical" },
                GatewayPacketLoss = new PacketLossResult { LossPercent = 100 }
            }
        };

        var score = new HealthScoreCalculator().Calculate(result);
        score.Score.Should().Be(0);
        score.Status.Should().Be("Critical");
    }

    [Fact]
    public void Calculate_StatusBucketsMatchScoreBoundaries()
    {
        // Verify that for any computed score, the status is consistent with documented boundaries:
        // >= 90 Healthy, >= 70 Good, >= 40 Warning, else Critical
        var ok = new HealthScoreCalculator().Calculate(OkResult());
        ok.Status.Should().Be("Healthy");

        var slowEth = OkResult();
        slowEth.Adapter.LinkSpeedMbps = 100;
        slowEth.Profile.ExpectedMinimumEthernetMbps = 1000;
        var s2 = new HealthScoreCalculator().Calculate(slowEth);
        if (s2.Score >= 90) s2.Status.Should().Be("Healthy");
        else if (s2.Score >= 70) s2.Status.Should().Be("Good");
        else if (s2.Score >= 40) s2.Status.Should().Be("Warning");
        else s2.Status.Should().Be("Critical");

        var manyIssues = OkResult();
        manyIssues.IpConfiguration.IpAddress = "Unknown";
        manyIssues.Tests.Gateway = new TestResult { Status = "Critical" };
        var s3 = new HealthScoreCalculator().Calculate(manyIssues);
        if (s3.Score >= 90) s3.Status.Should().Be("Healthy");
        else if (s3.Score >= 70) s3.Status.Should().Be("Good");
        else if (s3.Score >= 40) s3.Status.Should().Be("Warning");
        else s3.Status.Should().Be("Critical");
    }
}
