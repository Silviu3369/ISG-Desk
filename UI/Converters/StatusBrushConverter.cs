using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace NetScopeDiagnosticCenter.UI.Converters;

public sealed class StatusBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value?.ToString() switch
        {
            "OK" => new SolidColorBrush(Color.FromRgb(34, 197, 94)),
            "Info" => new SolidColorBrush(Color.FromRgb(14, 165, 233)),
            "Warning" => new SolidColorBrush(Color.FromRgb(245, 158, 11)),
            "Critical" => new SolidColorBrush(Color.FromRgb(239, 68, 68)),
            _ => new SolidColorBrush(Color.FromRgb(100, 116, 139))
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
