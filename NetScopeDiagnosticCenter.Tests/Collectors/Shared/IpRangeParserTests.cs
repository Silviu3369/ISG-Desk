using System.Net;
using NetScopeDiagnosticCenter.Collectors.Shared;

namespace NetScopeDiagnosticCenter.Tests.Collectors.Shared;

public class IpRangeParserTests
{
    private const int DefaultMaxHosts = 254;

    // ==== Single IP ====

    [Fact]
    public void TryResolveHosts_SinglePrivateIp_ReturnsOneHost()
    {
        var result = IpRangeParser.TryResolveHosts("192.168.1.10", DefaultMaxHosts);

        result.Error.Should().BeEmpty();
        result.Hosts.Should().HaveCount(1);
        result.Hosts[0].ToString().Should().Be("192.168.1.10");
        result.EstimatedHosts.Should().Be(1);
    }

    [Fact]
    public void TryResolveHosts_SinglePublicIp_ReturnsError()
    {
        var result = IpRangeParser.TryResolveHosts("8.8.8.8", DefaultMaxHosts);

        result.Error.Should().Contain("Public IP scanning is not allowed");
        result.Hosts.Should().BeEmpty();
    }

    [Theory]
    [InlineData("10.0.0.1")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.254")]
    [InlineData("192.168.0.1")]
    public void TryResolveHosts_AllPrivateRanges_AreAccepted(string ip)
    {
        var result = IpRangeParser.TryResolveHosts(ip, DefaultMaxHosts);

        result.Error.Should().BeEmpty();
        result.Hosts.Should().HaveCount(1);
    }

    // ==== CIDR ====

    [Fact]
    public void TryResolveHosts_Cidr24_Returns254Hosts()
    {
        var result = IpRangeParser.TryResolveHosts("192.168.1.0/24", DefaultMaxHosts);

        result.Error.Should().BeEmpty();
        result.Hosts.Should().HaveCount(254);
        result.Hosts.First().ToString().Should().Be("192.168.1.1");
        result.Hosts.Last().ToString().Should().Be("192.168.1.254");
    }

    [Fact]
    public void TryResolveHosts_Cidr32_ReturnsOneHost()
    {
        var result = IpRangeParser.TryResolveHosts("10.0.0.5/32", DefaultMaxHosts);

        result.Error.Should().BeEmpty();
        result.Hosts.Should().HaveCount(1);
        result.Hosts[0].ToString().Should().Be("10.0.0.5");
    }

    [Fact]
    public void TryResolveHosts_Cidr16_TooLarge_ReturnsError()
    {
        var result = IpRangeParser.TryResolveHosts("10.0.0.0/16", DefaultMaxHosts);

        result.Error.Should().Contain("safety limit");
        result.Hosts.Should().BeEmpty();
        result.EstimatedHosts.Should().BeGreaterThan(DefaultMaxHosts);
    }

    [Fact]
    public void TryResolveHosts_PublicCidr_ReturnsError()
    {
        var result = IpRangeParser.TryResolveHosts("8.8.8.0/24", DefaultMaxHosts);

        result.Error.Should().Contain("Public IP scanning is not allowed");
        result.Hosts.Should().BeEmpty();
    }

    [Fact]
    public void TryResolveHosts_InvalidCidr_ReturnsError()
    {
        var result = IpRangeParser.TryResolveHosts("not-a-cidr/24", DefaultMaxHosts);

        result.Error.Should().Contain("Invalid CIDR");
    }

    [Fact]
    public void TryResolveHosts_CidrPrefixOutOfRange_ReturnsError()
    {
        var result = IpRangeParser.TryResolveHosts("192.168.1.0/33", DefaultMaxHosts);

        result.Error.Should().Contain("Invalid CIDR");
    }

    // ==== Explicit range ====

    [Fact]
    public void TryResolveHosts_ExplicitRange_ReturnsCorrectHosts()
    {
        var result = IpRangeParser.TryResolveHosts("192.168.1.10-192.168.1.15", DefaultMaxHosts);

        result.Error.Should().BeEmpty();
        result.Hosts.Should().HaveCount(6);
        result.Hosts.First().ToString().Should().Be("192.168.1.10");
        result.Hosts.Last().ToString().Should().Be("192.168.1.15");
    }

    [Fact]
    public void TryResolveHosts_ExplicitRangeReversed_ReturnsError()
    {
        var result = IpRangeParser.TryResolveHosts("192.168.1.20-192.168.1.10", DefaultMaxHosts);

        result.Error.Should().Contain("end must be greater than or equal to the start");
    }

    [Fact]
    public void TryResolveHosts_ExplicitRangeTooLarge_ReturnsError()
    {
        var result = IpRangeParser.TryResolveHosts("10.0.0.1-10.0.5.1", DefaultMaxHosts);

        result.Error.Should().Contain("maximum allowed");
    }

    [Fact]
    public void TryResolveHosts_ExplicitRangePublic_ReturnsError()
    {
        var result = IpRangeParser.TryResolveHosts("8.8.8.1-8.8.8.10", DefaultMaxHosts);

        result.Error.Should().Contain("Public IP scanning is not allowed");
    }

    [Fact]
    public void TryResolveHosts_InvalidExplicitRange_ReturnsError()
    {
        var result = IpRangeParser.TryResolveHosts("foo-bar", DefaultMaxHosts);

        result.Error.Should().Contain("Invalid explicit IP range");
    }

    // ==== Garbage input ====

    [Fact]
    public void TryResolveHosts_RandomString_ReturnsError()
    {
        var result = IpRangeParser.TryResolveHosts("hello world", DefaultMaxHosts);

        result.Error.Should().NotBeEmpty();
        result.Hosts.Should().BeEmpty();
    }
}
