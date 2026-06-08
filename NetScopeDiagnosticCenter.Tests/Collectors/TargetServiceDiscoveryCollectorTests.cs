using NetScopeDiagnosticCenter.Collectors;
using NetScopeDiagnosticCenter.Core;

namespace NetScopeDiagnosticCenter.Tests.Collectors;

public sealed class TargetServiceDiscoveryCollectorTests
{
    [Fact]
    public async Task ScanRangeAsync_PublicIp_DoesNotScan()
    {
        var collector = new TargetServiceDiscoveryCollector();

        var result = await collector.ScanRangeAsync("8.8.8.8", [443]);

        result.Severity.Should().Be("Warning");
        result.SkippedReason.Should().Contain("Public IP scanning is not allowed");
        result.ScannedHosts.Should().Be(0);
        result.Candidates.Should().BeEmpty();
    }

    [Fact]
    public async Task ScanRangeAsync_InvalidPorts_DoesNotScan()
    {
        var collector = new TargetServiceDiscoveryCollector();

        var result = await collector.ScanRangeAsync("192.168.1.10", [0, 70000]);

        result.Severity.Should().Be("Warning");
        result.SkippedReason.Should().Contain("No valid TCP ports");
        result.Candidates.Should().BeEmpty();
    }

    [Fact]
    public async Task ScanRangeAsync_LargerThanSafetyLimit_DoesNotScan()
    {
        var collector = new TargetServiceDiscoveryCollector();

        var result = await collector.ScanRangeAsync("10.0.0.0/23", [443]);

        result.Severity.Should().Be("Warning");
        result.SkippedReason.Should().Contain("larger than the v1 safety limit");
        result.MaxHosts.Should().Be(DiagnosticConstants.MaxServiceDiscoveryHosts);
        result.Candidates.Should().BeEmpty();
    }
}
