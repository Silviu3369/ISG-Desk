using System.Diagnostics;
using NetScopeDiagnosticCenter.Infrastructure;

namespace NetScopeDiagnosticCenter.Tests.Infrastructure;

public sealed class PowerShellRunnerTests
{
    [Fact]
    public async Task RunJsonScriptAsync_PreCancelledToken_ThrowsOperationCanceled()
    {
        var runner = new PowerShellRunner(new NullLogger());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await FluentActions.Invoking(() => runner.RunJsonScriptAsync(
                "Write-Output '{}'",
                TimeSpan.FromSeconds(5),
                cancellation.Token))
            .Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task RunJsonScriptAsync_CancelledRunningScript_ThrowsQuickly()
    {
        var runner = new PowerShellRunner(new NullLogger());
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        var stopwatch = Stopwatch.StartNew();

        await FluentActions.Invoking(() => runner.RunJsonScriptAsync(
                "Start-Sleep -Seconds 5; Write-Output '{}'",
                TimeSpan.FromSeconds(10),
                cancellation.Token))
            .Should().ThrowAsync<OperationCanceledException>();

        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(3));
    }

    private sealed class NullLogger : ILoggingService
    {
        public void Debug(string message) { }
        public void Info(string message) { }
        public void Warning(string message) { }
        public void Error(string message, Exception? exception = null) { }
    }
}
