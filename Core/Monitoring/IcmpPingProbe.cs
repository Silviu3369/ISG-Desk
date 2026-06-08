using System.Net.NetworkInformation;

namespace NetScopeDiagnosticCenter.Core.Monitoring;

/// <summary>
/// ICMP-based probe using <see cref="Ping"/>. Latency thresholds are taken from
/// <see cref="DiagnosticConstants.DefaultPingTimeoutMs"/> and a fixed warning band.
/// </summary>
public sealed class IcmpPingProbe : IPingProbe
{
    /// <summary>Latencies at or above this trigger a "Warning" status even on success.</summary>
    public const int WarningLatencyMs = 100;

    /// <summary>Latencies at or above this trigger a "Critical" status even on success.</summary>
    public const int CriticalLatencyMs = 250;

    private readonly int _timeoutMs;

    public IcmpPingProbe(int timeoutMs = DiagnosticConstants.DefaultPingTimeoutMs)
    {
        _timeoutMs = timeoutMs;
    }

    public async Task<MonitoringSample> SendAsync(string target, CancellationToken cancellationToken)
    {
        var timestamp = DateTimeOffset.Now;
        if (string.IsNullOrWhiteSpace(target))
        {
            return new MonitoringSample(timestamp, Success: false, LatencyMs: null, Status: "Critical");
        }

        try
        {
            using var ping = new Ping();
            // Pass cancellationToken explicitly so consumers can cancel mid-flight.
            var reply = await ping.SendPingAsync(target, _timeoutMs).WaitAsync(cancellationToken);
            return reply.Status == IPStatus.Success
                ? new MonitoringSample(
                    Timestamp: timestamp,
                    Success: true,
                    LatencyMs: reply.RoundtripTime,
                    Status: ClassifyLatency(reply.RoundtripTime))
                : new MonitoringSample(timestamp, Success: false, LatencyMs: null, Status: "Critical");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Probe must never throw outward — surface as a failed sample instead.
            return new MonitoringSample(timestamp, Success: false, LatencyMs: null, Status: "Critical");
        }
    }

    public static string ClassifyLatency(long latencyMs) => latencyMs switch
    {
        >= CriticalLatencyMs => "Critical",
        >= WarningLatencyMs => "Warning",
        _ => "OK"
    };
}
