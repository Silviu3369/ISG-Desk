using NetScopeDiagnosticCenter.Collectors;
using NetScopeDiagnosticCenter.Infrastructure;

namespace NetScopeDiagnosticCenter.Tests.Collectors;

public class NetworkRepairServiceTests
{
    private sealed class NullLogger : ILoggingService
    {
        public void Debug(string message) { }
        public void Info(string message) { }
        public void Warning(string message) { }
        public void Error(string message, Exception? exception = null) { }
    }

    [Fact]
    public async Task FlushDnsAsync_AgainstRealPowerShell_Succeeds()
    {
        // Integration test on purpose: flushing the local DNS cache is harmless and this
        // exercises the actual script (a PS syntax error here once shipped past the
        // unit tests because they fake the service).
        var service = new NetworkRepairService(new PowerShellRunner(new NullLogger()));

        var result = await service.FlushDnsAsync(CancellationToken.None);

        result.Success.Should().BeTrue(result.Summary);
        result.Summary.Should().Contain("DNS client cache cleared");
        result.Severity.Should().Be("OK");
    }

    [Fact]
    public async Task RestartAdapterAsync_WithoutAdapterName_FailsFastWithoutPowerShell()
    {
        var service = new NetworkRepairService(new PowerShellRunner(new NullLogger()));

        var result = await service.RestartAdapterAsync(string.Empty, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Summary.Should().Contain("run Quick Diagnosis first");
    }
}
