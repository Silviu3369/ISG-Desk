namespace NetScopeDiagnosticCenter.Infrastructure.Wlan;

/// <summary>
/// Derives a Wi-Fi BSS operating channel width (20 / 40 / 80 / 160 MHz) from the raw 802.11
/// beacon / probe-response information elements. <c>WLAN_BSS_ENTRY</c> exposes no channel-width
/// field, so we walk the IE blob the driver captured — the same technique Wireshark and
/// inSSIDer use.
///
/// <para>
/// This type is deliberately pure and side-effect free (operates on a managed
/// <see cref="ReadOnlySpan{T}"/>, no P/Invoke, no allocation) so the bit-level parsing can be
/// unit-tested independently of the WLAN native layer. <see cref="WlanApiNative"/> copies the
/// native IE blob into a managed buffer and hands it to <see cref="FromInformationElements"/>.
/// </para>
/// </summary>
public static class WifiChannelWidthDecoder
{
    // 802.11 element IDs that carry operating-width information.
    private const int HtOperationElementId = 61;    // Wi-Fi 4 — 20 vs 40 MHz
    private const int VhtOperationElementId = 192;   // Wi-Fi 5 — 80 / 160 MHz
    private const int ExtensionElementId = 255;      // element-ID-extension marker
    private const int HeOperationExtId = 36;         // Wi-Fi 6/6E — HE Operation (ext ID 36)

    /// <summary>
    /// Walks the IE list and returns the operating channel width in MHz.
    /// <list type="bullet">
    ///   <item>HT Operation  (element ID 61)              → 20 vs 40 MHz (Wi-Fi 4)</item>
    ///   <item>VHT Operation (element ID 192)             → 80 / 160 MHz (Wi-Fi 5)</item>
    ///   <item>HE Operation  (element ID 255, ext ID 36)  → 5 GHz + 6 GHz width (Wi-Fi 6/6E)</item>
    /// </list>
    /// Returns null for an empty blob. A non-empty blob with no width-bearing IE means a legacy
    /// 802.11a/b/g AP and is reported as 20 MHz. The most specific source wins:
    /// HE 6 GHz width &gt; VHT &gt; HT &gt; legacy 20 MHz.
    /// </summary>
    public static int? FromInformationElements(ReadOnlySpan<byte> elements)
    {
        if (elements.IsEmpty) return null;

        int? htWidth = null;     // 20 / 40 from HT Operation
        int? vhtWidth = null;    // 80 / 160 from VHT Operation (standalone or HE-embedded)
        int? heWidth = null;     // width from HE 6 GHz Operation Information

        // Each IE is [element-id:1][length:1][data:length]. Walk until we run out of bytes.
        var i = 0;
        while (i + 2 <= elements.Length)
        {
            int id = elements[i];
            int len = elements[i + 1];
            var dataStart = i + 2;
            if (dataStart + len > elements.Length) break;   // truncated final IE — stop

            switch (id)
            {
                case HtOperationElementId when len >= 2:
                {
                    // data[1] = HT Operation Information byte 1:
                    //   bits 0-1 Secondary Channel Offset (1 = above, 3 = below, 0 = none)
                    //   bit  2   STA Channel Width (1 = operation wider than 20 MHz allowed)
                    var opInfo = elements[dataStart + 1];
                    var secondaryOffset = opInfo & 0x03;
                    var wideAllowed = (opInfo & 0x04) != 0;
                    htWidth = wideAllowed && secondaryOffset != 0 ? 40 : 20;
                    break;
                }
                case VhtOperationElementId when len >= 3:
                    vhtWidth = DecodeVhtWidth(elements[dataStart], elements[dataStart + 1], elements[dataStart + 2]);
                    break;
                case ExtensionElementId when len >= 1 && elements[dataStart] == HeOperationExtId:
                {
                    var (sixGhz, embeddedVht) = DecodeHeWidth(elements.Slice(dataStart + 1, len - 1));
                    if (sixGhz is { } sw) heWidth = sw;
                    if (embeddedVht is { } vw && vhtWidth is null) vhtWidth = vw;
                    break;
                }
            }

            i = dataStart + len;
        }

        return heWidth ?? vhtWidth ?? htWidth ?? 20;
    }

    /// <summary>
    /// Decodes the VHT Operation "Channel Width" byte plus the two centre-frequency segment
    /// indices. IEEE 802.11-2016 folded the deprecated explicit 160 / 80+80 encodings into
    /// width=1 + dual segments, so we inspect the segment gap: a gap of 8 channels means a
    /// contiguous 160 MHz channel. Returns null for width=0 (20/40 — defer to HT Operation).
    /// </summary>
    private static int? DecodeVhtWidth(byte channelWidth, byte segment0, byte segment1)
        => channelWidth switch
        {
            0 => null,                                        // 20/40 — HT element disambiguates
            2 => 160,                                         // deprecated explicit 160
            3 => 160,                                         // deprecated 80+80 — report as 160-class
            _ => segment1 != 0 && Math.Abs(segment1 - segment0) == 8 ? 160 : 80,
        };

    /// <summary>
    /// Parses the HE Operation element body (the bytes AFTER the Element ID Extension byte)
    /// for channel width. Layout: [HE Operation Parameters:3][BSS Color:1][Basic HE-MCS/NSS:2]
    /// then the optional [VHT Operation Information:3] / [Max Co-Hosted BSSID:1] / [6 GHz
    /// Operation Information:5] fields, each gated by a presence bit in HE Operation Parameters
    /// (bit 14 = VHT info, bit 15 = co-hosted BSS, bit 17 = 6 GHz info).
    /// </summary>
    private static (int? SixGhzWidth, int? EmbeddedVhtWidth) DecodeHeWidth(ReadOnlySpan<byte> body)
    {
        if (body.Length < 6) return (null, null);

        var paramBits = body[0] | (body[1] << 8) | (body[2] << 16);
        var vhtPresent = (paramBits & (1 << 14)) != 0;
        var coHostedPresent = (paramBits & (1 << 15)) != 0;
        var sixGhzPresent = (paramBits & (1 << 17)) != 0;

        var cursor = 6;   // past Parameters(3) + BSS Color(1) + Basic HE-MCS/NSS(2)
        int? embeddedVht = null;

        if (vhtPresent)
        {
            if (cursor + 3 <= body.Length)
            {
                embeddedVht = DecodeVhtWidth(body[cursor], body[cursor + 1], body[cursor + 2]);
            }
            cursor += 3;
        }

        if (coHostedPresent)
        {
            cursor += 1;   // Max Co-Hosted BSSID Indicator
        }

        int? sixGhzWidth = null;
        if (sixGhzPresent && cursor + 5 <= body.Length)
        {
            // 6 GHz Operation Information: [Primary:1][Control:1][CCFS0:1][CCFS1:1][MinRate:1].
            // Control bits 0-1 = Channel Width (0=20, 1=40, 2=80, 3=160/80+80).
            var control = body[cursor + 1];
            sixGhzWidth = (control & 0x03) switch
            {
                0 => 20,
                1 => 40,
                2 => 80,
                _ => 160,
            };
        }

        return (sixGhzWidth, embeddedVht);
    }
}
