using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace NetScopeDiagnosticCenter.UI.Converters;

/// <summary>
/// Visibility converter for severity strings: hide ("Collapsed") when the value is
/// <c>null</c>, empty, or "Unknown" — show otherwise. Used to keep the sidebar status
/// dots out of view until a module has actually produced a measurement.
/// </summary>
public sealed class StatusKnownToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var status = value?.ToString();
        return string.IsNullOrWhiteSpace(status) ||
               string.Equals(status, "Unknown", StringComparison.OrdinalIgnoreCase)
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
