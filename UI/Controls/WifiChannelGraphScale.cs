using NetScopeDiagnosticCenter.Core.Models.Wifi;

namespace NetScopeDiagnosticCenter.UI.Controls;

/// <summary>
/// Pure channel-graph scale helpers kept separate from the WPF drawing code so the
/// professional spectrum math can be regression-tested without rendering a control.
/// </summary>
public static class WifiChannelGraphScale
{
    public static (int Lo, int Hi) GetChannelRange(WifiBand band) => band switch
    {
        WifiBand.TwoPointFourGhz => (1, 14),
        WifiBand.FiveGhz => (32, 177),
        WifiBand.SixGhz => (1, 233),
        _ => (1, 14),
    };

    public static IReadOnlyList<int> GetAxisChannels(WifiBand band) => band switch
    {
        WifiBand.TwoPointFourGhz => Enumerable.Range(1, 14).ToArray(),
        WifiBand.FiveGhz => new[] { 36, 44, 52, 60, 100, 108, 116, 124, 132, 140, 149, 157, 165, 173 },
        WifiBand.SixGhz => new[] { 1, 17, 33, 49, 65, 81, 97, 113, 129, 145, 161, 177, 193, 209, 225 },
        _ => Enumerable.Range(1, 14).ToArray(),
    };

    public static double GetHalfChannelWidth(WifiBand band, int? channelWidthMhz)
    {
        var widthMhz = Math.Clamp(channelWidthMhz ?? 20, 20, 320);
        return band == WifiBand.TwoPointFourGhz
            ? widthMhz / 5.0 / 2.0
            : widthMhz / 20.0 * 2.0;
    }
}
