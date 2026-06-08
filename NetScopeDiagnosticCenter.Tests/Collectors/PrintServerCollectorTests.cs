using NetScopeDiagnosticCenter.Collectors;
using NetScopeDiagnosticCenter.Infrastructure;

namespace NetScopeDiagnosticCenter.Tests.Collectors;

public sealed class PrintServerCollectorTests
{
    [Theory]
    [InlineData("print.example.com")]
    [InlineData("8.8.8.8")]
    [InlineData(@"\\PRINT01\Queue")]
    public async Task DiscoverFromPrintServerAsync_InvalidTarget_ReturnsBeforePowerShell(string target)
    {
        var collector = new PrintServerCollector(new PowerShellRunner(new NullLogger()));

        var result = await collector.DiscoverFromPrintServerAsync(target);

        result.Severity.Should().Be("Critical");
        result.Verdict.Should().NotBeNullOrWhiteSpace();
        result.Evidence.Should().ContainSingle();
    }

    private sealed class NullLogger : ILoggingService
    {
        public void Debug(string message) { }
        public void Info(string message) { }
        public void Warning(string message) { }
        public void Error(string message, Exception? exception = null) { }
    }
}
