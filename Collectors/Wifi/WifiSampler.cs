using System.Threading.Channels;
using NetScopeDiagnosticCenter.Core.Models.Wifi;
using NetScopeDiagnosticCenter.Core.Wifi;

namespace NetScopeDiagnosticCenter.Collectors.Wifi;

/// <summary>
/// Polls the active Wi-Fi connection at 1 Hz (Decision 3 = A) and produces
/// <see cref="WifiSignalSample"/> + <see cref="WifiSpeedSample"/> events.
///
/// <para>
/// Drives the live monitors in Sections 2 (mini strip), 5 (Strength), 6 (Speed).
/// The ViewModel subscribes to <see cref="SignalSampled"/> + <see cref="SpeedSampled"/>
/// and accumulates into bounded ObservableCollections (60 samples = 60 seconds rolling).
/// </para>
///
/// <para>
/// Lifecycle: <see cref="Start"/> when the user opens the Wi-Fi page; <see cref="Stop"/>
/// when navigating away or on app shutdown. <see cref="Dispose"/> ensures the timer
/// is cleaned up. Re-startable.
/// </para>
///
/// <para>
/// Why a Timer + lock instead of Task.Run loop: Timer guarantees uniform 1Hz cadence
/// even if the polling work takes &gt; 1s (rare). The lock ensures we never have two
/// concurrent samples in flight (which would race on the WLAN handle).
/// </para>
/// </summary>
public sealed class WifiSampler : IDisposable
{
    private readonly IWifiScanner _scanner;
    private readonly TimeProvider _time;
    private readonly TimeSpan _interval;

    private readonly object _stateLock = new();
    private System.Threading.Timer? _timer;
    private Guid? _adapterId;
    private bool _disposed;
    private int _polling;       // Interlocked guard: 0 = idle, 1 = sample in flight

    // Previous NIC byte counters + the instant they were read, for the throughput delta.
    // -1 = "no baseline yet" → first sample reports 0 (we can't know the rate from one read).
    private long _prevRxBytes = -1;
    private long _prevTxBytes = -1;
    private long _prevCountersTicks;

    /// <summary>Fired on every successful RSSI sample. Subscribers run on a thread-pool thread.</summary>
    public event EventHandler<WifiSignalSample>? SignalSampled;

    /// <summary>Fired on every successful link-rate sample. Subscribers run on a thread-pool thread.</summary>
    public event EventHandler<WifiSpeedSample>? SpeedSampled;

    /// <summary>
    /// Fired once per tick with the freshly-queried connection (the same query already used
    /// for the speed sample — no extra cost). Lets the VM detect roaming at 1 Hz instead of
    /// only on a manual scan, so AP hand-offs are caught in real time.
    /// </summary>
    public event EventHandler<NetScopeDiagnosticCenter.Core.Models.Wifi.WifiConnectionDetails>? ConnectionSampled;

    /// <summary>True between Start() and Stop().</summary>
    public bool IsRunning => _timer is not null;

    public WifiSampler(IWifiScanner scanner, TimeProvider? time = null, TimeSpan? interval = null)
    {
        _scanner = scanner ?? throw new ArgumentNullException(nameof(scanner));
        _time = time ?? TimeProvider.System;
        _interval = interval ?? TimeSpan.FromSeconds(1);   // Decision 3 = A: uniform 1 Hz
    }

    /// <summary>
    /// Begin sampling the given adapter. Idempotent — calling Start while already running
    /// just retargets to the new adapter (used when user changes Wi-Fi adapter mid-session).
    /// </summary>
    public void Start(Guid adapterId)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(WifiSampler));

        lock (_stateLock)
        {
            _adapterId = adapterId;
            // Drop any stale counter baseline so the first throughput sample of a new
            // session is 0, not a giant spike computed against an hours-old reading.
            _prevRxBytes = -1;
            _prevTxBytes = -1;
            if (_timer is null)
            {
                _timer = new System.Threading.Timer(OnTick, state: null,
                    dueTime: TimeSpan.Zero, period: _interval);
            }
        }
    }

    /// <summary>
    /// Stop sampling. Safe to call multiple times. Pending samples may still fire briefly
    /// (timer disposes asynchronously) but no new ticks are scheduled.
    /// </summary>
    public void Stop()
    {
        lock (_stateLock)
        {
            _timer?.Dispose();
            _timer = null;
            _adapterId = null;
        }
    }

    private void OnTick(object? _)
    {
        // Skip if a previous sample is still in flight (would happen only if WLAN call hangs
        // for &gt;1s). Interlocked is faster than lock for the common idle path.
        if (System.Threading.Interlocked.Exchange(ref _polling, 1) == 1) return;

        try
        {
            Guid? adapter;
            lock (_stateLock)
            {
                adapter = _adapterId;
            }
            if (adapter is null) return;

            var rssi = _scanner.GetCurrentRssiDbm(adapter.Value);
            if (rssi.HasValue)
            {
                var sample = new WifiSignalSample(
                    Timestamp: _time.GetUtcNow().ToLocalTime(),
                    RssiDbm: rssi.Value,
                    Level: WifiFrequencyHelper.ClassifySignal(rssi));
                // Off-thread invocation (we're already on a thread-pool thread from the timer).
                // Subscribers must marshal to UI thread themselves (typical pattern in this app).
                SignalSampled?.Invoke(this, sample);
            }

            // Re-query the full connection for link rates. This is heavier than just RSSI
            // but link rates only change on rate-adaptation (~1 Hz natural cadence anyway).
            var conn = _scanner.GetCurrentConnection(adapter.Value);
            if (conn is { IsConnected: true })
            {
                // Roaming detection needs the BSSID every tick, independent of whether the
                // driver reported link rates this cycle.
                ConnectionSampled?.Invoke(this, conn);
            }
            if (conn is { IsConnected: true, RxRateMbps: not null, TxRateMbps: not null })
            {
                var (rxThroughput, txThroughput) = ReadThroughputMbps(adapter.Value);
                var sample = new WifiSpeedSample(
                    Timestamp: _time.GetUtcNow().ToLocalTime(),
                    RxRateMbps: conn.RxRateMbps.Value,
                    TxRateMbps: conn.TxRateMbps.Value,
                    RxThroughputMbps: rxThroughput,
                    TxThroughputMbps: txThroughput);
                SpeedSampled?.Invoke(this, sample);
            }
        }
        catch
        {
            // Sampling errors are non-fatal — silently skip this tick. WLAN failures bubble
            // up as null returns; any other exception (e.g. handle unexpectedly closed)
            // we just suppress to avoid crashing the UI thread via timer callback.
        }
        finally
        {
            System.Threading.Interlocked.Exchange(ref _polling, 0);
        }
    }

    /// <summary>
    /// Real throughput in Mbps for this tick: (Δbytes × 8) ÷ Δseconds ÷ 1e6, separately
    /// for receive and transmit, from the Wi-Fi NIC's cumulative byte counters. The first
    /// call after Start() only establishes the baseline and returns (0, 0) — you cannot
    /// derive a rate from a single cumulative reading. Counter resets / wraps (negative
    /// delta) clamp to 0 rather than producing a nonsense spike.
    /// </summary>
    private (double Rx, double Tx) ReadThroughputMbps(Guid adapterId)
    {
        var counters = Core.Wifi.WifiAdapterInfo.GetTrafficCounters(adapterId);
        if (counters is null) return (0, 0);

        var (rxBytes, txBytes) = counters.Value;
        var nowTicks = System.Diagnostics.Stopwatch.GetTimestamp();

        long dRx, dTx;
        double elapsedSec;
        // The baseline fields are also written by Start() (adapter switch / restart). The
        // _polling guard only serialises tick-vs-tick, not tick-vs-Start, so guard the
        // read-modify-write under _stateLock. The slow part (NIC enumeration above) is
        // deliberately OUTSIDE the lock so Start() never blocks on it.
        lock (_stateLock)
        {
            if (_prevRxBytes < 0 || _prevTxBytes < 0)
            {
                _prevRxBytes = rxBytes;
                _prevTxBytes = txBytes;
                _prevCountersTicks = nowTicks;
                return (0, 0);
            }

            elapsedSec = (nowTicks - _prevCountersTicks) / (double)System.Diagnostics.Stopwatch.Frequency;
            dRx = rxBytes - _prevRxBytes;
            dTx = txBytes - _prevTxBytes;

            _prevRxBytes = rxBytes;
            _prevTxBytes = txBytes;
            _prevCountersTicks = nowTicks;
        }

        if (elapsedSec <= 0) return (0, 0);

        var rxMbps = dRx > 0 ? dRx * 8.0 / elapsedSec / 1_000_000.0 : 0;
        var txMbps = dTx > 0 ? dTx * 8.0 / elapsedSec / 1_000_000.0 : 0;
        return (rxMbps, txMbps);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}
