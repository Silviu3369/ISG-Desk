using System.Collections;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using NetScopeDiagnosticCenter.Core.Models.Wifi;

namespace NetScopeDiagnosticCenter.UI.Controls;

/// <summary>
/// Spectrum-style channel graph. X axis is Wi-Fi channel/frequency, Y axis is signal
/// strength in dBm. Each AP is drawn as a translucent bell curve centered on its channel.
/// </summary>
public partial class WifiChannelGraph : UserControl
{
    private const double RssiTop = -20;
    private const double RssiFloor = -90;

    private const double MarginLeft = 36;
    private const double MarginRight = 12;
    private const double MarginTop = 16;
    private const double MarginBottom = 24;

    public WifiChannelGraph()
    {
        InitializeComponent();
        SizeChanged += (_, _) => Redraw();
        Loaded += (_, _) => Redraw();
    }

    public static readonly DependencyProperty AccessPointsProperty = DependencyProperty.Register(
        nameof(AccessPoints), typeof(IEnumerable), typeof(WifiChannelGraph),
        new PropertyMetadata(null, OnAccessPointsChanged));

    public IEnumerable? AccessPoints
    {
        get => (IEnumerable?)GetValue(AccessPointsProperty);
        set => SetValue(AccessPointsProperty, value);
    }

    public static readonly DependencyProperty BandProperty = DependencyProperty.Register(
        nameof(Band), typeof(WifiBand), typeof(WifiChannelGraph),
        new PropertyMetadata(WifiBand.TwoPointFourGhz, (d, _) => ((WifiChannelGraph)d).Redraw()));

    public WifiBand Band
    {
        get => (WifiBand)GetValue(BandProperty);
        set => SetValue(BandProperty, value);
    }

    private (int Lo, int Hi) ChannelRange => WifiChannelGraphScale.GetChannelRange(Band);

    private static void OnAccessPointsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var graph = (WifiChannelGraph)d;
        if (e.OldValue is INotifyCollectionChanged oldCollection)
        {
            oldCollection.CollectionChanged -= graph.OnCollectionChanged;
        }

        if (e.NewValue is INotifyCollectionChanged newCollection)
        {
            newCollection.CollectionChanged += graph.OnCollectionChanged;
        }

        graph.Redraw();
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (Dispatcher.CheckAccess()) Redraw();
        else Dispatcher.Invoke(Redraw);
    }

    private void Redraw()
    {
        PlotCanvas.Children.Clear();

        var width = ActualWidth;
        var height = ActualHeight;
        if (width < 60 || height < 60)
        {
            return;
        }

        var plotLeft = MarginLeft;
        var plotRight = width - MarginRight;
        var plotTop = MarginTop;
        var plotBottom = height - MarginBottom;
        var plotWidth = plotRight - plotLeft;
        var plotHeight = plotBottom - plotTop;
        if (plotWidth <= 0 || plotHeight <= 0)
        {
            return;
        }

        var (channelLow, channelHigh) = ChannelRange;
        var channelSpan = Math.Max(1, channelHigh - channelLow);

        double ChannelToX(double channel) => plotLeft + (channel - channelLow) / channelSpan * plotWidth;
        double RssiToY(double dbm)
        {
            var clamped = Math.Clamp(dbm, RssiFloor, RssiTop);
            var ratio = (clamped - RssiFloor) / (RssiTop - RssiFloor);
            return plotBottom - ratio * plotHeight;
        }

        DrawGrid(plotLeft, plotRight, plotTop, plotBottom, channelLow, channelHigh, ChannelToX, RssiToY);

        if (AccessPoints is null)
        {
            DrawCenteredText(width / 2, height / 2, "No scan data yet - press Scan Networks", "#64748B", 12);
            return;
        }

        var aps = new List<WifiAccessPoint>();
        foreach (var item in AccessPoints)
        {
            if (item is WifiAccessPoint ap && ap.Band == Band && ap.Channel > 0)
            {
                aps.Add(ap);
            }
        }

        if (aps.Count == 0)
        {
            DrawCenteredText(width / 2, height / 2, "No networks on this band - press Scan Networks", "#64748B", 12);
            return;
        }

        aps.Sort((a, b) =>
        {
            if (a.IsCurrentConnection != b.IsCurrentConnection)
            {
                return a.IsCurrentConnection ? 1 : -1;
            }

            return a.RssiDbm.CompareTo(b.RssiDbm);
        });

        foreach (var ap in aps)
        {
            DrawApHump(ap, ChannelToX, RssiToY, plotBottom);
        }

        var labelled = aps
            .GroupBy(a => a.Channel)
            .Select(g => g.OrderByDescending(a => a.IsCurrentConnection)
                          .ThenByDescending(a => a.RssiDbm)
                          .First())
            .OrderByDescending(a => a.IsCurrentConnection)
            .ThenByDescending(a => a.RssiDbm)
            .ToList();

        var placedLabels = new List<Rect>();
        foreach (var ap in labelled)
        {
            DrawApLabel(ap, ChannelToX, RssiToY, plotTop, plotBottom, placedLabels);
        }
    }

    private void DrawGrid(
        double left,
        double right,
        double top,
        double bottom,
        int channelLow,
        int channelHigh,
        Func<double, double> channelToX,
        Func<double, double> rssiToY)
    {
        var axisBrush = new SolidColorBrush(Color.FromRgb(0x33, 0x41, 0x55));
        var labelBrush = new SolidColorBrush(Color.FromRgb(0x94, 0xA3, 0xB8));

        for (var dbm = -20; dbm >= -90; dbm -= 20)
        {
            var y = rssiToY(dbm);
            PlotCanvas.Children.Add(new Line
            {
                X1 = left,
                X2 = right,
                Y1 = y,
                Y2 = y,
                Stroke = axisBrush,
                StrokeThickness = 0.5,
            });
            AddText($"{dbm}", 4, y - 8, labelBrush, 9);
        }

        foreach (var channel in WifiChannelGraphScale.GetAxisChannels(Band))
        {
            if (channel < channelLow || channel > channelHigh)
            {
                continue;
            }

            var x = channelToX(channel);
            PlotCanvas.Children.Add(new Line
            {
                X1 = x,
                X2 = x,
                Y1 = top,
                Y2 = bottom,
                Stroke = axisBrush,
                StrokeThickness = 0.3,
            });
            AddText($"{channel}", x - 6, bottom + 4, labelBrush, 9);
        }
    }

    private void DrawApHump(
        WifiAccessPoint ap,
        Func<double, double> channelToX,
        Func<double, double> rssiToY,
        double floorY)
    {
        var color = WifiColorPalette.ForBssid(ap.Bssid);
        var stroke = new SolidColorBrush(color);
        var halfChannels = WifiChannelGraphScale.GetHalfChannelWidth(Band, ap.ChannelWidthMhz);

        var leftX = channelToX(ap.Channel - halfChannels);
        var rightX = channelToX(ap.Channel + halfChannels);
        var apexY = rssiToY(ap.RssiDbm);

        const int samples = 48;
        var points = new PointCollection(samples + 1);
        for (var i = 0; i <= samples; i++)
        {
            var t = (double)i / samples;
            var x = leftX + t * (rightX - leftX);
            var u = (t - 0.5) * 2.0;
            var bell = Math.Cos(u * Math.PI / 2.0);
            bell *= bell;
            var y = floorY - (floorY - apexY) * bell;
            points.Add(new Point(x, y));
        }

        var geometry = new PathGeometry();
        var figure = new PathFigure { StartPoint = new Point(leftX, floorY), IsClosed = true };
        figure.Segments.Add(new PolyLineSegment(points, true));
        figure.Segments.Add(new LineSegment(new Point(leftX, floorY), false));
        geometry.Figures.Add(figure);

        var fill = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(0, 1),
            GradientStops =
            {
                new GradientStop(Color.FromArgb(0x40, color.R, color.G, color.B), 0.0),
                new GradientStop(Color.FromArgb(0x14, color.R, color.G, color.B), 0.55),
                new GradientStop(Color.FromArgb(0x00, color.R, color.G, color.B), 1.0),
            },
        };

        var path = new Path
        {
            Data = geometry,
            Stroke = stroke,
            StrokeThickness = ap.IsCurrentConnection ? 2.0 : 1.1,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeStartLineCap = PenLineCap.Round,
            Fill = fill,
            SnapsToDevicePixels = false,
        };
        path.SetValue(RenderOptions.EdgeModeProperty, EdgeMode.Unspecified);
        PlotCanvas.Children.Add(path);
    }

    private void DrawApLabel(
        WifiAccessPoint ap,
        Func<double, double> channelToX,
        Func<double, double> rssiToY,
        double plotTop,
        double plotBottom,
        List<Rect> placed)
    {
        var color = WifiColorPalette.ForBssid(ap.Bssid);
        var label = FormatNetworkLabel(ap);

        var textBlock = new TextBlock
        {
            Text = label,
            Foreground = new SolidColorBrush(color),
            FontSize = ap.IsCurrentConnection ? 12 : 10,
            FontWeight = ap.IsCurrentConnection ? FontWeights.Bold : FontWeights.SemiBold,
        };
        textBlock.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

        var textWidth = textBlock.DesiredSize.Width;
        var textHeight = textBlock.DesiredSize.Height;
        var centerX = channelToX(ap.Channel);
        var apexY = rssiToY(ap.RssiDbm);

        var maxX = Math.Max(2, ActualWidth - textWidth - 2);
        var x = Math.Clamp(centerX - textWidth / 2, 2, maxX);
        var y = Math.Clamp(apexY - textHeight - 2, plotTop, Math.Max(plotTop, plotBottom - textHeight));

        var rect = new Rect(x, y, textWidth, textHeight);
        var guard = 0;
        while (placed.Any(existing => existing.IntersectsWith(rect)) && guard++ < 12)
        {
            y -= textHeight + 2;
            if (y < plotTop)
            {
                y = apexY + 2;
            }

            y = Math.Clamp(y, plotTop, Math.Max(plotTop, plotBottom - textHeight));
            rect = new Rect(x, y, textWidth, textHeight);
        }

        placed.Add(rect);
        Canvas.SetLeft(textBlock, x);
        Canvas.SetTop(textBlock, y);
        PlotCanvas.Children.Add(textBlock);
    }

    private static string FormatNetworkLabel(WifiAccessPoint ap)
    {
        var label = ap.DisplaySsid;
        if (label.Length > 16)
        {
            label = label[..15] + "...";
        }

        return ap.IsCurrentConnection ? "* " + label : label;
    }

    private void AddText(string text, double x, double y, Brush brush, double size)
    {
        var textBlock = new TextBlock { Text = text, Foreground = brush, FontSize = size };
        Canvas.SetLeft(textBlock, x);
        Canvas.SetTop(textBlock, y);
        PlotCanvas.Children.Add(textBlock);
    }

    private void DrawCenteredText(double x, double y, string text, string hex, double size)
    {
        var textBlock = new TextBlock
        {
            Text = text,
            Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)),
            FontSize = size,
            MaxWidth = Math.Max(120, ActualWidth - MarginLeft - MarginRight - 24),
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
        };
        textBlock.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Canvas.SetLeft(textBlock, x - textBlock.DesiredSize.Width / 2);
        Canvas.SetTop(textBlock, y - textBlock.DesiredSize.Height / 2);
        PlotCanvas.Children.Add(textBlock);
    }
}
