using System.Net;
using NetScopeDiagnosticCenter.Collectors.Shared;

namespace NetScopeDiagnosticCenter.Tests.Collectors.Shared;

public class NetworkSafetyPolicyTests
{
    [Theory]
    [InlineData("10.0.0.1", true)]
    [InlineData("10.255.255.255", true)]
    [InlineData("172.16.0.1", true)]
    [InlineData("172.31.255.254", true)]
    [InlineData("192.168.0.1", true)]
    [InlineData("192.168.255.255", true)]
    [InlineData("8.8.8.8", false)]
    [InlineData("1.1.1.1", false)]
    [InlineData("172.15.0.1", false)]
    [InlineData("172.32.0.1", false)]
    [InlineData("192.167.0.1", false)]
    [InlineData("169.254.0.1", false)] // APIPA — not "private" RFC1918
    public void IsPrivateIpv4_ReturnsExpected(string ipString, bool expected)
    {
        var address = IPAddress.Parse(ipString);
        NetworkSafetyPolicy.IsPrivateIpv4(address).Should().Be(expected);
    }

    [Theory]
    [InlineData("169.254.0.1", true)]
    [InlineData("169.254.255.255", true)]
    [InlineData("169.253.0.1", false)]
    [InlineData("192.168.0.1", false)]
    public void IsApipa_ReturnsExpected(string ipString, bool expected)
    {
        var address = IPAddress.Parse(ipString);
        NetworkSafetyPolicy.IsApipa(address).Should().Be(expected);
    }

    [Fact]
    public void ToUInt32_AndBack_RoundTrips()
    {
        var ip = IPAddress.Parse("192.168.1.100");
        var asUint = NetworkSafetyPolicy.ToUInt32(ip);
        var back = NetworkSafetyPolicy.FromUInt32(asUint);
        back.ToString().Should().Be("192.168.1.100");
    }

    [Theory]
    [InlineData(0, 0u)]
    [InlineData(8, 0xFF000000u)]
    [InlineData(16, 0xFFFF0000u)]
    [InlineData(24, 0xFFFFFF00u)]
    [InlineData(32, 0xFFFFFFFFu)]
    public void Mask_ReturnsExpected(int prefix, uint expected)
    {
        NetworkSafetyPolicy.Mask(prefix).Should().Be(expected);
    }

    [Theory]
    [InlineData(32, 1)]
    [InlineData(31, 2)]
    [InlineData(30, 2)]
    [InlineData(24, 254)]
    [InlineData(16, 65534)]
    public void EstimateUsableHosts_ReturnsExpected(int prefix, int expected)
    {
        NetworkSafetyPolicy.EstimateUsableHosts(prefix).Should().Be(expected);
    }

    [Fact]
    public void EnsurePrivate_PrivateAddress_DoesNotThrow()
    {
        var ip = IPAddress.Parse("10.0.0.1");
        var act = () => NetworkSafetyPolicy.EnsurePrivate(ip);
        act.Should().NotThrow();
    }

    [Fact]
    public void EnsurePrivate_PublicAddress_Throws()
    {
        var ip = IPAddress.Parse("8.8.8.8");
        var act = () => NetworkSafetyPolicy.EnsurePrivate(ip);
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Public IP scanning is not allowed*");
    }

    [Theory]
    [InlineData("192.168.1.1", true)]
    [InlineData("8.8.8.8", false)]
    [InlineData("169.254.1.1", false)] // APIPA excluded
    public void IsScannable_ReturnsExpected(string ip, bool expected)
    {
        NetworkSafetyPolicy.IsScannable(IPAddress.Parse(ip)).Should().Be(expected);
    }
}
