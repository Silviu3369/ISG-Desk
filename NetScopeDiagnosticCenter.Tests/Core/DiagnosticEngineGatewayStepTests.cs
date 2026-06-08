using System.Reflection;
using NetScopeDiagnosticCenter.Core;
using NetScopeDiagnosticCenter.Core.Models;

namespace NetScopeDiagnosticCenter.Tests.Core;

public sealed class DiagnosticEngineGatewayStepTests
{
    [Fact]
    public void BuildGatewayStep_IncludesLiveGatewayTargetAndInterface()
    {
        var tests = new ConnectivityTests
        {
            Gateway = new TestResult
            {
                Target = "Gateway",
                Status = "OK",
                LatencyMs = 1.2,
                Details = "Ping 192.168.1.1: 2/2 replies."
            },
            GatewayPacketLoss = new PacketLossResult
            {
                Target = "192.168.1.1",
                Status = "OK",
                Sent = 4,
                Received = 4,
                LossPercent = 0
            }
        };
        var ip = new IpConfigurationInfo
        {
            InterfaceAlias = "Wi-Fi",
            Gateway = "192.168.1.1"
        };

        var step = BuildGatewayStep(tests, ip);

        step.Status.Should().Be("OK");
        step.Evidence.Should().Contain("Source: live gateway target from the current IP configuration.");
        step.Evidence.Should().Contain("Gateway target: 192.168.1.1; interface: Wi-Fi.");
        step.Recommendation.Should().Be("Gateway path is responding.");
    }

    [Fact]
    public void BuildGatewayStep_WhenGatewayUnavailable_DoesNotClaimGatewayResponds()
    {
        var tests = new ConnectivityTests
        {
            Gateway = TestResult.Unknown("Gateway"),
            GatewayPacketLoss = PacketLossResult.Unknown("Gateway")
        };
        var ip = new IpConfigurationInfo
        {
            InterfaceAlias = "Ethernet",
            Gateway = "Unknown"
        };

        var step = BuildGatewayStep(tests, ip);

        step.Status.Should().Be("Unknown");
        step.Recommendation.Should().Contain("could not complete");
        step.Recommendation.Should().NotContain("responding");
    }

    [Fact]
    public void BuildGatewayStep_WhenConnectivityCollectorFails_SurfacesCollectorError()
    {
        var tests = new ConnectivityTests
        {
            CollectorStatus = "DNS/internet collector timed out.",
            Gateway = TestResult.Unknown("Gateway"),
            GatewayPacketLoss = PacketLossResult.Unknown("Gateway")
        };
        var ip = new IpConfigurationInfo
        {
            InterfaceAlias = "Ethernet",
            Gateway = "192.168.1.1"
        };

        var step = BuildGatewayStep(tests, ip);

        step.Error.Should().Be("DNS/internet collector timed out.");
    }

    private static DiagnosisStepResult BuildGatewayStep(ConnectivityTests tests, IpConfigurationInfo ip)
    {
        var method = typeof(DiagnosticEngine).GetMethod(
            "BuildGatewayStep",
            BindingFlags.NonPublic | BindingFlags.Static);

        method.Should().NotBeNull();
        return (DiagnosisStepResult)method!.Invoke(null, [tests, ip, 10d])!;
    }
}
