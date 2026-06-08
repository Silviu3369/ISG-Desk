using NetScopeDiagnosticCenter.Core;

namespace NetScopeDiagnosticCenter.Tests.Core;

public sealed class DiagnosticTargetValidatorTests
{
    [Theory]
    [InlineData("192.168.1.1", "192.168.1.1")]
    [InlineData("2001:4860:4860::8888", "2001:4860:4860::8888")]
    [InlineData("smtp.office365.com", "smtp.office365.com")]
    [InlineData("server-01", "server-01")]
    [InlineData("  gateway.local  ", "gateway.local")]
    public void TryNormalizeHost_ValidTargets_ReturnsNormalizedValue(string value, string expected)
    {
        DiagnosticTargetValidator.TryNormalizeHost(value, out var normalized, out var reason)
            .Should().BeTrue();

        normalized.Should().Be(expected);
        reason.Should().BeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("host name")]
    [InlineData("https://example.com")]
    [InlineData("-server")]
    [InlineData("server-")]
    [InlineData("server..local")]
    [InlineData("host\r\nname")]
    public void TryNormalizeHost_InvalidTargets_ReturnsReason(string value)
    {
        DiagnosticTargetValidator.TryNormalizeHost(value, out var normalized, out var reason)
            .Should().BeFalse();

        normalized.Should().Be(value.Trim());
        reason.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void TryNormalizeHost_TooLongTarget_ReturnsReason()
    {
        var value = new string('a', 254);

        DiagnosticTargetValidator.TryNormalizeHost(value, out _, out var reason)
            .Should().BeFalse();

        reason.Should().Contain("253");
    }

    [Theory]
    [InlineData(@"\\fileserver\shared", "fileserver")]
    [InlineData(@"\\192.168.10.20\scan", "192.168.10.20")]
    public void TryNormalizeHostOrShare_UncShare_ReturnsServerName(string value, string expected)
    {
        DiagnosticTargetValidator.TryNormalizeHostOrShare(value, out var normalized, out var reason)
            .Should().BeTrue();

        normalized.Should().Be(expected);
        reason.Should().BeEmpty();
    }

    [Theory]
    [InlineData(@"\\fileserver\shared", "fileserver", @"\\fileserver\shared")]
    [InlineData(@"\\fileserver\Shared Files\Team A", "fileserver", @"\\fileserver\Shared Files\Team A")]
    public void TryNormalizeHostOrShare_UncShare_ReturnsSharePath(string value, string expectedHost, string expectedSharePath)
    {
        DiagnosticTargetValidator.TryNormalizeHostOrShare(value, out var normalizedHost, out var sharePath, out var reason)
            .Should().BeTrue();

        normalizedHost.Should().Be(expectedHost);
        sharePath.Should().Be(expectedSharePath);
        reason.Should().BeEmpty();
    }

    [Theory]
    [InlineData(@"\\fileserver\bad*share")]
    [InlineData(@"\\fileserver\share\\folder")]
    public void TryNormalizeHostOrShare_InvalidSharePath_ReturnsReason(string value)
    {
        DiagnosticTargetValidator.TryNormalizeHostOrShare(value, out _, out _, out var reason)
            .Should().BeFalse();

        reason.Should().Contain("Share path");
    }
}
