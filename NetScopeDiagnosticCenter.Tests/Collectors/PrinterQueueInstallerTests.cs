using NetScopeDiagnosticCenter.Collectors;
using NetScopeDiagnosticCenter.Infrastructure;

namespace NetScopeDiagnosticCenter.Tests.Collectors;

public sealed class PrinterQueueInstallerTests
{
    [Theory]
    [InlineData("PRINT01")]
    [InlineData(@"\\print.example.com\HP")]
    [InlineData(@"\\8.8.8.8\HP")]
    [InlineData(@"\\PRINT01\HP\Extra")]
    public async Task InstallSharedQueueAsync_InvalidConnection_ReturnsBeforePowerShell(string connection)
    {
        var installer = new PrinterQueueInstaller(new PowerShellRunner(new NullLogger()));

        var result = await installer.InstallSharedQueueAsync(connection);

        result.Success.Should().BeFalse();
        result.Category.Should().Be("InvalidInput");
        result.Error.Should().NotBeNullOrWhiteSpace();
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
