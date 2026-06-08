namespace NetScopeDiagnosticCenter.Core.Monitoring;

/// <summary>
/// Single-probe abstraction. Implementations must never throw; failures are
/// reported through <see cref="MonitoringSample.Success"/> and <see cref="MonitoringSample.Status"/>.
/// </summary>
public interface IPingProbe
{
    /// <summary>
    /// Sends a single ping probe to <paramref name="target"/> and returns its outcome.
    /// </summary>
    Task<MonitoringSample> SendAsync(string target, CancellationToken cancellationToken);
}
