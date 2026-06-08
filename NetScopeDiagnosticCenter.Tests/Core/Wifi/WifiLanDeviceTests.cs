using NetScopeDiagnosticCenter.Core.Models.Wifi;

namespace NetScopeDiagnosticCenter.Tests.Core.Wifi;

public sealed class WifiLanDeviceTests
{
    [Fact]
    public void HostDisplay_WithResolvedName_ShowsDeviceName()
    {
        var device = new WifiLanDevice(
            IpAddress: "192.168.1.25",
            MacAddress: "AA:BB:CC:DD:EE:FF",
            HostName: "OFFICE-PRINTER",
            Vendor: "Example Vendor",
            IsGateway: false,
            IsThisPc: false);

        device.HostDisplay.Should().Be("OFFICE-PRINTER");
        device.HasResolvedName.Should().BeTrue();
        device.NameSourceDisplay.Should().Be("Resolved");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void HostDisplay_WithoutResolvedName_IsExplicit(string? hostName)
    {
        var device = new WifiLanDevice(
            IpAddress: "192.168.1.50",
            MacAddress: null,
            HostName: hostName,
            Vendor: null,
            IsGateway: false,
            IsThisPc: false);

        device.HostDisplay.Should().Be("No advertised name");
        device.HasResolvedName.Should().BeFalse();
        device.NameSourceDisplay.Should().Contain("No DNS");
    }

    [Fact]
    public void RoleLabel_ForGatewayAndAccessPoint_IsExplicit()
    {
        var device = new WifiLanDevice(
            IpAddress: "192.168.1.1",
            MacAddress: "AA:BB:CC:00:00:01",
            HostName: "Internet gateway",
            Vendor: "Example Vendor",
            IsGateway: true,
            IsThisPc: false,
            IsWifiAccessPoint: true,
            AccessPointBssid: "AA:BB:CC:00:00:01",
            RoleSource: "Default gateway MAC matches the connected Wi-Fi BSSID.");

        device.RoleLabel.Should().Be("Internet gateway / Wi-Fi AP");
        device.RoleSourceDisplay.Should().Contain("BSSID");
    }

    [Fact]
    public void RoleLabel_ForLikelyAccessPoint_StaysHonest()
    {
        var device = new WifiLanDevice(
            IpAddress: "192.168.1.1",
            MacAddress: "AA:BB:CC:00:00:02",
            HostName: "Internet gateway",
            Vendor: "Example Vendor",
            IsGateway: true,
            IsThisPc: false,
            IsLikelyWifiAccessPoint: true,
            AccessPointBssid: "AA:BB:CC:00:00:01",
            RoleSource: "Default gateway MAC and connected Wi-Fi BSSID share OUI AA:BB:CC.");

        device.RoleLabel.Should().Be("Internet gateway / likely AP");
        device.RoleSourceDisplay.Should().Contain("OUI");
    }

    [Fact]
    public void RoleLabel_ForGatewayOnly_DoesNotClaimAccessPoint()
    {
        var device = new WifiLanDevice(
            IpAddress: "192.168.1.1",
            MacAddress: "AA:BB:CC:00:00:02",
            HostName: "Internet gateway",
            Vendor: "Example Vendor",
            IsGateway: true,
            IsThisPc: false);

        device.RoleLabel.Should().Be("Internet gateway");
    }
}
