namespace NetScopeDiagnosticCenter.Core.Models;

/// <summary>
/// One printer consumable (toner / drum / waste box) decoded from the Printer-MIB
/// (RFC 3805, prtMarkerSupplies table). Levels carry MIB sentinels: -1 = other,
/// -2 = unknown, -3 = "some remaining, not measurable" — surfaced as text, not a bar.
/// </summary>
public sealed class SnmpSupplyInfo
{
    public string Name { get; set; } = string.Empty;

    /// <summary>Raw <c>prtMarkerSuppliesLevel</c>.</summary>
    public int Level { get; set; }

    /// <summary>Raw <c>prtMarkerSuppliesMaxCapacity</c>.</summary>
    public int MaxCapacity { get; set; }

    /// <summary>0–100 when both level and max are positive; null when the printer can't measure it.</summary>
    public int? PercentRemaining =>
        Level > 0 && MaxCapacity > 0
            ? Math.Clamp((int)Math.Round(Level * 100.0 / MaxCapacity), 0, 100)
            : null;

    /// <summary>Display text: "62%", "Some remaining", "Unknown", or "OK".</summary>
    public string LevelText => PercentRemaining is { } p
        ? $"{p}%"
        : Level switch
        {
            -3 => "Some remaining",
            -2 => "Unknown",
            -1 => "—",
            _ => Level >= 0 ? Level.ToString() : "—",
        };

    /// <summary>Toner/ink colour guessed from the supply description, for the UI bar.</summary>
    public string ColorHex
    {
        get
        {
            var n = Name.ToLowerInvariant();
            if (n.Contains("cyan")) return "#06B6D4";
            if (n.Contains("magenta")) return "#DB2777";
            if (n.Contains("yellow")) return "#F59E0B";
            if (n.Contains("black") || n.Contains("/k") || n.Contains(" k ")) return "#334155";
            if (n.Contains("drum") || n.Contains("imaging") || n.Contains("photoconductor")) return "#7C3AED";
            if (n.Contains("waste") || n.Contains("maintenance")) return "#94A3B8";
            return "#0EA5E9";
        }
    }

    /// <summary>Bar fill width in px (0–200), scaled by <see cref="PercentRemaining"/>.</summary>
    public double BarWidth => PercentRemaining is { } p ? p / 100.0 * 200.0 : 0;

    /// <summary>True when the level is measurable and low enough to warrant attention.</summary>
    public bool IsLow => PercentRemaining is { } p && p <= 15;
}
