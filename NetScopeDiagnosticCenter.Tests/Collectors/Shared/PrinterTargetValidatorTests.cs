using NetScopeDiagnosticCenter.Collectors.Shared;

namespace NetScopeDiagnosticCenter.Tests.Collectors.Shared;

public sealed class PrinterTargetValidatorTests
{
    [Theory]
    [InlineData("PRINT01", "PRINT01")]
    [InlineData(@"\\PRINT01", "PRINT01")]
    [InlineData("192.168.1.25", "192.168.1.25")]
    public void TryNormalizePrintServer_ValidLanTargets_ReturnsNormalizedValue(string value, string expected)
    {
        PrinterTargetValidator.TryNormalizePrintServer(value, out var normalized, out var reason)
            .Should().BeTrue();

        normalized.Should().Be(expected);
        reason.Should().BeEmpty();
    }

    [Theory]
    [InlineData("print.example.com")]
    [InlineData("8.8.8.8")]
    [InlineData(@"\\PRINT01\Queue")]
    [InlineData("PRINT 01")]
    public void TryNormalizePrintServer_InvalidTargets_ReturnsReason(string value)
    {
        PrinterTargetValidator.TryNormalizePrintServer(value, out _, out var reason)
            .Should().BeFalse();

        reason.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void TryNormalizeSharedQueueConnection_ValidConnection_ReturnsNormalizedValue()
    {
        PrinterTargetValidator.TryNormalizeSharedQueueConnection(
                @"\\PRINT01\HP LaserJet",
                out var normalized,
                out var reason)
            .Should().BeTrue();

        normalized.Should().Be(@"\\PRINT01\HP LaserJet");
        reason.Should().BeEmpty();
    }

    [Theory]
    [InlineData("PRINT01")]
    [InlineData(@"\\print.example.com\HP")]
    [InlineData(@"\\8.8.8.8\HP")]
    [InlineData(@"\\PRINT01\")]
    [InlineData(@"\\PRINT01\HP\Extra")]
    public void TryNormalizeSharedQueueConnection_InvalidConnection_ReturnsReason(string value)
    {
        PrinterTargetValidator.TryNormalizeSharedQueueConnection(value, out _, out var reason)
            .Should().BeFalse();

        reason.Should().NotBeNullOrWhiteSpace();
    }
}
