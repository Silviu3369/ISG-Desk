using System.Collections.Concurrent;
using System.Windows.Media;

namespace NetScopeDiagnosticCenter.UI.Controls;

/// <summary>
/// Deterministic, perceptually-DISTINCT per-network colour shared by
/// <see cref="WifiChannelGraph"/> and <see cref="WifiSignalTimeGraph"/>.
///
/// <para>
/// The old approach hashed the BSSID into an HSL hue. That looked terrible in practice:
/// similar BSSIDs (same vendor / adjacent MACs — exactly the Telenet case the user hit)
/// produced near-identical hues, so multiple lines were visually the same colour. This
/// replaces it with a CURATED 20-colour palette of maximally-distinct, legible hues
/// (ColorBrewer / Tableau-style) assigned by a stable first-seen INDEX per BSSID. Each
/// network therefore gets a clearly different colour, keeps it across rescans, and uses
/// the SAME colour in both charts (cross-chart identity).
/// </para>
///
/// <para>Thread-safe: the registry is a <see cref="ConcurrentDictionary{TKey,TValue}"/>
/// (graphs redraw on the UI thread, but scan/sampler callbacks may touch it off-thread).</para>
/// </summary>
internal static class WifiColorPalette
{
    // 20 hand-picked vivid colours, ordered for maximum adjacent contrast on a dark plot.
    private static readonly Color[] Palette =
    {
        FromHex("#38BDF8"), // sky
        FromHex("#F472B6"), // pink
        FromHex("#A3E635"), // lime
        FromHex("#FB923C"), // orange
        FromHex("#A78BFA"), // violet
        FromHex("#2DD4BF"), // teal
        FromHex("#FACC15"), // amber
        FromHex("#F87171"), // red
        FromHex("#60A5FA"), // blue
        FromHex("#34D399"), // emerald
        FromHex("#E879F9"), // fuchsia
        FromHex("#FBBF24"), // gold
        FromHex("#4ADE80"), // green
        FromHex("#22D3EE"), // cyan
        FromHex("#C084FC"), // purple
        FromHex("#FB7185"), // rose
        FromHex("#93C5FD"), // light blue
        FromHex("#FDE047"), // yellow
        FromHex("#5EEAD4"), // aqua
        FromHex("#D8B4FE"), // lavender
    };

    private static readonly ConcurrentDictionary<string, int> _index = new(StringComparer.OrdinalIgnoreCase);
    private static int _next = -1;

    /// <summary>
    /// Stable distinct colour for a BSSID. First time a BSSID is seen it claims the next
    /// palette slot; thereafter it always returns the same colour.
    /// </summary>
    public static Color ForBssid(string bssid)
    {
        var idx = _index.GetOrAdd(bssid, _ => System.Threading.Interlocked.Increment(ref _next));
        return Palette[idx % Palette.Length];
    }

    private static Color FromHex(string hex) => (Color)ColorConverter.ConvertFromString(hex);
}
