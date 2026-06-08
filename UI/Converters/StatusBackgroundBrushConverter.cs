using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace NetScopeDiagnosticCenter.UI.Converters;

/// <summary>
/// Returns a light pastel background brush per severity, intended for module
/// module cards. Colors match Tailwind level-50 — easy on the eyes while still
/// communicating status at a glance.
/// </summary>
public sealed class StatusBackgroundBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value?.ToString() switch
        {
            "OK"       => new SolidColorBrush(Color.FromRgb(220, 252, 231)), // green-50
            "Warning"  => new SolidColorBrush(Color.FromRgb(254, 243, 199)), // amber-50
            "Critical" => new SolidColorBrush(Color.FromRgb(254, 226, 226)), // red-50
            _          => new SolidColorBrush(Color.FromRgb(248, 250, 252))  // slate-50
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
