using System.Globalization;
using System.Windows.Data;

namespace NetScopeDiagnosticCenter.UI.Converters;

/// <summary>
/// Returns <c>true</c> when both bound strings are equal (case-insensitive). Used
/// in <see cref="System.Windows.DataTrigger"/>s to detect active sidebar navigation:
/// bind the button's <c>CommandParameter</c> and the shell's <c>CurrentPage</c> and
/// trigger an "active" visual style when they match.
/// </summary>
public sealed class StringEqualsConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values is null || values.Length < 2) return false;
        var a = values[0]?.ToString();
        var b = values[1]?.ToString();
        return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
