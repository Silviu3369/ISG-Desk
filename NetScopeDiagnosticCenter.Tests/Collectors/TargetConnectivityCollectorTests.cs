using NetScopeDiagnosticCenter.Collectors;
using NetScopeDiagnosticCenter.Core.Models;
using NetScopeDiagnosticCenter.Infrastructure;

namespace NetScopeDiagnosticCenter.Tests.Collectors;

public sealed class TargetConnectivityCollectorTests
{
    [Fact]
    public async Task ProbeTargetAsync_InvalidTarget_ReturnsCollectorWarningBeforePowerShell()
    {
        var collector = new TargetConnectivityCollector(new PowerShellRunner(new NullLogger()));
        var target = new DiagnosticTarget
        {
            Name = "Bad target",
            Host = "https://example.com/path",
            Purpose = "Service target",
            Ports = [443]
        };

        var result = await collector.ProbeTargetAsync(target, [443]);

        result.CollectorStatus.Should().Contain("host name or IP address");
        result.Details.Should().Contain("probe was not started");
        result.Ports.Should().ContainSingle(port => port.Port == 443 && !port.TcpSucceeded);
    }

    [Fact]
    public async Task ProbeTargetAsync_SharePathHostMismatch_ReturnsCollectorWarningBeforePowerShell()
    {
        var collector = new TargetConnectivityCollector(new PowerShellRunner(new NullLogger()));
        var target = new DiagnosticTarget
        {
            Name = @"\\server-b\share",
            Host = "server-a",
            SharePath = @"\\server-b\share",
            Purpose = "File server",
            Ports = [445]
        };

        var result = await collector.ProbeTargetAsync(target, [445]);

        result.CollectorStatus.Should().Contain("Share path host must match");
        result.ShareStatus.Should().Be("Unknown");
        result.Ports.Should().ContainSingle(port => port.Port == 445 && !port.TcpSucceeded);
    }

    private sealed class NullLogger : ILoggingService
    {
        public void Debug(string message) { }
        public void Info(string message) { }
        public void Warning(string message) { }
        public void Error(string message, Exception? exception = null) { }
    }
}
