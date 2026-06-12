namespace NetScopeDiagnosticCenter.Core.Monitoring;

/// <summary>
/// Aggregated statistics across the live sample window of a monitoring session.
/// </summary>
public sealed record MonitoringSummary(
    int SampleCount,
    int SuccessCount,
    int FailureCount,
    double LossPercent,
    double? AverageLatencyMs,
    double? MinLatencyMs,
    double? MaxLatencyMs,
    double? JitterMs,
    TimeSpan Duration)
{
    public static MonitoringSummary Empty { get; } = new(
        SampleCount: 0,
        SuccessCount: 0,
        FailureCount: 0,
        LossPercent: 0,
        AverageLatencyMs: null,
        MinLatencyMs: null,
        MaxLatencyMs: null,
        JitterMs: null,
        Duration: TimeSpan.Zero);

    /// <summary>
    /// Computes a summary across the supplied samples. The samples enumeration is
    /// materialized once, so it is safe to pass a snapshot taken under a lock.
    /// </summary>
    public static MonitoringSummary From(IReadOnlyList<MonitoringSample> samples)
    {
        if (samples.Count == 0)
        {
            return Empty;
        }

        var successCount = 0;
        var failureCount = 0;
        var latencies = new List<double>(samples.Count);
        foreach (var sample in samples)
        {
            if (sample.Success && sample.LatencyMs.HasValue)
            {
                successCount++;
                latencies.Add(sample.LatencyMs.Value);
            }
            else
            {
                failureCount++;
            }
        }

        var loss = samples.Count == 0 ? 0 : Math.Round(failureCount / (double)samples.Count * 100, 1);
        double? average = latencies.Count > 0 ? Math.Round(latencies.Average(), 1) : null;
        double? min = latencies.Count > 0 ? Math.Round(latencies.Min(), 1) : null;
        double? max = latencies.Count > 0 ? Math.Round(latencies.Max(), 1) : null;
        double? jitter = ComputeJitter(latencies);
        var duration = samples[^1].Timestamp - samples[0].Timestamp;

        return new MonitoringSummary(
            SampleCount: samples.Count,
            SuccessCount: successCount,
            FailureCount: failureCount,
            LossPercent: loss,
            AverageLatencyMs: average,
            MinLatencyMs: min,
            MaxLatencyMs: max,
            JitterMs: jitter,
            Duration: duration);
    }

    /// <summary>
    /// Sidebar loss text. Honest before the first sample — the old binding fallback
    /// claimed "loss 0%" while the monitor had measured nothing yet.
    /// Invariant culture so the dot decimal separator is stable across locales.
    /// </summary>
    public string LossDisplay => SampleCount == 0
        ? "waiting for samples"
        : FormattableString.Invariant($"loss {LossPercent:N0}%");

    /// <summary>Multi-line tooltip with the whole window's statistics.</summary>
    public string TooltipText => SampleCount == 0
        ? "No samples yet — the gateway monitor is waiting for its first ping."
        : FormattableString.Invariant($"Window: {SampleCount} sample(s) over {Duration.TotalSeconds:N0} s\n") +
          FormattableString.Invariant($"Latency: avg {FormatMs(AverageLatencyMs)} · min {FormatMs(MinLatencyMs)} · max {FormatMs(MaxLatencyMs)}\n") +
          FormattableString.Invariant($"Jitter: {FormatMs(JitterMs)} · loss {LossPercent:N1}% ({FailureCount} failed)");

    private static string FormatMs(double? value) => value.HasValue
        ? FormattableString.Invariant($"{value.Value:N1} ms")
        : "—";

    /// <summary>
    /// Mean absolute difference between consecutive latencies — a simple proxy for jitter.
    /// </summary>
    private static double? ComputeJitter(IReadOnlyList<double> latencies)
    {
        if (latencies.Count < 2)
        {
            return null;
        }

        var deltaSum = 0d;
        for (var i = 1; i < latencies.Count; i++)
        {
            deltaSum += Math.Abs(latencies[i] - latencies[i - 1]);
        }
        return Math.Round(deltaSum / (latencies.Count - 1), 1);
    }
}
