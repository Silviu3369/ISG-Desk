using NetScopeDiagnosticCenter.Collectors;
using NetScopeDiagnosticCenter.Core;

namespace NetScopeDiagnosticCenter.Tests.Collectors;

public sealed class TargetShareDiscoveryCollectorTests
{
    [Fact]
    public async Task ScanRangeAsync_PublicIp_DoesNotScan()
    {
        var collector = new TargetShareDiscoveryCollector();

        var result = await collector.ScanRangeAsync("8.8.8.8");

        result.Severity.Should().Be("Warning");
        result.SkippedReason.Should().Contain("Public IP scanning is not allowed");
        result.ScannedHosts.Should().Be(0);
        result.Candidates.Should().BeEmpty();
    }

    [Fact]
    public async Task ScanRangeAsync_LargerThanSafetyLimit_DoesNotScan()
    {
        var collector = new TargetShareDiscoveryCollector();

        var result = await collector.ScanRangeAsync("10.0.0.0/23");

        result.Severity.Should().Be("Warning");
        result.SkippedReason.Should().Contain("larger than the v1 safety limit");
        result.MaxHosts.Should().Be(DiagnosticConstants.MaxSmbDiscoveryHosts);
        result.Candidates.Should().BeEmpty();
    }
}
