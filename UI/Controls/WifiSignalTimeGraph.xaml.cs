using System.Collections;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using NetScopeDiagnosticCenter.Core.Models.Wifi;

namespace NetScopeDiagnosticCenter.UI.Controls;

/// <summary>
/// Multi-network RSSI-over-time graph. One coloured series per BSSID, with colours shared
/// with Channel Graph so the same network keeps the same visual identity.
/// </summary>
public partial class WifiSignalTimeGraph : UserControl
{
    private const double MarginLeft = 36;
    private const double MarginRight = 14;
    private const double MarginTop = 12;
    private const double MarginBottom = 24;

    private readonly HashSet<WifiSignalTrend> _subscribedTrends = new();

    public WifiSignalTimeGraph()
    {
        InitializeComponent();
        SizeChanged += (_, _) => Redraw();
        Loaded += (_, _) => Redraw();
        Unloaded += (_, _) => ClearTrendSubscriptions();
    }

    public static readonly DependencyProperty TrendsProperty = DependencyProperty.Register(
        nameof(Trends), typeof(IEnumerable), typeof(WifiSignalTimeGraph),
        new PropertyMetadata(null, OnTrendsChanged));

    public IEnumerable? Trends
    {
        get => (IEnumerable?)GetValue(TrendsProperty);
        set => SetValue(TrendsProperty, value);
    }

    private static void OnTrendsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var graph = (WifiSignalTimeGraph)d;
        if (e.OldValue is INotifyCollectionChanged oldCollection)
        {
            oldCollection.CollectionChanged -= graph.OnCollectionChanged;
        }

        if (e.NewValue is INotifyCollectionChanged newCollection)
        {
            newCollection.CollectionChanged += graph.OnCollectionChanged;
        }

        graph.SyncTrendSubscriptions();
        graph.Redraw();
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        SyncTrendSubscriptions();
        if (Dispatcher.CheckAccess()) Redraw();
        else Dispatcher.Invoke(Redraw);
    }

    private void SyncTrendSubscriptions()
    {
        var current = new HashSet<WifiSignalTrend>();
        if (Trends is not null)
        {
            foreach (var item in Trends)
            {
                if (item is WifiSignalTrend trend)
                {
                    current.Add(trend);
                }
            }
        }

        foreach (var trend in _subscribedTrends.Except(current).ToList())
        {
            trend.PropertyChanged -= OnTrendItemChanged;
            _subscribedTrends.Remove(trend);
        }

        foreach (var trend in current.Except(_subscribedTrends))
        {
            trend.PropertyChanged += OnTrendItemChanged;
            _subscribedTrends.Add(trend);
        }
    }

    private void ClearTrendSubscriptions()
    {
        foreach (var trend in _subscribedTrends)
        {
            trend.PropertyChanged -= OnTrendItemChanged;
        }

        _subscribedTrends.Clear();
    }

    private void OnTrendItemChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(WifiSignalTrend.IsVisible)) return;
        if (Dispatcher.CheckAccess()) Redraw();
        else Dispatcher.Invoke(Redraw);
    }

    private void Redraw()
    {
        PlotCanvas.Children.Clear();

        var width = ActualWidth;
        var height = ActualHeight;
        if (width < 80 || height < 60)
        {
            return;
        }

        var left = MarginLeft;
        var right = width - MarginRight;
        var top = MarginTop;
        var bottom = height - MarginBottom;
        var plotWidth = right - left;
        var plotHeight = bottom - top;
        if (plotWidth <= 0 || plotHeight <= 0)
        {
            return;
        }

        DrawGrid(left, right, top, bottom);

        var series = GetVisibleSeries();
        if (series.Count == 0)
        {
            AddCentered(
                left + plotWidth / 2,
                height / 2,
                "No history yet - scan a few times or enable Auto every 30s",
                "#64748B",
                12);
            return;
        }

        var maxLength = series.Max(s => s.RssiHistory.Count);

        foreach (var trend in series)
        {
            DrawTrendSeries(trend, maxLength, left, right, top, bottom);
        }

        AddXAxisLabel("oldest", left, bottom + 6, TextAlignment.Left);
        AddXAxisLabel("latest", right, bottom + 6, TextAlignment.Right);
    }

    private List<WifiSignalTrend> GetVisibleSeries()
    {
        var series = new List<WifiSignalTrend>();
        if (Trends is null)
        {
            return series;
        }

        foreach (var item in Trends)
        {
            if (item is WifiSignalTrend trend && trend.IsVisible && trend.RssiHistory.Count > 0)
            {
                series.Add(trend);
            }
        }

        series.Sort((a, b) =>
        {
            if (a.IsCurrentConnection != b.IsCurrentConnection)
            {
                return a.IsCurrentConnection ? 1 : -1;
            }

            return (a.LatestRssi ?? -100).CompareTo(b.LatestRssi ?? -100);
        });
        return series;
    }

    private void DrawGrid(double left, double right, double top, double bottom)
    {
        var axis = new SolidColorBrush(Color.FromRgb(0x33, 0x41, 0x55));
        var label = new SolidColorBrush(Color.FromRgb(0x94, 0xA3, 0xB8));

        for (var dbm = -30; dbm >= -90; dbm -= 15)
        {
            var y = WifiSignalTimeGraphScale.RssiToY(dbm, top, bottom);
            PlotCanvas.Children.Add(new Line
            {
                X1 = left,
                X2 = right,
                Y1 = y,
                Y2 = y,
                Stroke = axis,
                StrokeThickness = 0.5,
            });
            AddText($"{dbm}", 4, y - 8, label, 9);
        }

        foreach (var ratio in new[] { 0.0, 0.5, 1.0 })
        {
            var x = left + ratio * (right - left);
            PlotCanvas.Children.Add(new Line
            {
                X1 = x,
                X2 = x,
                Y1 = top,
                Y2 = bottom,
                Stroke = axis,
                StrokeThickness = 0.3,
            });
        }
    }

    private void DrawTrendSeries(
        WifiSignalTrend trend,
        int maxLength,
        double left,
        double right,
        double top,
        double bottom)
    {
        var points = WifiSignalTimeGraphScale.BuildSeriesPoints(
            trend.RssiHistory,
            maxLength,
            left,
            right,
            top,
            bottom);
        if (points.Count == 0)
        {
            return;
        }

        var color = WifiColorPalette.ForBssid(trend.Bssid);
        var brush = new SolidColorBrush(color);

        if (points.Count >= 2)
        {
            var line = new Polyline
            {
                Stroke = brush,
                StrokeThickness = trend.IsCurrentConnection ? 2.8 : 1.7,
                StrokeLineJoin = PenLineJoin.Round,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                Opacity = trend.IsCurrentConnection ? 1.0 : 0.9,
                Points = new PointCollection(points),
            };
            PlotCanvas.Children.Add(line);
        }

        var latest = points[^1];
        PlotCanvas.Children.Add(new Ellipse
        {
            Width = trend.IsCurrentConnection ? 7 : 5,
            Height = trend.IsCurrentConnection ? 7 : 5,
            Fill = brush,
            Stroke = new SolidColorBrush(Color.FromRgb(0x0F, 0x17, 0x2A)),
            StrokeThickness = 1,
            RenderTransform = new TranslateTransform(
                latest.X - (trend.IsCurrentConnection ? 3.5 : 2.5),
                latest.Y - (trend.IsCurrentConnection ? 3.5 : 2.5)),
        });
    }

    private void AddText(string text, double x, double y, Brush brush, double size)
    {
        var textBlock = new TextBlock { Text = text, Foreground = brush, FontSize = size };
        Canvas.SetLeft(textBlock, x);
        Canvas.SetTop(textBlock, y);
        PlotCanvas.Children.Add(textBlock);
    }

    private void AddXAxisLabel(string text, double anchorX, double y, TextAlignment alignment)
    {
        var labelBrush = new SolidColorBrush(Color.FromRgb(0x64, 0x74, 0x8B));
        var textBlock = new TextBlock { Text = text, Foreground = labelBrush, FontSize = 9 };
        textBlock.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

        var x = alignment == TextAlignment.Right
            ? anchorX - textBlock.DesiredSize.Width
            : anchorX;
        Canvas.SetLeft(textBlock, Math.Clamp(x, 0, Math.Max(0, ActualWidth - textBlock.DesiredSize.Width)));
        Canvas.SetTop(textBlock, y);
        PlotCanvas.Children.Add(textBlock);
    }

    private void AddCentered(double x, double y, string text, string hex, double size)
    {
        var textBlock = new TextBlock
        {
            Text = text,
            Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)),
            FontSize = size,
            MaxWidth = Math.Max(140, ActualWidth - MarginLeft - MarginRight - 24),
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
        };
        textBlock.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Canvas.SetLeft(textBlock, x - textBlock.DesiredSize.Width / 2);
        Canvas.SetTop(textBlock, y - textBlock.DesiredSize.Height / 2);
        PlotCanvas.Children.Add(textBlock);
    }
}
