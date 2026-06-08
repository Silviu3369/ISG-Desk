using NetScopeDiagnosticCenter.Collectors;
using NetScopeDiagnosticCenter.Infrastructure;

namespace NetScopeDiagnosticCenter.Tests.Collectors;

public sealed class PortTestCollectorTests
{
    [Fact]
    public async Task TestPortAsync_InvalidTarget_ReturnsFailureBeforePowerShell()
    {
        var collector = new PortTestCollector(new PowerShellRunner(new NullLogger()));

        var result = await collector.TestPortAsync("https://example.com/path", 443);

        result.Severity.Should().Be("Critical");
        result.Port.Should().Be(443);
        result.Verdict.Should().Contain("host name or IP address");
    }

    [Fact]
    public async Task TestPortAsync_InvalidPort_ReturnsFailureBeforePowerShell()
    {
        var collector = new PortTestCollector(new PowerShellRunner(new NullLogger()));

        var result = await collector.TestPortAsync("example.com", 70000);

        result.Severity.Should().Be("Critical");
        result.Port.Should().Be(70000);
        result.Verdict.Should().Contain("1-65535");
    }

    private sealed class NullLogger : ILoggingService
    {
        public void Debug(string message) { }
        public void Info(string message) { }
        public void Warning(string message) { }
        public void Error(string message, Exception? exception = null) { }
    }
}
