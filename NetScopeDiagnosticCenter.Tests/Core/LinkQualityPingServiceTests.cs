using NetScopeDiagnosticCenter.Core;
using NetScopeDiagnosticCenter.Core.Models;

namespace NetScopeDiagnosticCenter.Tests.Core;

public sealed class LinkQualityPingServiceTests
{
    [Fact]
    public async Task RunAsync_InvalidTarget_ReturnsWarningWithoutSamples()
    {
        var service = new LinkQualityPingService();

        var result = await service.RunAsync(
            "https://example.com/path",
            10,
            LinkQualityPingCategory.Target,
            LinkQualityThresholds.Default);

        result.Status.Should().Be("Warning");
        result.Sent.Should().Be(0);
        result.Received.Should().Be(0);
        result.Details.Should().Contain("Ping test was not started");
    }
}
