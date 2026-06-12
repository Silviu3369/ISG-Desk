using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using NetScopeDiagnosticCenter.UI;

namespace NetScopeDiagnosticCenter.Core.Monitoring;

/// <summary>
/// Default <see cref="IMonitoringSession"/> implementation. Probes on a background loop,
/// marshals events back to the captured <see cref="SynchronizationContext"/> when present,
/// and uses <see cref="BindingOperations.EnableCollectionSynchronization"/> so the live
/// <see cref="Samples"/> collection is safe to bind from WPF.
/// </summary>
internal sealed class MonitoringSession : ObservableObject, IMonitoringSession
{
    private readonly IPingProbe _probe;
    private readonly int _intervalMs;
    private readonly int _maxSamples;
    private readonly object _samplesLock = new();
    private readonly SynchronizationContext? _syncContext;

    private CancellationTokenSource? _cts;
    private Task? _runner;
    private string _currentTarget = string.Empty;
    private bool _isRunning;
    private MonitoringSummary _summary = MonitoringSummary.Empty;
    private string _lastStatus = "OK";
    private MonitoringSample? _lastSample;
    private PointCollection _sparklinePoints = new();

    // Sparkline rendering parameters — values that fit a 180×24 px footprint.
    private const double SparklineWidth = 180;
    private const double SparklineHeight = 24;
    private const double SparklinePaddingX = 2;
    private const double SparklinePaddingY = 3;

    public MonitoringSession(IPingProbe probe, int intervalMs, int maxSamples)
    {
        if (intervalMs < 50) throw new ArgumentOutOfRangeException(nameof(intervalMs), "Interval must be at least 50ms.");
        if (maxSamples < 1) throw new ArgumentOutOfRangeException(nameof(maxSamples), "MaxSamples must be at least 1.");

        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
        _intervalMs = intervalMs;
        _maxSamples = maxSamples;
        // Capture only the real WPF Dispatcher context. Test runners can install their
        // own SynchronizationContext after Application.Current exists; posting through
        // that makes SampleReceived/StatusChanged timing-dependent without helping WPF.
        _syncContext = SynchronizationContext.Current is DispatcherSynchronizationContext
            ? SynchronizationContext.Current
            : null;

        Samples = [];
        // Required so WPF can read the collection from the UI thread while we
        // mutate it from the background loop. Lock object must be the same we
        // use for AddSample/Trim.
        BindingOperations.EnableCollectionSynchronization(Samples, _samplesLock);
    }

    public ObservableCollection<MonitoringSample> Samples { get; }

    public string CurrentTarget
    {
        get => _currentTarget;
        private set => SetProperty(ref _currentTarget, value);
    }

    public bool IsRunning
    {
        get => _isRunning;
        private set => SetProperty(ref _isRunning, value);
    }

    public MonitoringSummary Summary
    {
        get => _summary;
        private set => SetProperty(ref _summary, value);
    }

    /// <summary>The most recent sample, useful for binding "last status" indicators.</summary>
    public MonitoringSample? LastSample
    {
        get => _lastSample;
        private set
        {
            if (SetProperty(ref _lastSample, value))
            {
                OnPropertyChanged(nameof(LastStatus));
                OnPropertyChanged(nameof(LastLatencyMs));
            }
        }
    }

    /// <summary>"OK"/"Warning"/"Critical" of the last sample, or "Unknown" before any.</summary>
    public string LastStatus => _lastSample?.Status ?? "Unknown";

    /// <summary>Last successful round-trip latency or <c>null</c> when the last sample failed.</summary>
    public double? LastLatencyMs => _lastSample?.LatencyMs;

    /// <summary>
    /// Polyline points for a 180×24 px sparkline (latest samples on the right).
    /// Re-projected on every sample so a WPF Polyline binding stays live.
    /// </summary>
    public PointCollection SparklinePoints
    {
        get => _sparklinePoints;
        private set => SetProperty(ref _sparklinePoints, value);
    }

    public event EventHandler<MonitoringSample>? SampleReceived;
    public event EventHandler<string>? StatusChanged;

    public async Task StartAsync(string target, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(target))
        {
            throw new ArgumentException("Target must be a non-empty hostname or IP.", nameof(target));
        }

        Stop();

        CurrentTarget = target;
        ClearSamples();
        _lastStatus = "OK";

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = _cts.Token;
        IsRunning = true;
        _runner = Task.Run(() => RunLoopAsync(token), token);

        // Allow caller to await the StartAsync completion only — the loop runs in background.
        await Task.CompletedTask;
    }

    public void Stop()
    {
        if (_cts is null)
        {
            return;
        }

        try { _cts.Cancel(); } catch { /* ignore double-cancel */ }
        try { _cts.Dispose(); } catch { /* ignore */ }
        _cts = null;
        IsRunning = false;
    }

    public void Dispose()
    {
        Stop();
        WaitForRunnerToExit();
        // Note: BindingOperations.EnableCollectionSynchronization is auto-cleaned
        // when the collection becomes unreferenced; no explicit Disable required.
    }

    private void WaitForRunnerToExit()
    {
        try
        {
            _runner?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException ex) when (ex.InnerExceptions.All(inner => inner is OperationCanceledException or TaskCanceledException))
        {
            // Expected during cancellation.
        }
        catch (ObjectDisposedException)
        {
            // The cancellation source may already be disposed by an idempotent Stop/Dispose path.
        }
        finally
        {
            _runner = null;
        }
    }

    private async Task RunLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            MonitoringSample? sample = null;
            try
            {
                sample = await _probe.SendAsync(CurrentTarget, token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // Probe contract is to never throw, but defend anyway.
                sample = new MonitoringSample(DateTimeOffset.Now, Success: false, LatencyMs: null, Status: "Critical");
            }

            try
            {
                if (sample is not null)
                {
                    AppendSample(sample);
                    NotifySampleReceived(sample);
                    if (!string.Equals(sample.Status, _lastStatus, StringComparison.OrdinalIgnoreCase))
                    {
                        _lastStatus = sample.Status;
                        NotifyStatusChanged(sample.Status);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // Keep the always-on monitor alive even if a UI projection or event
                // subscriber fails for one tick.
            }

            try { await Task.Delay(_intervalMs, token); }
            catch (OperationCanceledException) { break; }
        }
    }

    private void AppendSample(MonitoringSample sample)
    {
        List<MonitoringSample> snapshot;
        lock (_samplesLock)
        {
            Samples.Add(sample);
            while (Samples.Count > _maxSamples)
            {
                Samples.RemoveAt(0);
            }
            snapshot = Samples.ToList();
        }

        // Build the new PointCollection on the background thread, then *freeze* it. A frozen
        // Freezable carries no thread affinity and can be assigned to a UI Dependency target
        // from any thread without WPF throwing "Must create DependencySource on same Thread".
        var sparkline = BuildSparkline(snapshot);
        if (sparkline.CanFreeze) sparkline.Freeze();
        var summary = MonitoringSummary.From(snapshot);

        // Marshal property updates onto the captured UI SynchronizationContext. This keeps
        // INotifyPropertyChanged events (consumed by WPF bindings) on the UI thread, which is
        // required for any binding target that touches a Freezable / DependencyObject.
        Marshal(() =>
        {
            Summary = summary;
            LastSample = sample;
            SparklinePoints = sparkline;
        });
    }

    private void ClearSamples()
    {
        lock (_samplesLock)
        {
            Samples.Clear();
            Summary = MonitoringSummary.Empty;
        }
        LastSample = null;
        // Frozen so StartAsync can be invoked from any thread (e.g. the network-change
        // retarget path) without handing WPF a thread-affine Freezable.
        var empty = new PointCollection();
        if (empty.CanFreeze) empty.Freeze();
        SparklinePoints = empty;
    }

    /// <summary>
    /// Projects the supplied samples to <see cref="PointCollection"/> coordinates fitting
    /// the sparkline footprint. Failed samples are drawn at the bottom (latency = 0).
    /// </summary>
    private static PointCollection BuildSparkline(IReadOnlyList<MonitoringSample> snapshot)
    {
        var points = new PointCollection();
        if (snapshot.Count == 0)
        {
            return points;
        }

        var latencies = snapshot.Select(s => s.LatencyMs ?? 0).ToList();
        var max = latencies.DefaultIfEmpty(0).Max();
        if (max < 1) max = 1;

        var width = SparklineWidth - 2 * SparklinePaddingX;
        var height = SparklineHeight - 2 * SparklinePaddingY;
        var divisor = Math.Max(snapshot.Count - 1, 1);

        for (var i = 0; i < snapshot.Count; i++)
        {
            var x = SparklinePaddingX + width * i / divisor;
            var ratio = Math.Min(1.0, latencies[i] / max);
            var y = SparklinePaddingY + height - height * ratio;
            points.Add(new Point(x, y));
        }
        return points;
    }

    private void NotifySampleReceived(MonitoringSample sample)
    {
        var handler = SampleReceived;
        if (handler is null) return;
        Marshal(() => handler(this, sample));
    }

    private void NotifyStatusChanged(string status)
    {
        var handler = StatusChanged;
        if (handler is null) return;
        Marshal(() => handler(this, status));
    }

    /// <summary>
    /// Posts onto the captured SynchronizationContext when present (UI thread in WPF).
    /// In headless contexts (tests, console) the action runs inline — sufficient because
    /// EnableCollectionSynchronization ensures the underlying collection stays consistent.
    /// </summary>
    private void Marshal(Action action)
    {
        if (_syncContext is null)
        {
            action();
        }
        else
        {
            _syncContext.Post(_ => action(), null);
        }
    }
}
