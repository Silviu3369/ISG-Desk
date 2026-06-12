using NetScopeDiagnosticCenter.Core.Models;

namespace NetScopeDiagnosticCenter.Tests.Core;

public class PortProbeResultTests
{
    [Fact]
    public void StatusText_FailedPort_IgnoresHttpProbe()
    {
        var port = new PortProbeResult { Port = 443, TcpSucceeded = false, HttpStatusCode = 200 };

        port.StatusText.Should().Be("Failed");
    }

    [Fact]
    public void StatusText_OpenWithoutHttpProbe_StaysPlainOpen()
    {
        var port = new PortProbeResult { Port = 445, TcpSucceeded = true };

        port.StatusText.Should().Be("Open");
        port.HasHttpProbe.Should().BeFalse();
    }

    [Theory]
    [InlineData(200, "Open (HTTP 200)")]
    [InlineData(401, "Open (HTTP 401)")]
    [InlineData(503, "Open (HTTP 503)")]
    public void StatusText_OpenWebPort_IncludesHttpStatus(int statusCode, string expected)
    {
        var port = new PortProbeResult
        {
            Port = 443,
            TcpSucceeded = true,
            HttpStatusCode = statusCode,
            HttpDetails = $"HTTP {statusCode} description"
        };

        port.StatusText.Should().Be(expected);
        port.HasHttpProbe.Should().BeTrue();
    }

    [Fact]
    public void StatusText_TlsHandshakeFailure_ShowsOpenWithoutCode()
    {
        // TCP connected but the HTTP probe failed (e.g. TLS error) — no status code,
        // but the failure detail is preserved for evidence.
        var port = new PortProbeResult
        {
            Port = 8443,
            TcpSucceeded = true,
            HttpDetails = "HTTP probe failed: TLS handshake error"
        };

        port.StatusText.Should().Be("Open");
        port.HasHttpProbe.Should().BeTrue();
    }
}
