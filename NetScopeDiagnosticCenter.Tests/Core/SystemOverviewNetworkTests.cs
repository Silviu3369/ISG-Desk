using NetScopeDiagnosticCenter.Core.Models;

namespace NetScopeDiagnosticCenter.Tests.Core;

public class SystemOverviewNetworkTests
{
    [Theory]
    [InlineData("Wi-Fi", "Wi-Fi", "Wi-Fi")]                       // literal duplicate collapses
    [InlineData("wi-fi", "Wi-Fi", "wi-fi")]                       // case-insensitive collapse
    [InlineData("Ethernet 2", "Ethernet", "Ethernet 2 · Ethernet")] // distinct values stay joined
    [InlineData("Unknown", "Unknown", "Unknown")]                 // double-unknown collapses
    public void AdapterDisplay_CollapsesDuplicateNameAndType(string name, string type, string expected)
    {
        var network = new SystemOverviewNetwork { AdapterName = name, ConnectionType = type };

        network.AdapterDisplay.Should().Be(expected);
    }
}
