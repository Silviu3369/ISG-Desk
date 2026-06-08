using System.Collections;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using NetScopeDiagnosticCenter.Core.Models.Wifi;
using NetScopeDiagnosticCenter.Core.Monitoring;

namespace NetScopeDiagnosticCenter.UI.Controls;

/// <summary>
/// Live area+line sparkline for the Wi-Fi monitor strip — RSSI, link speed or gateway
/// ping. Replaces the old "project into a fixed 240×60 canvas then Viewbox-stretch"
/// approach, which scaled the stroke too (the fat, distorted lines the user disliked).
///
/// <para>
/// This control draws straight at its real pixel size: the line is a crisp 1.4 px
/// anti-aliased stroke at every window width, the area is a soft vertical gradient, and
/// the X axis carries real wall-clock <c>HH:mm:ss</c> tick labels so the operator can
/// follow the monitor live. Y auto-scales to the data window. Binds directly to the raw
/// timestamped sample collections (no VM-side geometry projection needed).
/// </para>
/// </summary>
public partial class WifiSparklineGraph : UserControl
{
    private const double MarginLeft = 6;
    private const double MarginRight = 6;
    private const double MarginTop = 8;
    private const double MarginBottom = 16;   // room for the HH:mm:ss tick row

    public WifiSparklineGraph()
    {
        InitializeComponent();
        SizeChanged += (_, _) => Redraw();
        Loaded += (_, _) => Redraw();
    }

    public static readonly DependencyProperty SamplesProperty = DependencyProperty.Register(
        nameof(Samples), typeof(IEnumerable), typeof(WifiSparklineGraph),
        new PropertyMetadata(null, OnSamplesChanged));

    public IEnumerable? Samples
    {
        get => (IEnumerable?)GetValue(SamplesProperty);
        set => SetValue(SamplesProperty, value);
    }

    /// <summary>"Rssi" (dBm), "Speed" (PHY Mbps), "Throughput" (real Mbps) or "Ping" (ms).</summary>
    public static readonly DependencyProperty ModeProperty = DependencyProperty.Register(
        nameof(Mode), typeof(string), typeof(WifiSparklineGraph),
        new PropertyMetadata("Rssi", (d, _) => ((WifiSparklineGraph)d).Redraw()));

    public string Mode
    {
        get => (string)GetValue(ModeProperty);
        set => SetValue(ModeProperty, value);
    }

    private static void OnSamplesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var g = (WifiSparklineGraph)d;
        if (e.OldValue is INotifyCollectionChanged oc) oc.CollectionChanged -= g.OnCol;
        if (e.NewValue is INotifyCollectionChanged nc) nc.CollectionChanged += g.OnCol;
        g.Redraw();
    }

    private void OnCol(object? s, NotifyCollectionChangedEventArgs e)
    {
        if (Dispatcher.CheckAccess()) Redraw();
        else Dispatcher.Invoke(Redraw);
    }

    private void Redraw()
    {
        PlotCanvas.Children.Clear();
        var w = ActualWidth;
        var h = ActualHeight;
        if (w < 40 || h < 40) return;

        var left = MarginLeft;
        var right = w - MarginRight;
        var top = MarginTop;
        var bottom = h - MarginBottom;
        var plotW = right - left;
        var plotH = bottom - top;
        if (plotW <= 0 || plotH <= 0) return;

        var mode = Mode ?? "Rssi";
        var isThroughput = mode.Equals("Throughput", StringComparison.OrdinalIgnoreCase);
        var times = new List<DateTimeOffset>();
        var primary = new List<double>();
        var secondary = new List<double>();
        var hasSecondary = mode.Equals("Speed", StringComparison.OrdinalIgnoreCase) || isThroughput;

        if (Samples is not null)
        {
            foreach (var o in Samples)
            {
                switch (o)
                {
                    case WifiSignalSample sig:
                        times.Add(sig.Timestamp); primary.Add(sig.RssiDbm); break;
                    case WifiSpeedSample sp:
                        times.Add(sp.Timestamp);
                        primary.Add(isThroughput ? sp.RxThroughputMbps : sp.RxRateMbps);
                        secondary.Add(isThroughput ? sp.TxThroughputMbps : sp.TxRateMbps);
                        break;
                    case MonitoringSample ms:
                        times.Add(ms.Timestamp); primary.Add(ms.LatencyMs ?? 0); break;
                }
            }
        }

        var lblBrush = new SolidColorBrush(Color.FromRgb(0x64, 0x74, 0x8B));

        if (primary.Count == 0)
        {
            AddCentered(left + plotW / 2, h / 2, "Waiting for live data…", "#475569", 11);
            return;
        }

        var (accent, accent2) = mode.ToLowerInvariant() switch
        {
            "speed" => (Color.FromRgb(0x34, 0xD3, 0x99), Color.FromRgb(0xFB, 0xBF, 0x24)),
            "throughput" => (Color.FromRgb(0x14, 0xB8, 0xA6), Color.FromRgb(0xF5, 0x9E, 0x0B)),
            "ping" => (Color.FromRgb(0x3B, 0x82, 0xF6), Colors.Transparent),
            _ => (Color.FromRgb(0x38, 0xBD, 0xF8), Colors.Transparent),   // RSSI
        };

        // Y range: pad the data window so a near-flat series still shows real jitter.
        var all = hasSecondary ? primary.Concat(secondary).ToList() : primary;
        var lo = all.Min();
        var hi = all.Max();
        var span = hi - lo;
        var pad = span < 1e-6 ? Math.Max(1, Math.Abs(hi) * 0.1) : span * 0.18;
        lo -= pad;
        hi += pad;
        var range = Math.Max(hi - lo, 1e-6);

        var n = primary.Count;
        double X(int i) => left + (n <= 1 ? plotW : (double)i / (n - 1) * plotW);
        double Y(double v) => bottom - (v - lo) / range * plotH;

        DrawSeries(primary, X, Y, bottom, accent, isPrimary: true);
        if (hasSecondary && secondary.Count > 0)
        {
            DrawSeries(secondary, X, Y, bottom, accent2, isPrimary: false);
        }

        // Real wall-clock ticks: oldest / middle / newest.
        void TimeTick(int idx, double anchorX, TextAlignment align)
        {
            if (idx < 0 || idx >= times.Count) return;
            var tb = new TextBlock
            {
                Text = times[idx].ToLocalTime().ToString("HH:mm:ss"),
                Foreground = lblBrush, FontSize = 9,
            };
            tb.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var x = align switch
            {
                TextAlignment.Left => anchorX,
                TextAlignment.Right => anchorX - tb.DesiredSize.Width,
                _ => anchorX - tb.DesiredSize.Width / 2,
            };
            Canvas.SetLeft(tb, Math.Clamp(x, 0, w - tb.DesiredSize.Width));
            Canvas.SetTop(tb, bottom + 3);
            PlotCanvas.Children.Add(tb);
        }
        TimeTick(0, left, TextAlignment.Left);
        TimeTick(n / 2, left + plotW / 2, TextAlignment.Center);
        TimeTick(n - 1, right, TextAlignment.Right);
    }

    private void DrawSeries(List<double> values, Func<int, double> x, Func<double, double> y,
        double baseline, Color color, bool isPrimary)
    {
        var n = values.Count;

        if (isPrimary)
        {
            // Soft gradient area under the primary line (apex colour → transparent base).
            var area = new Polygon
            {
                Fill = new LinearGradientBrush(
                    Color.FromArgb(0x5C, color.R, color.G, color.B),
                    Color.FromArgb(0x00, color.R, color.G, color.B),
                    new Point(0, 0), new Point(0, 1)),
            };
            var apts = new PointCollection(n + 2) { new(x(0), baseline) };
            for (var i = 0; i < n; i++) apts.Add(new Point(x(i), y(values[i])));
            apts.Add(new Point(x(n - 1), baseline));
            area.Points = apts;
            PlotCanvas.Children.Add(area);
        }

        if (n >= 2)
        {
            var line = new Polyline
            {
                Stroke = new SolidColorBrush(color),
                StrokeThickness = isPrimary ? 1.4 : 1.2,
                StrokeLineJoin = PenLineJoin.Round,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                Opacity = isPrimary ? 1.0 : 0.85,
            };
            var pts = new PointCollection(n);
            for (var i = 0; i < n; i++) pts.Add(new Point(x(i), y(values[i])));
            line.Points = pts;
            PlotCanvas.Children.Add(line);
        }

        // Bright dot on the most-recent sample — the "live" cue.
        PlotCanvas.Children.Add(new Ellipse
        {
            Width = 5, Height = 5, Fill = new SolidColorBrush(color),
            RenderTransform = new TranslateTransform(x(n - 1) - 2.5, y(values[n - 1]) - 2.5),
        });
    }

    private void AddCentered(double cx, double cy, string text, string hex, double size)
    {
        var tb = new TextBlock
        {
            Text = text,
            Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)),
            FontSize = size,
        };
        tb.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Canvas.SetLeft(tb, cx - tb.DesiredSize.Width / 2);
        Canvas.SetTop(tb, cy - tb.DesiredSize.Height / 2);
        PlotCanvas.Children.Add(tb);
    }
}
