using System.Diagnostics;
using System.IO;
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

    [Fact]
    public async Task RunJsonScriptAsync_NonAsciiOutput_RoundTripsAsUtf8()
    {
        var runner = new PowerShellRunner(new NullLogger());

        // '·', '°' and Romanian diacritics get mangled ("ú", "ø") when PowerShell writes
        // stdout in the OEM codepage — the runner must force UTF-8 on both sides.
        var result = await runner.RunJsonScriptAsync(
            "Write-Output 'GPU·list 34 °C București șț'",
            TimeSpan.FromSeconds(15),
            CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Output.Should().Contain("GPU·list");
        result.Output.Should().Contain("34 °C");
        result.Output.Should().Contain("București șț");
    }

    [Fact]
    public async Task RunJsonScriptAsync_ScriptPastCommandLineLimit_RunsViaTempFile()
    {
        var runner = new PowerShellRunner(new NullLogger());
        // ~45 K chars of padding — encoded as base64/UTF-16 this would blow the ~32 K
        // CreateProcess command-line ceiling (Win32 error 206), forcing the temp-.ps1 path.
        var padding = string.Join(Environment.NewLine,
            Enumerable.Range(0, 600).Select(i => $"# pad {i} {new string('x', 60)}"));
        var script = padding + Environment.NewLine + "Write-Output '{\"Ok\":true}'";

        // An orphan from a killed run — the long-script path must sweep it. Fresh files may
        // belong to tests running in parallel, so only this old plant is asserted on.
        var stalePlant = Path.Combine(Path.GetTempPath(), "isgdesk-stale-test-plant.ps1");
        File.WriteAllText(stalePlant, "# stale orphan");
        File.SetLastWriteTimeUtc(stalePlant, DateTime.UtcNow.AddHours(-2));

        try
        {
            var result = await runner.RunJsonScriptAsync(script, TimeSpan.FromSeconds(30), CancellationToken.None);

            result.Success.Should().BeTrue();
            result.Output.Should().Contain("\"Ok\":true");
            File.Exists(stalePlant).Should().BeFalse("orphaned temp scripts older than an hour must be swept");
        }
        finally
        {
            if (File.Exists(stalePlant)) File.Delete(stalePlant);
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
