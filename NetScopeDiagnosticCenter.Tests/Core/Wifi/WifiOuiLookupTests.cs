using NetScopeDiagnosticCenter.Core.Wifi;

namespace NetScopeDiagnosticCenter.Tests.Core.Wifi;

public class WifiOuiLookupTests
{
    [Theory]
    [InlineData("00:01:42:AB:CD:EF", "Cisco")]
    [InlineData("F0:9F:C2:11:22:33", "Ubiquiti")]
    [InlineData("4C:5E:0C:00:11:22", "MikroTik")]
    [InlineData("78:24:8F:DE:AD:BE", "Asus")]
    [InlineData("E4:0F:65:01:02:03", "Aruba")]
    public void Lookup_KnownVendor_ReturnsCorrectName(string bssid, string expected)
    {
        WifiOuiLookup.Lookup(bssid).Should().Be(expected);
    }

    [Theory]
    [InlineData("00:01:42:AB:CD:EF")]   // colon-separated
    [InlineData("00-01-42-AB-CD-EF")]   // dash-separated
    [InlineData("000142ABCDEF")]        // raw hex
    [InlineData("00:01:42-ab-cd-ef")]   // mixed separators + lowercase
    public void Lookup_AcceptsCommonFormats(string bssid)
    {
        WifiOuiLookup.Lookup(bssid).Should().Be("Cisco");
    }

    [Theory]
    [InlineData("FF:FF:FF:00:00:00")]   // not a real OUI
    [InlineData("AA:BB:CC:DD:EE:FF")]   // not in our embedded list
    public void Lookup_UnknownOui_ReturnsNull(string bssid)
    {
        WifiOuiLookup.Lookup(bssid).Should().BeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-bssid")]
    [InlineData("00:11")]                // too short
    [InlineData("XX:YY:ZZ:00:00:00")]    // non-hex
    public void Lookup_InvalidInput_ReturnsNull(string? bssid)
    {
        WifiOuiLookup.Lookup(bssid).Should().BeNull();
    }

    [Fact]
    public void Lookup_IsCaseInsensitive()
    {
        // Same OUI in all-lower / all-upper / mixed should hit the same entry.
        WifiOuiLookup.Lookup("00:01:42:11:22:33").Should().Be("Cisco");
        WifiOuiLookup.Lookup("00:01:42:11:22:33".ToLowerInvariant()).Should().Be("Cisco");
        WifiOuiLookup.Lookup("00:01:42:11:22:33".ToUpperInvariant()).Should().Be("Cisco");
    }

    [Fact]
    public void Lookup_OnlyConsidersFirstThreeBytes()
    {
        // Two different MACs with same OUI must return the same vendor.
        WifiOuiLookup.Lookup("E4:0F:65:00:00:01").Should().Be("Aruba");
        WifiOuiLookup.Lookup("E4:0F:65:FF:FF:FF").Should().Be("Aruba");
    }

    [Theory]
    [InlineData("0E:96:86:0C:3D:95", true)]   // 0x0E → U/L bit set (Telenet virtual radio)
    [InlineData("02:00:00:00:00:00", true)]   // 0x02 → set
    [InlineData("06:11:22:33:44:55", true)]   // 0x06 → set
    [InlineData("0A:BB:CC:DD:EE:FF", true)]   // 0x0A → set
    [InlineData("00:01:42:AA:BB:CC", false)]  // 0x00 → universal (real Cisco OUI)
    [InlineData("E4:0F:65:00:00:01", false)]  // 0xE4 → universal (real Aruba OUI)
    [InlineData("D8:33:B7:00:00:01", false)]  // 0xD8 → universal
    public void IsLocallyAdministered_DetectsUlBit(string bssid, bool expected)
    {
        WifiOuiLookup.IsLocallyAdministered(bssid).Should().Be(expected);
    }

    [Fact]
    public void DescribeVendor_LocallyAdministered_ExplainsInsteadOfUnknown()
    {
        // The exact BSSID from the user's screenshot — locally administered, no vendor.
        WifiOuiLookup.DescribeVendor("0E:96:86:0C:3D:95")
            .Should().Be("Private (randomized MAC)");
    }

    [Fact]
    public void DescribeVendor_KnownUniversalOui_ReturnsVendor()
    {
        WifiOuiLookup.DescribeVendor("00:01:42:AA:BB:CC").Should().Be("Cisco");
    }

    [Fact]
    public void DescribeVendor_UnknownUniversalOui_ReturnsNull()
    {
        // Universal address but not in our embedded DB → null (UI shows "Vendor unknown").
        WifiOuiLookup.DescribeVendor("D8:33:B7:00:00:01").Should().BeNull();
    }
}
