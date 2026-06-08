using System.Collections;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using NetScopeDiagnosticCenter.Core.Models.Wifi;

namespace NetScopeDiagnosticCenter.UI.Controls;

/// <summary>
/// Live REAL-throughput monitor for ONE direction (Receive or Transmit), driven by the
/// adapter's byte counters (Δbytes×8÷Δt), not the PHY rate. Filled area + line so idle
/// reads as a flat baseline near zero and real traffic reads as obvious spikes — the
/// exact opposite of the old paired-column PHY chart the user (rightly) found unreadable.
///
/// <para>
/// Single series by design: the user asked for separate Receive / Transmit monitors. Two
/// instances of this control (Series="Rx" / "Tx") give two independent, clearly-labelled
/// graphs. Y auto-scales and switches Kbps↔Mbps so a 50 KB/s background trickle and a
/// 300 Mbps download are both legible. X axis is the real wall clock (HH:mm:ss).
/// </para>
/// </summary>
public partial class WifiThroughputGraph : UserControl
{
    private const double MarginLeft = 52;
    private const double MarginRight = 12;
    private const double MarginTop = 14;
    private const double MarginBottom = 22;

    public WifiThroughputGraph()
    {
        InitializeComponent();
        SizeChanged += (_, _) => Redraw();
        Loaded += (_, _) => Redraw();
    }

    public static readonly DependencyProperty SamplesProperty = DependencyProperty.Register(
        nameof(Samples), typeof(IEnumerable), typeof(WifiThroughputGraph),
        new PropertyMetadata(null, OnSamplesChanged));

    public IEnumerable? Samples
    {
        get => (IEnumerable?)GetValue(SamplesProperty);
        set => SetValue(SamplesProperty, value);
    }

    /// <summary>"Rx" → receive/download, "Tx" → transmit/upload. Default "Rx".</summary>
    public static readonly DependencyProperty SeriesProperty = DependencyProperty.Register(
        nameof(Series), typeof(string), typeof(WifiThroughputGraph),
        new PropertyMetadata("Rx", (d, _) => ((WifiThroughputGraph)d).Redraw()));

    public string Series
    {
        get => (string)GetValue(SeriesProperty);
        set => SetValue(SeriesProperty, value);
    }

    private bool IsTx => string.Equals(Series, "Tx", StringComparison.OrdinalIgnoreCase);

    private static void OnSamplesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var g = (WifiThroughputGraph)d;
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
        if (w < 80 || h < 60) return;

        var left = MarginLeft;
        var right = w - MarginRight;
        var top = MarginTop;
        var bottom = h - MarginBottom;
        var plotW = right - left;
        var plotH = bottom - top;
        if (plotW <= 0 || plotH <= 0) return;

        var tx = IsTx;
        var values = new List<double>();
        var times = new List<DateTimeOffset>();
        if (Samples is not null)
            foreach (var o in Samples)
                if (o is WifiSpeedSample sp)
                {
                    values.Add(tx ? sp.TxThroughputMbps : sp.RxThroughputMbps);
                    times.Add(sp.Timestamp);
                }

        var axis = new SolidColorBrush(Color.FromRgb(0x33, 0x41, 0x55));
        var lblBrush = new SolidColorBrush(Color.FromRgb(0x94, 0xA3, 0xB8));
        var accent = tx
            ? Color.FromRgb(0xFB, 0xBF, 0x24)   // amber  — transmit / upload
            : Color.FromRgb(0x34, 0xD3, 0x99);  // emerald — receive / download
        var lineBrush = new SolidColorBrush(accent);
        var dirLabel = tx ? "Transfer Rate" : "Receive Rate";

        if (values.Count == 0)
        {
            AddCentered(left + plotW / 2, h / 2,
                "No data yet - Start Analyzer to sample traffic", "#64748B", 12);
            return;
        }

        // --- Smart units: keep the scale legible from KB/s trickle to Gb/s burst. ---
        var maxMbps = Math.Max(values.Max(), 0.0);
        bool useKbps = maxMbps < 1.0;            // < 1 Mbps → show in Kbps
        double Scale(double mbps) => useKbps ? mbps * 1000.0 : mbps;
        var unit = useKbps ? "Kbps" : "Mbps";

        var dispMax = NiceCeiling(Math.Max(Scale(maxMbps), useKbps ? 10 : 1));
        double YFor(double mbps) => bottom - Math.Clamp(Scale(mbps) / dispMax, 0, 1) * plotH;

        // Horizontal gridlines + value labels.
        for (var g = 0; g <= 4; g++)
        {
            var val = dispMax * g / 4.0;
            var y = bottom - g / 4.0 * plotH;
            PlotCanvas.Children.Add(new Line
            {
                X1 = left, X2 = right, Y1 = y, Y2 = y, Stroke = axis, StrokeThickness = 0.5,
            });
            AddText(val >= 100 ? $"{val:N0}" : $"{val:0.#}", 4, y - 8, lblBrush, 9);
        }

        var n = values.Count;
        var slot = plotW / n;
        // Slim, evenly-gapped bars — a hairline gap keeps it elegant at 60 samples and the
        // bar never gets clunky (capped at 10 px) nor invisible (min 2 px).
        var barW = Math.Clamp(slot * 0.62, 2.0, 10.0);
        double XCenter(int i) => left + (i + 0.5) * slot;

        for (var i = 0; i < n; i++)
        {
            var yTop = YFor(values[i]);
            var bh = Math.Max(0, bottom - yTop);
            var bar = new System.Windows.Shapes.Rectangle
            {
                Width = barW,
                Height = bh,
                RadiusX = 1.5,
                RadiusY = 1.5,
                // Subtle vertical gradient (brighter cap → softer base) = the "elegant"
                // look, not a flat slab.
                Fill = new LinearGradientBrush(
                    Color.FromArgb(0xFF, accent.R, accent.G, accent.B),
                    Color.FromArgb(0x66, accent.R, accent.G, accent.B),
                    new Point(0, 0), new Point(0, 1)),
            };
            Canvas.SetLeft(bar, XCenter(i) - barW / 2);
            Canvas.SetTop(bar, yTop);
            PlotCanvas.Children.Add(bar);
        }
        double XFor(int i) => XCenter(i);

        // Dashed session-average line.
        var avg = values.Average();
        PlotCanvas.Children.Add(new Line
        {
            X1 = left, X2 = right, Y1 = YFor(avg), Y2 = YFor(avg),
            Stroke = lineBrush, StrokeThickness = 1, Opacity = 0.55,
            StrokeDashArray = new DoubleCollection { 4, 3 },
        });

        // Time axis: 5 ticks, real wall-clock HH:mm:ss + faint vertical gridlines.
        const int ticks = 4;
        for (var k = 0; k <= ticks; k++)
        {
            var idx = Math.Clamp((int)Math.Round((double)k / ticks * (n - 1)), 0, n - 1);
            var gx = XFor(idx);
            PlotCanvas.Children.Add(new Line
            {
                X1 = gx, X2 = gx, Y1 = top, Y2 = bottom,
                Stroke = axis, StrokeThickness = 0.5, Opacity = 0.45,
            });
            var t = times[idx].ToLocalTime().ToString("HH:mm:ss");
            var tb = new TextBlock { Text = t, Foreground = lblBrush, FontSize = 9 };
            tb.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var lx = k == 0 ? gx : k == ticks ? gx - tb.DesiredSize.Width : gx - tb.DesiredSize.Width / 2;
            Canvas.SetLeft(tb, Math.Clamp(lx, 0, w - tb.DesiredSize.Width));
            Canvas.SetTop(tb, bottom + 5);
            PlotCanvas.Children.Add(tb);
        }

        // Direction label, top-left.
        var dir = new TextBlock
        {
            Text = dirLabel, Foreground = lineBrush, FontSize = 11, FontWeight = FontWeights.SemiBold,
        };
        Canvas.SetLeft(dir, left + 4);
        Canvas.SetTop(dir, top + 2);
        PlotCanvas.Children.Add(dir);

        // Stats, top-right: live / average / peak in the chosen unit.
        var cur = new TextBlock
        {
            Text = $"Now {Fmt(Scale(values[^1]))} | Peak {Fmt(Scale(maxMbps))} {unit}",
            Foreground = new SolidColorBrush(Color.FromRgb(0xCB, 0xD5, 0xE1)),
            FontSize = 11, FontWeight = FontWeights.SemiBold,
        };
        cur.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Canvas.SetLeft(cur, Math.Max(left + 4, right - cur.DesiredSize.Width - 2));
        Canvas.SetTop(cur, top + 2);
        PlotCanvas.Children.Add(cur);

        static string Fmt(double v) => v >= 100 ? $"{v:N0}" : v >= 10 ? $"{v:0.#}" : $"{v:0.##}";
    }

    private void AddText(string t, double x, double y, Brush b, double size)
    {
        var tb = new TextBlock { Text = t, Foreground = b, FontSize = size };
        Canvas.SetLeft(tb, x);
        Canvas.SetTop(tb, y);
        PlotCanvas.Children.Add(tb);
    }

    private void AddCentered(double x, double y, string text, string hex, double size)
    {
        var tb = new TextBlock
        {
            Text = text,
            Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)),
            FontSize = size,
        };
        tb.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Canvas.SetLeft(tb, x - tb.DesiredSize.Width / 2);
        Canvas.SetTop(tb, y - tb.DesiredSize.Height / 2);
        PlotCanvas.Children.Add(tb);
    }

    /// <summary>Round a max value up to a clean axis bound (412 → 500, 8.6 → 10).</summary>
    private static double NiceCeiling(double v)
    {
        if (v <= 0) return 1;
        var mag = Math.Pow(10, Math.Floor(Math.Log10(v)));
        var norm = v / mag;
        var nice = norm <= 1 ? 1 : norm <= 2 ? 2 : norm <= 5 ? 5 : 10;
        return nice * mag;
    }
}
