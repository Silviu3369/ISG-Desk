using NetScopeDiagnosticCenter.Core.Models;

namespace NetScopeDiagnosticCenter.Tests.Core;

public sealed class NetworkDeviceInterfaceInfoTests
{
    [Fact]
    public void DisplayProperties_ReturnUnknownForEmptyInterfaceFields()
    {
        var info = new NetworkDeviceInterfaceInfo
        {
            Name = "",
            Description = "",
            AdminStatus = "",
            OperStatus = "",
            SpeedText = "",
            TrafficText = "",
            TrafficRateText = "",
            AttentionReason = "",
            RecommendedNextCheck = "",
            Verdict = "",
            Severity = ""
        };

        info.NameDisplay.Should().Be("Unknown");
        info.DescriptionDisplay.Should().Be("Unknown");
        info.AdminStatusDisplay.Should().Be("Unknown");
        info.OperStatusDisplay.Should().Be("Unknown");
        info.SpeedTextDisplay.Should().Be("Unknown");
        info.TrafficTextDisplay.Should().Be("Unknown");
        info.TrafficRateTextDisplay.Should().Be("Unknown");
        info.AttentionReasonDisplay.Should().Be("Unknown");
        info.RecommendedNextCheckDisplay.Should().Be("Unknown");
        info.VerdictDisplay.Should().Be("Unknown");
        info.SeverityDisplay.Should().Be("Unknown");
    }

    [Fact]
    public void DisplayProperties_ReturnRealInterfaceFields()
    {
        var info = new NetworkDeviceInterfaceInfo
        {
            Name = "Gi1/0/1",
            Description = "Uplink",
            AdminStatus = "Up",
            OperStatus = "Up",
            SpeedText = "1,000 Mbps",
            TrafficText = "In 1 MB / Out 2 MB",
            TrafficRateText = "20 KB/s",
            AttentionReason = "Port down",
            RecommendedNextCheck = "Check cable.",
            Verdict = "Warning",
            Severity = "Warning"
        };

        info.NameDisplay.Should().Be("Gi1/0/1");
        info.DescriptionDisplay.Should().Be("Uplink");
        info.AdminStatusDisplay.Should().Be("Up");
        info.OperStatusDisplay.Should().Be("Up");
        info.SpeedTextDisplay.Should().Be("1,000 Mbps");
        info.TrafficTextDisplay.Should().Be("In 1 MB / Out 2 MB");
        info.TrafficRateTextDisplay.Should().Be("20 KB/s");
        info.AttentionReasonDisplay.Should().Be("Port down");
        info.RecommendedNextCheckDisplay.Should().Be("Check cable.");
        info.VerdictDisplay.Should().Be("Warning");
        info.SeverityDisplay.Should().Be("Warning");
    }
}
