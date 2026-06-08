using NetScopeDiagnosticCenter.Core.Models.Wifi;

namespace NetScopeDiagnosticCenter.Core.Wifi;

/// <summary>
/// Pure functions for translating between centre frequency (kHz / MHz) ↔ channel ↔ band.
/// Used by <c>WlanApiScanner</c> to convert raw WLAN_BSS_ENTRY frequencies and by
/// <c>WifiAnalyzerEngine</c> to compute channel overlap.
///
/// <para>References:</para>
/// <list type="bullet">
///   <item>IEEE 802.11-2020 Annex E.1 — channel numbering and centre frequencies.</item>
///   <item>FCC 47 CFR Part 15.247 / 15.407 — band edges for 2.4 / 5 / 6 GHz.</item>
/// </list>
///
/// <para>Boundary handling:</para>
/// <list type="bullet">
///   <item>2.4 GHz: 2412..2484 MHz, channels 1..14 (14 only in JP). Each channel is 5 MHz apart
///         except channel 14 which is 12 MHz above channel 13.</item>
///   <item>5 GHz: 5170..5895 MHz at 5 MHz steps; channel = (freq - 5000) / 5.</item>
///   <item>6 GHz: 5945..7115 MHz at 5 MHz steps; channel = (freq - 5950) / 5 + 1.</item>
/// </list>
/// </summary>
public static class WifiFrequencyHelper
{
    /// <summary>
    /// 5 GHz channels covered by Dynamic Frequency Selection (DFS) — must vacate on radar
    /// detection. Helpdesk uses this flag to advise away from channels that randomly
    /// disconnect APs in industrial / airport environments.
    /// </summary>
    private static readonly HashSet<int> DfsChannels = new()
    {
        52, 56, 60, 64,                      // U-NII-2A
        100, 104, 108, 112, 116, 120, 124,
        128, 132, 136, 140, 144,             // U-NII-2C
    };

    /// <summary>
    /// Convert a centre frequency (in MHz) to band + channel number. Returns
    /// (Unknown, 0) when the frequency is outside known Wi-Fi ranges.
    /// </summary>
    public static (WifiBand Band, int Channel) FromFrequencyMhz(int frequencyMhz)
    {
        // 2.4 GHz: ISM band 2400..2500 MHz.
        if (frequencyMhz >= 2412 && frequencyMhz <= 2472)
        {
            // Channels 1..13 at 5 MHz steps starting from 2412.
            var ch = (frequencyMhz - 2412) / 5 + 1;
            return (WifiBand.TwoPointFourGhz, ch);
        }
        if (frequencyMhz == 2484)
        {
            // Japan-only channel 14 (2484 MHz, 12 MHz above ch 13).
            return (WifiBand.TwoPointFourGhz, 14);
        }

        // 5 GHz: U-NII bands 5150..5895 MHz.
        if (frequencyMhz >= 5170 && frequencyMhz <= 5895)
        {
            // channel = (freq - 5000) / 5  for the standard 5 MHz grid.
            var ch = (frequencyMhz - 5000) / 5;
            return (WifiBand.FiveGhz, ch);
        }

        // 6 GHz: U-NII-5..U-NII-8 bands 5925..7125 MHz (Wi-Fi 6E).
        if (frequencyMhz >= 5945 && frequencyMhz <= 7125)
        {
            // 6 GHz channel numbering restarts at 1: channel 1 = 5955 MHz.
            // Microsoft reports the centre frequency directly; we derive channel.
            var ch = (frequencyMhz - 5950) / 5 + 1;
            return (WifiBand.SixGhz, ch);
        }

        return (WifiBand.Unknown, 0);
    }

    /// <summary>
    /// Convert a centre frequency reported by WLAN API (in kHz) directly to band+channel.
    /// WLAN_BSS_ENTRY.ulChCenterFrequency is documented in kHz.
    /// </summary>
    public static (WifiBand Band, int Channel, int FrequencyMhz) FromFrequencyKhz(int frequencyKhz)
    {
        var mhz = frequencyKhz / 1000;
        var (band, ch) = FromFrequencyMhz(mhz);
        return (band, ch, mhz);
    }

    /// <summary>
    /// True if the given 5 GHz channel is a DFS channel (radar avoidance required).
    /// Always false for 2.4 GHz and 6 GHz.
    /// </summary>
    public static bool IsDfsChannel(WifiBand band, int channel) =>
        band == WifiBand.FiveGhz && DfsChannels.Contains(channel);

    /// <summary>
    /// Returns the list of 2.4 GHz channels whose 20 MHz mask overlaps the given channel.
    /// Used by the channel-map UI to compute "APs on overlapping channels".
    /// 2.4 GHz channels are 5 MHz apart but each AP transmits over 22 MHz, so a window of
    /// ±2 channels overlaps. Examples:
    ///   ch 1 → {1,2,3,4,5}
    ///   ch 6 → {4,5,6,7,8}
    ///   ch 11 → {9,10,11,12,13}
    /// Returns just [channel] for 5 GHz / 6 GHz (non-overlapping at 20 MHz).
    /// </summary>
    public static IEnumerable<int> GetOverlappingChannels(WifiBand band, int channel)
    {
        if (band != WifiBand.TwoPointFourGhz) return new[] { channel };

        var start = Math.Max(1, channel - 2);
        var end = Math.Min(14, channel + 2);
        var range = new List<int>(5);
        for (var c = start; c <= end; c++) range.Add(c);
        return range;
    }

    /// <summary>
    /// Bucket an AP-on-channel count into a congestion level.
    /// </summary>
    public static WifiCongestionLevel ClassifyCongestion(int apCount) => apCount switch
    {
        0 => WifiCongestionLevel.Free,
        1 or 2 => WifiCongestionLevel.Light,
        3 or 4 => WifiCongestionLevel.Moderate,
        _ => WifiCongestionLevel.Heavy,
    };

    /// <summary>
    /// Bucket an RSSI dBm reading into a friendly signal level.
    /// Thresholds match WifiSignalClassifier (already in app):
    ///   Excellent: &gt;= -65
    ///   Good:      -65 .. -75
    ///   Fair:      -75 .. -85
    ///   Poor:      &lt; -85
    /// </summary>
    public static WifiSignalLevel ClassifySignal(int? rssiDbm)
    {
        if (rssiDbm is null) return WifiSignalLevel.Unknown;
        if (rssiDbm.Value >= -65) return WifiSignalLevel.Excellent;
        if (rssiDbm.Value >= -75) return WifiSignalLevel.Good;
        if (rssiDbm.Value >= -85) return WifiSignalLevel.Fair;
        return WifiSignalLevel.Poor;
    }
}
