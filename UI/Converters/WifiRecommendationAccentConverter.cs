using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace NetScopeDiagnosticCenter.UI.Converters;

/// <summary>
/// Maps a Wi-Fi recommendation string to a severity accent colour. The mapping is based
/// on stable text prefixes from WifiAnalyzerEngine, not emoji glyphs, so it remains
/// reliable on machines with different fonts or console encodings.
/// </summary>
public sealed class WifiRecommendationAccentConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var text = value as string ?? string.Empty;
        var background = string.Equals(parameter as string, "bg", StringComparison.OrdinalIgnoreCase);

        var (accent, tint) = Tier(text) switch
        {
            Severity.Critical => ("#DC2626", "#FEF2F2"),
            Severity.Info => ("#2563EB", "#EFF6FF"),
            _ => ("#F59E0B", "#FFFBEB"),
        };

        return new SolidColorBrush((Color)ColorConverter.ConvertFromString(background ? tint : accent));
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();

    private static Severity Tier(string text)
    {
        if (StartsWithAny(text, "Security:"))
        {
            return Severity.Critical;
        }

        if (StartsWithAny(text, "Capability:", "Density:", "Connect "))
        {
            return Severity.Info;
        }

        return Severity.Caution;
    }

    private static bool StartsWithAny(string text, params string[] prefixes)
        => prefixes.Any(prefix => text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    private enum Severity
    {
        Critical,
        Caution,
        Info
    }
}
