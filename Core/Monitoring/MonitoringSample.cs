namespace NetScopeDiagnosticCenter.Core.Monitoring;

/// <summary>
/// One probe taken during a monitoring session.
/// </summary>
/// <param name="Timestamp">When the probe completed (local time, with offset).</param>
/// <param name="Success">Whether the probe reached the target.</param>
/// <param name="LatencyMs">Round-trip time when successful; <c>null</c> when not.</param>
/// <param name="Status">Severity bucket: <c>OK</c>, <c>Warning</c>, <c>Critical</c>.</param>
public sealed record MonitoringSample(
    DateTimeOffset Timestamp,
    bool Success,
    double? LatencyMs,
    string Status);
