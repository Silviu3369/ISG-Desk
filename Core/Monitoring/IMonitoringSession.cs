using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Media;

namespace NetScopeDiagnosticCenter.Core.Monitoring;

/// <summary>
/// Live ping session against a single target. Owned and disposed by a single
/// ViewModel; do not share across modules — request a fresh session from the factory.
/// </summary>
public interface IMonitoringSession : INotifyPropertyChanged, IDisposable
{
    /// <summary>Starts probing the target. If already running, the previous target is stopped first.</summary>
    Task StartAsync(string target, CancellationToken cancellationToken = default);

    /// <summary>Stops the current probe loop. Idempotent.</summary>
    void Stop();

    /// <summary>Most recent target supplied to <see cref="StartAsync"/>.</summary>
    string CurrentTarget { get; }

    /// <summary>True between <see cref="StartAsync"/> and <see cref="Stop"/>.</summary>
    bool IsRunning { get; }

    /// <summary>
    /// Live samples capped at the factory-supplied maximum. The collection is
    /// safe for WPF binding via <c>BindingOperations.EnableCollectionSynchronization</c>.
    /// </summary>
    ObservableCollection<MonitoringSample> Samples { get; }

    /// <summary>Aggregated stats refreshed on every sample.</summary>
    MonitoringSummary Summary { get; }

    /// <summary>Most recent sample, or <c>null</c> before the first probe completes.</summary>
    MonitoringSample? LastSample { get; }

    /// <summary>"OK" / "Warning" / "Critical" of the last sample, or "Unknown" before any.</summary>
    string LastStatus { get; }

    /// <summary>Round-trip latency of the last sample (ms), or <c>null</c> when it failed.</summary>
    double? LastLatencyMs { get; }

    /// <summary>
    /// Frozen polyline points for the live sparkline (180×24 px footprint). Re-projected on
    /// every sample so a WPF Polyline.Points binding stays in sync.
    /// </summary>
    PointCollection SparklinePoints { get; }

    /// <summary>Fires after every sample is appended (UI thread when a SyncContext was captured).</summary>
    event EventHandler<MonitoringSample>? SampleReceived;

    /// <summary>Fires when the rolling status bucket changes (OK ⇄ Warning ⇄ Critical).</summary>
    event EventHandler<string>? StatusChanged;
}
