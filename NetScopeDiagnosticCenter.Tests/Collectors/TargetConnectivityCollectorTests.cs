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

    [Fact]
    public async Task ProbeTargetAsync_FileServerWithSmbOpen_ListsPublishedShares()
    {
        // localhost always answers TCP 445 when LanmanServer runs; the fake seam below
        // keeps the share list deterministic regardless of the machine's real shares.
        var collector = new FakeEnumerationCollector(new PowerShellRunner(new NullLogger()))
        {
            EnumerationResult = new SmbShareEnumerationResult
            {
                Status = "OK",
                Shares =
                [
                    new SmbShareInfo { Name = "Public", IsDiskShare = true },
                    new SmbShareInfo { Name = "C$", IsDiskShare = true, IsHidden = true }
                ]
            }
        };
        var target = new DiagnosticTarget
        {
            Name = "localhost",
            Host = "localhost",
            Purpose = "File server",
            Ports = [445]
        };

        var result = await collector.ProbeTargetAsync(target, [445]);

        result.ShareEnumStatus.Should().Be("OK");
        result.VisibleShares.Should().ContainInOrder("Public", "C$ (hidden)");
        result.VisibleSharesText.Should().Be("Public, C$ (hidden)");
        result.ShareEnumSummary.Should().Contain("2 share(s) published");
    }

    [Fact]
    public async Task ProbeTargetAsync_NonFileServerPurpose_SkipsShareEnumeration()
    {
        var collector = new FakeEnumerationCollector(new PowerShellRunner(new NullLogger()));
        var target = new DiagnosticTarget
        {
            Name = "localhost",
            Host = "localhost",
            Purpose = "Service target",
            Ports = [445]
        };

        var result = await collector.ProbeTargetAsync(target, [445]);

        collector.EnumerationRequested.Should().BeFalse("share lists are only relevant for file-server targets");
        result.ShareEnumStatus.Should().Be("Not run");
        result.VisibleSharesText.Should().Be("—");
    }

    [Fact]
    public async Task EnumerateShares_AgainstLocalhost_CompletesWithoutThrowing()
    {
        // Real netapi32 integration: status must be a known value and never throw.
        var result = await SmbShareEnumerator.EnumerateAsync("localhost", TimeSpan.FromSeconds(8));

        result.Status.Should().BeOneOf("OK", "AccessDenied", "Unavailable");
        if (result.Status == "OK")
        {
            result.Shares.Should().OnlyContain(share => !string.IsNullOrWhiteSpace(share.Name));
            result.Shares.Should().OnlyContain(share => !share.Name.Equals("IPC$", StringComparison.OrdinalIgnoreCase));
        }
    }

    private class FakeEnumerationCollector : TargetConnectivityCollector
    {
        public SmbShareEnumerationResult EnumerationResult { get; set; } = SmbShareEnumerationResult.Unavailable("not configured");
        public bool EnumerationRequested { get; private set; }

        public FakeEnumerationCollector(PowerShellRunner runner) : base(runner) { }

        protected override Task<SmbShareEnumerationResult> EnumerateSharesAsync(string host, CancellationToken cancellationToken)
        {
            EnumerationRequested = true;
            return Task.FromResult(EnumerationResult);
        }
    }

    private sealed class NullLogger : ILoggingService
    {
        public void Debug(string message) { }
        public void Info(string message) { }
        public void Warning(string message) { }
        public void Error(string message, Exception? exception = null) { }
    }
}
