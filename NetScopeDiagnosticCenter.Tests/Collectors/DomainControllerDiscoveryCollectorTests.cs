using NetScopeDiagnosticCenter.Collectors;
using NetScopeDiagnosticCenter.Infrastructure;

namespace NetScopeDiagnosticCenter.Tests.Collectors;

public sealed class DomainControllerDiscoveryCollectorTests
{
    [Fact]
    public async Task DiscoverAsync_NoValidDomains_DoesNotRunDnsLookup()
    {
        var collector = new DomainControllerDiscoveryCollector(new PowerShellRunner(new NullLogger()));

        var result = await collector.DiscoverAsync(["", "https://contoso.local/path"]);

        result.Severity.Should().Be("Warning");
        result.SkippedReason.Should().Contain("No valid AD domain name");
        result.Candidates.Should().BeEmpty();
    }

    private sealed class NullLogger : ILoggingService
    {
        public void Debug(string message) { }
        public void Info(string message) { }
        public void Warning(string message) { }
        public void Error(string message, Exception? exception = null) { }
    }
}
