using NetScopeDiagnosticCenter.Core.Models;

namespace NetScopeDiagnosticCenter.Tests.Core;

public sealed class SnmpDeviceInfoTests
{
    [Fact]
    public void DisplayProperties_ReturnUnknownForEmptySnmpFields()
    {
        var info = new SnmpDeviceInfo();

        info.AddressDisplay.Should().Be("Unknown");
        info.SysNameDisplay.Should().Be("Unknown");
        info.SysDescrDisplay.Should().Be("Unknown");
        info.SysLocationDisplay.Should().Be("Unknown");
        info.SysContactDisplay.Should().Be("Unknown");
        info.SysUpTimeDisplay.Should().Be("Unknown");
        info.ProtocolDisplay.Should().Be("Unknown");
        info.PageCountText.Should().Be("Unknown");
    }

    [Fact]
    public void DisplayProperties_ReturnRealSnmpFields()
    {
        var info = new SnmpDeviceInfo
        {
            Address = "192.168.1.10",
            SysName = "switch-01",
            SysDescr = "Managed switch",
            SysLocation = "Closet A",
            SysContact = "network@example.com",
            SysUpTime = "12d 4h 3m",
            Protocol = SnmpProtocolVersion.V3AuthPriv,
            PageCount = 12345
        };

        info.AddressDisplay.Should().Be("192.168.1.10");
        info.SysNameDisplay.Should().Be("switch-01");
        info.SysDescrDisplay.Should().Be("Managed switch");
        info.SysLocationDisplay.Should().Be("Closet A");
        info.SysContactDisplay.Should().Be("network@example.com");
        info.SysUpTimeDisplay.Should().Be("12d 4h 3m");
        info.ProtocolDisplay.Should().Be(SnmpProtocolVersion.V3AuthPriv);
        info.PageCountText.Should().Be("12,345 pages");
    }
}
