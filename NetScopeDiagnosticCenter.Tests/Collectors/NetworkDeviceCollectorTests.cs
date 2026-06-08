using NetScopeDiagnosticCenter.Collectors;
using NetScopeDiagnosticCenter.Core.Models;
using NetScopeDiagnosticCenter.Infrastructure;

namespace NetScopeDiagnosticCenter.Tests.Collectors;

public sealed class NetworkDeviceCollectorTests
{
    [Fact]
    public async Task IdentifyAsync_InvalidTarget_ReturnsValidationResultBeforeNetwork()
    {
        var collector = new NetworkDeviceCollector(new PowerShellRunner(new NullLogger()), new SnmpClientService());

        var result = await collector.IdentifyAsync(new SnmpSessionOptions
        {
            Target = "https://example.com/device",
            Community = "private-community"
        });

        result.Verdict.Should().Be("Network device target is invalid.");
        result.Severity.Should().Be("Warning");
        result.SnmpStatus.Should().Be("Not tested");
        result.DnsStatus.Should().Be("Invalid target");
        result.Evidence.Should().ContainSingle(item => item.Contains("host name or IP address"));
        result.Recommendations.Should().ContainSingle(item => item.Contains("without protocol"));
        result.Recommendations.Should().NotContain(item => item.Contains("not reachable", StringComparison.OrdinalIgnoreCase));
        result.UnknownFindings.Should().ContainSingle(item => item.Contains("target input format is invalid"));
        result.AffectedLayer.Should().Be("Target input");
        result.OwnerSuggestion.Should().Be("Technician");
        result.Confidence.Should().Be("High");
    }

    [Fact]
    public async Task IdentifyAsync_PreCancelledToken_ThrowsOperationCanceled()
    {
        var collector = new NetworkDeviceCollector(new PowerShellRunner(new NullLogger()), new SnmpClientService());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await FluentActions.Invoking(() => collector.IdentifyAsync(
                new SnmpSessionOptions { Target = "192.168.1.1" },
                cancellation.Token))
            .Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task IdentifyAsync_PublicIp_ReturnsSafetyResultBeforeReachability()
    {
        var collector = new NetworkDeviceCollector(new PowerShellRunner(new NullLogger()), new SnmpClientService());

        var result = await collector.IdentifyAsync(new SnmpSessionOptions
        {
            Target = "8.8.8.8",
            Community = "public"
        });

        result.Verdict.Should().Be("Target rejected by safety policy.");
        result.Severity.Should().Be("Critical");
        result.SnmpStatus.Should().Be("Not tested");
        result.DnsStatus.Should().Be("Rejected");
        result.Ports.Should().BeEmpty();
        result.PingReachable.Should().BeFalse();
        result.Evidence.Should().ContainSingle(item => item.Contains("authorized private IPv4"));
        result.Limitations.Should().ContainSingle(item => item.Contains("RFC-1918"));
        result.Recommendations.Should().ContainSingle(item => item.Contains("RFC-1918"));
        result.Recommendations.Should().NotContain(item => item.Contains("not reachable", StringComparison.OrdinalIgnoreCase));
        result.ProbableFindings.Should().BeEmpty();
        result.UnknownFindings.Should().ContainSingle(item => item.Contains("outside the authorized private IPv4 scope"));
        result.AffectedLayer.Should().Be("Target validation");
        result.OwnerSuggestion.Should().Be("Technician");
        result.Confidence.Should().Be("High");
    }

    [Fact]
    public async Task IdentifyAsync_SnmpV3MissingCredentials_DoesNotReportDeviceOffline()
    {
        var collector = new NetworkDeviceCollector(new PowerShellRunner(new NullLogger()), new SnmpClientService());

        var result = await collector.IdentifyAsync(new SnmpSessionOptions
        {
            Target = "192.168.1.1",
            Protocol = SnmpProtocolVersion.V3AuthPriv,
            UserName = "monitor",
            AuthPassword = "auth-secret",
            PrivacyPassword = ""
        });

        result.Verdict.Should().Be("SNMPv3 credentials are incomplete.");
        result.Severity.Should().Be("Warning");
        result.SnmpStatus.Should().Be("Credentials missing");
        result.Ports.Should().BeEmpty();
        result.Recommendations.Should().ContainSingle(item => item.Contains("SNMPv3 authPriv"));
        result.Recommendations.Should().NotContain(item => item.Contains("not reachable", StringComparison.OrdinalIgnoreCase));
        result.ProbableFindings.Should().ContainSingle(item => item.Contains("credential input is incomplete"));
        result.UnknownFindings.Should().BeEmpty();
        result.AffectedLayer.Should().Be("SNMP credentials");
        result.Confidence.Should().Be("High");
    }

    private sealed class NullLogger : ILoggingService
    {
        public void Debug(string message) { }
        public void Info(string message) { }
        public void Warning(string message) { }
        public void Error(string message, Exception? exception = null) { }
    }
}
