namespace NetScopeDiagnosticCenter.Core.Monitoring;

/// <summary>
/// Default factory. Hands out fresh <see cref="MonitoringSession"/> instances bound
/// to the supplied <see cref="IPingProbe"/>. The probe itself is shared (singleton);
/// each session is independent state.
/// </summary>
public sealed class MonitoringSessionFactory : IMonitoringSessionFactory
{
    private readonly IPingProbe _probe;

    public MonitoringSessionFactory(IPingProbe probe)
    {
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
    }

    public IMonitoringSession Create(int intervalMs = 1000, int maxSamples = 300)
    {
        return new MonitoringSession(_probe, intervalMs, maxSamples);
    }
}
