namespace NetScopeDiagnosticCenter.Core.Monitoring;

/// <summary>
/// Creates per-ViewModel <see cref="IMonitoringSession"/> instances.
/// Replaces the v1 plan's monolithic singleton MonitoringService — different modules
/// can now monitor different targets concurrently without state collisions.
/// </summary>
public interface IMonitoringSessionFactory
{
    /// <summary>
    /// Creates a new session that probes once per <paramref name="intervalMs"/> and
    /// retains at most <paramref name="maxSamples"/> samples (older samples are dropped).
    /// </summary>
    IMonitoringSession Create(int intervalMs = 1000, int maxSamples = 300);
}
