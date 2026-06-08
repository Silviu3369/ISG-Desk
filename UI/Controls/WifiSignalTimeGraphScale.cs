using System.Windows;

namespace NetScopeDiagnosticCenter.UI.Controls;

/// <summary>
/// Pure scale helpers for the Signal Over Time graph. Keeping the math outside the WPF
/// control lets us regression-test right-alignment and RSSI clamping without rendering.
/// </summary>
public static class WifiSignalTimeGraphScale
{
    public const double RssiTop = -30;
    public const double RssiFloor = -90;

    public static double RssiToY(double rssiDbm, double top, double bottom)
    {
        var clamped = Math.Clamp(rssiDbm, RssiFloor, RssiTop);
        return bottom - (clamped - RssiFloor) / (RssiTop - RssiFloor) * (bottom - top);
    }

    public static IReadOnlyList<Point> BuildSeriesPoints(
        IReadOnlyList<int> rssiHistory,
        int maxSeriesLength,
        double left,
        double right,
        double top,
        double bottom)
    {
        if (rssiHistory.Count == 0)
        {
            return Array.Empty<Point>();
        }

        var plotWidth = right - left;
        var xDiv = Math.Max(maxSeriesLength - 1, 1);
        var offset = Math.Max(maxSeriesLength - rssiHistory.Count, 0);
        var points = new List<Point>(rssiHistory.Count);

        for (var i = 0; i < rssiHistory.Count; i++)
        {
            var x = maxSeriesLength <= 1
                ? right
                : left + (double)(offset + i) / xDiv * plotWidth;
            points.Add(new Point(x, RssiToY(rssiHistory[i], top, bottom)));
        }

        return points;
    }
}
