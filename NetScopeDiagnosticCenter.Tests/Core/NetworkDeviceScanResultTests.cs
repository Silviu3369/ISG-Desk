using NetScopeDiagnosticCenter.Core.Models;

namespace NetScopeDiagnosticCenter.Tests.Core;

public sealed class NetworkDeviceScanResultTests
{
    [Fact]
    public void DisplayProperties_ReturnDefaultsForEmptyFields()
    {
        var result = new NetworkDeviceScanResult();

        result.SourceDisplay.Should().Be("None");
        result.SnmpCommunityMaskedDisplay.Should().Be("Not used");
        result.AdapterNameDisplay.Should().Be("Unknown");
        result.SkippedReasonDisplay.Should().Be("None");
    }

    [Fact]
    public void DisplayProperties_ReturnRealValues()
    {
        var result = new NetworkDeviceScanResult
        {
            Source = "10.0.0.0/24",
            SnmpCommunityMasked = "********",
            AdapterName = "Ethernet",
            SkippedReason = "Manual skip"
        };

        result.SourceDisplay.Should().Be("10.0.0.0/24");
        result.SnmpCommunityMaskedDisplay.Should().Be("********");
        result.AdapterNameDisplay.Should().Be("Ethernet");
        result.SkippedReasonDisplay.Should().Be("Manual skip");
    }
}
