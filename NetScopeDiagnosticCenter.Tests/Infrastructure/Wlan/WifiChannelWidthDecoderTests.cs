using NetScopeDiagnosticCenter.Infrastructure.Wlan;

namespace NetScopeDiagnosticCenter.Tests.Infrastructure.Wlan;

/// <summary>
/// Unit tests for <see cref="WifiChannelWidthDecoder"/> — the pure 802.11 information-element
/// parser that derives a BSS operating channel width (20/40/80/160 MHz) from beacon HT / VHT /
/// HE operation elements. Each test builds a synthetic IE blob byte-for-byte so the bit-level
/// decoding is verified independently of the WLAN P/Invoke layer.
/// </summary>
public class WifiChannelWidthDecoderTests
{
    // ----- IE blob builders -------------------------------------------------

    /// <summary>Builds one information element: [element-id][length][data…].</summary>
    private static byte[] Ie(int id, params byte[] data)
    {
        var ie = new byte[2 + data.Length];
        ie[0] = (byte)id;
        ie[1] = (byte)data.Length;
        data.CopyTo(ie, 2);
        return ie;
    }

    private static byte[] Concat(params byte[][] parts)
    {
        var result = new byte[parts.Sum(p => p.Length)];
        var offset = 0;
        foreach (var p in parts)
        {
            p.CopyTo(result, offset);
            offset += p.Length;
        }
        return result;
    }

    /// <summary>HT Operation element (id 61): primary channel + HT Operation Information byte 1.</summary>
    private static byte[] HtOperation(byte htOpInfoByte1) => Ie(61, 0x24 /* primary ch 36 */, htOpInfoByte1);

    /// <summary>VHT Operation element (id 192): channel-width byte + two centre-freq segments.</summary>
    private static byte[] VhtOperation(byte channelWidth, byte ccfs0, byte ccfs1)
        => Ie(192, channelWidth, ccfs0, ccfs1, 0x00, 0x00 /* VHT-MCS/NSS set */);

    private static byte[] HeOperation(byte[] heBody)
    {
        var data = new byte[1 + heBody.Length];
        data[0] = 36;                       // Element ID Extension = HE Operation
        heBody.CopyTo(data, 1);
        return Ie(255, data);
    }

    /// <summary>HE Operation carrying a 6 GHz Operation Information field with the given Control byte.</summary>
    private static byte[] HeOperation6Ghz(byte sixGhzControl) => HeOperation(new byte[]
    {
        0x00, 0x00, 0x02,                   // HE Operation Parameters — bit 17 (6 GHz info present)
        0x00,                               // BSS Color Information
        0x00, 0x00,                         // Basic HE-MCS and NSS Set
        0x25, sixGhzControl, 0x00, 0x00, 0x00,  // 6 GHz Op Info: primary, control, ccfs0, ccfs1, min-rate
    });

    /// <summary>HE Operation carrying an embedded VHT Operation Information field.</summary>
    private static byte[] HeOperationEmbeddedVht(byte vhtWidth, byte ccfs0, byte ccfs1) => HeOperation(new byte[]
    {
        0x00, 0x40, 0x00,                   // HE Operation Parameters — bit 14 (VHT info present)
        0x00,                               // BSS Color Information
        0x00, 0x00,                         // Basic HE-MCS and NSS Set
        vhtWidth, ccfs0, ccfs1,             // embedded VHT Operation Information
    });

    // ----- tests ------------------------------------------------------------

    [Fact]
    public void FromInformationElements_EmptyBlob_ReturnsNull()
        => WifiChannelWidthDecoder.FromInformationElements(ReadOnlySpan<byte>.Empty).Should().BeNull();

    [Fact]
    public void FromInformationElements_NoWidthElements_ReportsLegacy20Mhz()
    {
        // A legacy 802.11b/g beacon carrying only an SSID element (id 0).
        var blob = Ie(0, 0x4E, 0x65, 0x74);
        WifiChannelWidthDecoder.FromInformationElements(blob).Should().Be(20);
    }

    [Fact]
    public void FromInformationElements_HtOperationNarrow_Returns20()
        => WifiChannelWidthDecoder.FromInformationElements(HtOperation(0x00)).Should().Be(20);

    [Fact]
    public void FromInformationElements_HtOperation40Mhz_Returns40()
    {
        // STA Channel Width bit (0x04) + Secondary Channel Offset "above" (0x01).
        WifiChannelWidthDecoder.FromInformationElements(HtOperation(0x05)).Should().Be(40);
    }

    [Fact]
    public void FromInformationElements_HtSecondaryOffsetWithoutWideBit_Returns20()
    {
        // Secondary Channel Offset set but STA Channel Width bit clear → still 20 MHz.
        WifiChannelWidthDecoder.FromInformationElements(HtOperation(0x01)).Should().Be(20);
    }

    [Fact]
    public void FromInformationElements_VhtOperation80Mhz_Returns80()
        => WifiChannelWidthDecoder.FromInformationElements(VhtOperation(1, 42, 0)).Should().Be(80);

    [Fact]
    public void FromInformationElements_VhtContiguous160_Returns160()
    {
        // width=1 with two centre-freq segments 8 channels apart → contiguous 160 MHz.
        WifiChannelWidthDecoder.FromInformationElements(VhtOperation(1, 50, 58)).Should().Be(160);
    }

    [Fact]
    public void FromInformationElements_VhtDeprecated160Encoding_Returns160()
        => WifiChannelWidthDecoder.FromInformationElements(VhtOperation(2, 50, 0)).Should().Be(160);

    [Fact]
    public void FromInformationElements_VhtWidth0_DefersToHtOperation()
    {
        // VHT channel-width 0 means 20/40 — the HT element disambiguates (here: 40 MHz).
        var blob = Concat(HtOperation(0x05), VhtOperation(0, 36, 0));
        WifiChannelWidthDecoder.FromInformationElements(blob).Should().Be(40);
    }

    [Fact]
    public void FromInformationElements_He6GhzOperation_ReadsControlWidth()
    {
        // 6 GHz HE AP, Control channel-width field = 2 → 80 MHz.
        WifiChannelWidthDecoder.FromInformationElements(HeOperation6Ghz(0x02)).Should().Be(80);
    }

    [Fact]
    public void FromInformationElements_He6GhzOperation160_Returns160()
        => WifiChannelWidthDecoder.FromInformationElements(HeOperation6Ghz(0x03)).Should().Be(160);

    [Fact]
    public void FromInformationElements_HeEmbeddedVhtInfo_Returns80()
        => WifiChannelWidthDecoder.FromInformationElements(HeOperationEmbeddedVht(1, 42, 0)).Should().Be(80);

    [Fact]
    public void FromInformationElements_SkipsLeadingNonWidthElements()
    {
        // SSID + Supported Rates elements before the HT Operation element.
        var blob = Concat(Ie(0, 0x41, 0x42), Ie(1, 0x82, 0x84), HtOperation(0x05));
        WifiChannelWidthDecoder.FromInformationElements(blob).Should().Be(40);
    }

    [Fact]
    public void FromInformationElements_TruncatedTrailingElement_DoesNotThrow()
    {
        // HT element whose length byte overruns the buffer — the walk must stop gracefully.
        var blob = new byte[] { 61, 20, 0x24, 0x05 };
        var act = () => WifiChannelWidthDecoder.FromInformationElements(blob);
        act.Should().NotThrow();
        WifiChannelWidthDecoder.FromInformationElements(blob).Should().Be(20);
    }

    [Fact]
    public void FromInformationElements_He6GhzWinsOverVhtAndHt()
    {
        // Most specific source wins: HE 6 GHz width > VHT > HT.
        var blob = Concat(HtOperation(0x05), VhtOperation(1, 42, 0), HeOperation6Ghz(0x03));
        WifiChannelWidthDecoder.FromInformationElements(blob).Should().Be(160);
    }
}
