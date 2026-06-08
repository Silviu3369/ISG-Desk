using System.Net.NetworkInformation;
using NetScopeDiagnosticCenter.Core.Models;

namespace NetScopeDiagnosticCenter.Core;

public sealed class LinkQualityPingService
{
    private static readonly TimeSpan DelayBetweenSamples = TimeSpan.FromMilliseconds(150);

    public Task<LinkQualityPingResult> RunAsync(
        string target,
        int sampleCount,
        CancellationToken cancellationToken = default) =>
        RunAsync(target, sampleCount, LinkQualityPingCategory.Target, LinkQualityThresholds.Default, cancellationToken);

    public async Task<LinkQualityPingResult> RunAsync(
        string target,
        int sampleCount,
        LinkQualityPingCategory category = LinkQualityPingCategory.Target,
        LinkQualityThresholds? thresholds = null,
        CancellationToken cancellationToken = default)
    {
        thresholds ??= LinkQualityThresholds.Default;
        var thresholdProfile = BuildThresholdProfile(category, thresholds);
        if (!DiagnosticTargetValidator.TryNormalizeHost(target, out var normalizedTarget, out var validationReason))
        {
            return new LinkQualityPingResult
            {
                Target = string.Empty,
                Category = category.ToString(),
                ThresholdProfile = thresholdProfile,
                Status = "Warning",
                Details = $"{validationReason} Ping test was not started."
            };
        }

        target = normalizedTarget;
        var sent = Math.Clamp(sampleCount, 1, 50);
        var received = 0;
        var latencies = new List<double>();

        using var ping = new Ping();
        for (var i = 0; i < sent; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var reply = await ping.SendPingAsync(target, thresholds.PingTimeoutMs)
                    .WaitAsync(TimeSpan.FromMilliseconds(thresholds.PingTimeoutMs + 300), cancellationToken);

                if (reply.Status == IPStatus.Success)
                {
                    received++;
                    latencies.Add(reply.RoundtripTime);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // A failed sample is counted as lost. Later samples may still succeed.
            }

            if (i < sent - 1)
            {
                await Task.Delay(DelayBetweenSamples, cancellationToken);
            }
        }

        var loss = Math.Round(((sent - received) / (double)sent) * 100, 1);
        var average = latencies.Count > 0 ? Math.Round(latencies.Average(), 1) : (double?)null;
        var minimum = latencies.Count > 0 ? Math.Round(latencies.Min(), 1) : (double?)null;
        var maximum = latencies.Count > 0 ? Math.Round(latencies.Max(), 1) : (double?)null;
        var jitter = CalculateJitter(latencies);

        return new LinkQualityPingResult
        {
            Target = target,
            Category = category.ToString(),
            ThresholdProfile = thresholdProfile,
            Status = Classify(loss, average, jitter, received, category, thresholds),
            Sent = sent,
            Received = received,
            LossPercent = loss,
            MinimumLatencyMs = minimum,
            MaximumLatencyMs = maximum,
            AverageLatencyMs = average,
            JitterMs = jitter,
            Details = $"{received}/{sent} replies; loss {loss:N1}%; avg {Format(average)}; jitter {Format(jitter)}."
        };
    }

    private static double? CalculateJitter(IReadOnlyList<double> latencies)
    {
        if (latencies.Count < 2)
        {
            return latencies.Count == 1 ? 0 : null;
        }

        var deltas = new List<double>();
        for (var i = 1; i < latencies.Count; i++)
        {
            deltas.Add(Math.Abs(latencies[i] - latencies[i - 1]));
        }

        return Math.Round(deltas.Average(), 1);
    }

    private static string Classify(
        double lossPercent,
        double? averageLatencyMs,
        double? jitterMs,
        int received,
        LinkQualityPingCategory category,
        LinkQualityThresholds thresholds)
    {
        var warningLatency = category switch
        {
            LinkQualityPingCategory.Gateway => thresholds.GatewayWarningLatencyMs,
            LinkQualityPingCategory.Internet => thresholds.InternetWarningLatencyMs,
            _ => thresholds.TargetWarningLatencyMs
        };
        var criticalLatency = category switch
        {
            LinkQualityPingCategory.Gateway => thresholds.GatewayCriticalLatencyMs,
            LinkQualityPingCategory.Internet => thresholds.InternetCriticalLatencyMs,
            _ => thresholds.TargetCriticalLatencyMs
        };
        var criticalLoss = category == LinkQualityPingCategory.Internet
            ? thresholds.CriticalInternetPacketLossPercent
            : thresholds.CriticalLocalPacketLossPercent;

        if (received == 0 ||
            lossPercent > criticalLoss ||
            averageLatencyMs > criticalLatency ||
            jitterMs > thresholds.CriticalJitterMs)
        {
            return "Critical";
        }

        if (lossPercent > thresholds.WarningPacketLossPercent ||
            averageLatencyMs > warningLatency ||
            jitterMs > thresholds.WarningJitterMs)
        {
            return "Warning";
        }

        return "OK";
    }

    private static string Format(double? value) =>
        value.HasValue ? $"{value.Value:N1} ms" : "Unknown";

    private static string BuildThresholdProfile(LinkQualityPingCategory category, LinkQualityThresholds thresholds)
    {
        var warningLatency = category switch
        {
            LinkQualityPingCategory.Gateway => thresholds.GatewayWarningLatencyMs,
            LinkQualityPingCategory.Internet => thresholds.InternetWarningLatencyMs,
            _ => thresholds.TargetWarningLatencyMs
        };
        var criticalLatency = category switch
        {
            LinkQualityPingCategory.Gateway => thresholds.GatewayCriticalLatencyMs,
            LinkQualityPingCategory.Internet => thresholds.InternetCriticalLatencyMs,
            _ => thresholds.TargetCriticalLatencyMs
        };
        var criticalLoss = category == LinkQualityPingCategory.Internet
            ? thresholds.CriticalInternetPacketLossPercent
            : thresholds.CriticalLocalPacketLossPercent;

        return $"{category}: warn > {warningLatency:N0} ms; critical > {criticalLatency:N0} ms or loss > {criticalLoss:N1}%.";
    }
}
