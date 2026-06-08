using NetScopeDiagnosticCenter.Core.Models.Wifi;
using NetScopeDiagnosticCenter.Infrastructure.Wlan;

namespace NetScopeDiagnosticCenter.Tests.Core.Wifi;

public class WifiPhyStandardTests
{
    [Theory]
    [InlineData(Dot11PhyType.Ht, WifiBand.TwoPointFourGhz, "Wi-Fi 4 (n)")]
    [InlineData(Dot11PhyType.Vht, WifiBand.FiveGhz, "Wi-Fi 5 (ac)")]
    [InlineData(Dot11PhyType.He, WifiBand.FiveGhz, "Wi-Fi 6 (ax)")]
    [InlineData(Dot11PhyType.Eht, WifiBand.SixGhz, "Wi-Fi 7 (be)")]
    public void ToFriendlyName_StandardMapping(Dot11PhyType phy, WifiBand band, string expected)
    {
        WifiPhyStandard.ToFriendlyName(phy, band).Should().Be(expected);
    }

    [Fact]
    public void ToFriendlyName_HePhyOn6Ghz_LabelsAsWifi6E()
    {
        // The 6E label requires the band check — HE on 5 GHz is "Wi-Fi 6", on 6 GHz it's "Wi-Fi 6E".
        WifiPhyStandard.ToFriendlyName(Dot11PhyType.He, WifiBand.SixGhz).Should().Be("Wi-Fi 6E (ax)");
        WifiPhyStandard.ToFriendlyName(Dot11PhyType.He, WifiBand.FiveGhz).Should().Be("Wi-Fi 6 (ax)");
    }

    [Theory]
    [InlineData(Dot11PhyType.Ht, "n")]
    [InlineData(Dot11PhyType.Vht, "ac")]
    [InlineData(Dot11PhyType.He, "ax")]
    [InlineData(Dot11PhyType.Eht, "be")]
    [InlineData(Dot11PhyType.Erp, "g")]
    [InlineData(Dot11PhyType.Unknown, "?")]
    public void ToShortLabel_ReturnsCompactLetter(Dot11PhyType phy, string expected)
    {
        WifiPhyStandard.ToShortLabel(phy).Should().Be(expected);
    }

    [Theory]
    [InlineData(Dot11PhyType.Ht, "802.11n")]
    [InlineData(Dot11PhyType.Vht, "802.11ac")]
    [InlineData(Dot11PhyType.He, "802.11ax")]
    [InlineData(Dot11PhyType.Eht, "802.11be")]
    public void ToIeeeName_ReturnsIeeeIdentifier(Dot11PhyType phy, string expected)
    {
        WifiPhyStandard.ToIeeeName(phy).Should().Be(expected);
    }

    [Fact]
    public void ToFriendlyName_UnknownPhy_ReturnsUnknown()
    {
        WifiPhyStandard.ToFriendlyName(Dot11PhyType.Unknown, WifiBand.Unknown).Should().Be("Unknown");
    }
}
