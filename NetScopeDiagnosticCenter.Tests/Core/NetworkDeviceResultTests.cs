using NetScopeDiagnosticCenter.Core.Models;

namespace NetScopeDiagnosticCenter.Tests.Core;

public sealed class NetworkDeviceResultTests
{
    [Fact]
    public void DisplayProperties_ReturnUnknownForEmptyReachabilityFields()
    {
        var result = new NetworkDeviceResult();

        result.TargetDisplay.Should().Be("Unknown");
        result.AddressDisplay.Should().Be("Unknown");
        result.ClassificationConfidenceDisplay.Should().Be("Low");
        result.ReverseDnsNameDisplay.Should().Be("Unknown");
        result.MacAddressDisplay.Should().Be("Unknown");
        result.MacVendorDisplay.Should().Be("Unknown");
        result.NeighborStateDisplay.Should().Be("Unknown");
        result.NeighborInterfaceDisplay.Should().Be("Unknown");
        result.PingReachableText.Should().Be("Unknown");
        result.IdentityNameDisplay.Should().Be("Unknown");
        result.IdentityDescriptionDisplay.Should().Be("Unknown");
    }

    [Fact]
    public void DisplayProperties_ReturnRealReachabilityValues()
    {
        var result = new NetworkDeviceResult
        {
            Target = "switch-01",
            Address = "10.0.0.10",
            ClassificationConfidence = "High",
            ReverseDnsName = "switch-01.contoso.local",
            MacAddress = "00-11-22-33-44-55",
            MacVendor = "Contoso",
            NeighborState = "Reachable",
            NeighborInterface = "Ethernet",
            PingReachable = true,
            Identity = new SnmpDeviceInfo
            {
                SysName = "core-switch",
                SysDescr = "Contoso switch"
            }
        };

        result.TargetDisplay.Should().Be("switch-01");
        result.AddressDisplay.Should().Be("10.0.0.10");
        result.ClassificationConfidenceDisplay.Should().Be("High");
        result.ReverseDnsNameDisplay.Should().Be("switch-01.contoso.local");
        result.MacAddressDisplay.Should().Be("00-11-22-33-44-55");
        result.MacVendorDisplay.Should().Be("Contoso");
        result.NeighborStateDisplay.Should().Be("Reachable");
        result.NeighborInterfaceDisplay.Should().Be("Ethernet");
        result.PingReachableText.Should().Be("Yes");
        result.IdentityNameDisplay.Should().Be("core-switch");
        result.IdentityDescriptionDisplay.Should().Be("Contoso switch");
    }

    [Fact]
    public void PingReachableText_ReturnsNoForKnownUnreachablePing()
    {
        var result = new NetworkDeviceResult
        {
            PingStatus = "Unreachable",
            PingReachable = false
        };

        result.PingReachableText.Should().Be("No");
    }
}
