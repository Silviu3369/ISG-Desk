using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace NetScopeDiagnosticCenter.UI.Controls;

/// <summary>
/// Circular health-score gauge: 0–100 score rendered as a coloured arc on a light track,
/// with the numeric score and status label centred. Used on Technician Home and Diagnosis.
/// </summary>
public partial class HealthGaugeControl : UserControl
{
    // Geometry constants — must match the XAML (Width/Height, StartPoint, ArcSegment.Size).
    private const double Center = 80;
    private const double Radius = 70;

    public HealthGaugeControl()
    {
        InitializeComponent();
        UpdateGeometry();
    }

    public static readonly DependencyProperty ScoreProperty = DependencyProperty.Register(
        nameof(Score),
        typeof(int?),
        typeof(HealthGaugeControl),
        new PropertyMetadata(null, OnScoreChanged));

    public static readonly DependencyProperty StatusProperty = DependencyProperty.Register(
        nameof(Status),
        typeof(string),
        typeof(HealthGaugeControl),
        new PropertyMetadata(string.Empty, OnStatusChanged));

    /// <summary>0–100 health score. <c>null</c> renders the empty placeholder.</summary>
    public int? Score
    {
        get => (int?)GetValue(ScoreProperty);
        set => SetValue(ScoreProperty, value);
    }

    /// <summary>Health status label (e.g. "Healthy", "Good", "Warning", "Critical").</summary>
    public string Status
    {
        get => (string)GetValue(StatusProperty);
        set => SetValue(StatusProperty, value);
    }

    private static void OnScoreChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is HealthGaugeControl gauge) gauge.UpdateGeometry();
    }

    private static void OnStatusChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is HealthGaugeControl gauge) gauge.StatusText.Text = string.IsNullOrWhiteSpace(gauge.Status) ? "No data" : gauge.Status;
    }

    private void UpdateGeometry()
    {
        var score = Score;
        if (!score.HasValue || score < 1)
        {
            // No measurement yet — render a faint full ring as placeholder.
            ScoreText.Text = "—";
            ArcPath.Stroke = new SolidColorBrush(Color.FromRgb(226, 232, 240)); // slate-200
            ArcSegment.Point = new Point(Center, Center - Radius + 0.01); // near full circle
            ArcSegment.IsLargeArc = true;
            return;
        }

        var clamped = Math.Clamp(score.Value, 0, 100);
        ScoreText.Text = clamped.ToString();

        // -90° starts at 12 o'clock; sweep 3.6° per percent clockwise.
        var angleDeg = -90 + (clamped * 3.6);
        var angleRad = angleDeg * Math.PI / 180;
        var endX = Center + Radius * Math.Cos(angleRad);
        var endY = Center + Radius * Math.Sin(angleRad);

        ArcSegment.Point = new Point(endX, endY);
        ArcSegment.IsLargeArc = clamped > 50;
        ArcPath.Stroke = new SolidColorBrush(ColorForScore(clamped));
    }

    /// <summary>Gauge colour bands aligned with HealthScoreCalculator.ToStatus thresholds.</summary>
    private static Color ColorForScore(int score) => score switch
    {
        >= 90 => Color.FromRgb(34, 197, 94),    // green-500 — Healthy
        >= 70 => Color.FromRgb(59, 130, 246),   // blue-500 — Good
        >= 40 => Color.FromRgb(245, 158, 11),   // amber-500 — Warning
        _     => Color.FromRgb(239, 68, 68)     // red-500 — Critical
    };
}
