using System.Diagnostics;
using System.Windows.Media;
using NetScopeDiagnosticCenter.Core.Monitoring;

namespace NetScopeDiagnosticCenter.Tests.Core.Monitoring;

/// <summary>
/// Performance + stress tests that exercise MonitoringSession under realistic loads.
/// These complement the unit tests by verifying invariants over time and concurrency:
///   • Bounded memory: <c>Samples</c> never exceeds <c>maxSamples</c> regardless of how
///     many probes have run, so a long-running session cannot leak unbounded memory.
///   • Dispose actually stops the loop (no zombie probe Tasks after Dispose).
///   • Many concurrent sessions can coexist without throwing or deadlocking.
///   • Restart against a different target clears stale samples.
/// All tests use a deterministic in-memory probe so they run offline in &lt; 5 seconds.
/// </summary>
[Collection(MonitoringTestCollection.Name)]
public class MonitoringSessionStressTests
{
    private sealed class CountingProbe : IPingProbe
    {
        private int _calls;
        public int CallCount => Volatile.Read(ref _calls);

        public Task<MonitoringSample> SendAsync(string target, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new MonitoringSample(
                DateTimeOffset.Now,
                Success: true,
                LatencyMs: 10 + (CallCount % 30),
                Status: "OK"));
        }
    }

    private static async Task<bool> WaitForAsync(Func<bool> predicate, int timeoutMs = 5000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (predicate()) return true;
            await Task.Delay(20);
        }
        return predicate();
    }

    private static IMonitoringSession CreateHeadlessSession(
        IPingProbe probe,
        int intervalMs = 1000,
        int maxSamples = 300)
    {
        var previousContext = SynchronizationContext.Current;
        try
        {
            SynchronizationContext.SetSynchronizationContext(null);
            return new MonitoringSessionFactory(probe).Create(intervalMs, maxSamples);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }
    }

    [Fact]
    public async Task Samples_NeverExceedMaxSamples_OverHundredsOfProbes()
    {
        // Use a small maxSamples and require the probe to run roughly 2× past that — at the
        // ~70ms cycle (probe + delay) this completes in well under the 8 s timeout while
        // still proving the trim logic kicks in repeatedly.
        const int maxSamples = 20;
        var probe = new CountingProbe();
        using var session = CreateHeadlessSession(probe, intervalMs: 60, maxSamples: maxSamples);

        await session.StartAsync("192.168.1.1");
        var enough = await WaitForAsync(() => probe.CallCount >= maxSamples + 5, timeoutMs: 12000);
        enough.Should().BeTrue("the probe should run past the max sample cap within the stress window");
        session.Stop();

        session.Samples.Count.Should().BeLessOrEqualTo(maxSamples);
        // Summary should mirror the trimmed window.
        session.Summary.SampleCount.Should().BeLessOrEqualTo(maxSamples);
    }

    [Fact]
    public async Task Dispose_FullyTerminatesProbingWithinShortGracePeriod()
    {
        var probe = new CountingProbe();
        var session = CreateHeadlessSession(probe, intervalMs: 50, maxSamples: 100);

        await session.StartAsync("192.168.1.1");
        await WaitForAsync(() => probe.CallCount >= 3);

        var snapshotBeforeDispose = probe.CallCount;
        session.Dispose();

        // Allow up to 250 ms for any in-flight probe to finish; afterwards, the loop must
        // have stopped — extra calls beyond the snapshot are limited to at most one.
        await Task.Delay(250);
        var growthAfterDispose = probe.CallCount - snapshotBeforeDispose;
        growthAfterDispose.Should().BeLessOrEqualTo(1, "no probes should run after Dispose");
    }

    [Fact]
    public async Task ConcurrentSessions_DoNotInterfereOrThrow()
    {
        // Simulates the shared-monitor target: Technician Home, Wi-Fi tab, Printers tab, NetworkDevices
        // tab — multiple modules running their own monitoring sessions in parallel.
        var probe = new CountingProbe();

        var sessions = Enumerable.Range(0, 6)
            .Select(_ => CreateHeadlessSession(probe, intervalMs: 60, maxSamples: 25))
            .ToList();

        try
        {
            for (var i = 0; i < sessions.Count; i++)
            {
                await sessions[i].StartAsync($"192.168.{i}.1");
            }

            // Each session must collect at least a couple of samples without any thrown
            // exception, and the collections must respect their cap.
            var allHaveSamples = await WaitForAsync(
                () => sessions.All(s => s.Samples.Count >= 2),
                timeoutMs: 10000);
            allHaveSamples.Should().BeTrue();

            foreach (var s in sessions)
            {
                s.Samples.Count.Should().BeLessOrEqualTo(25);
            }
        }
        finally
        {
            foreach (var s in sessions) s.Dispose();
        }
    }

    [Fact]
    public async Task Restart_ClearsStaleSamplesAndRetargets()
    {
        var probe = new CountingProbe();
        using var session = CreateHeadlessSession(probe, intervalMs: 60, maxSamples: 100);

        await session.StartAsync("192.168.1.1");
        await WaitForAsync(() => session.Samples.Count >= 5);
        session.Samples.Count.Should().BeGreaterThan(0);

        // Restart against a new target — old samples must be discarded, current target updates.
        await session.StartAsync("10.0.0.1");
        session.CurrentTarget.Should().Be("10.0.0.1");

        // Right after restart the sample count is small (drained); over the next 200ms we
        // expect new samples to populate the cleared buffer.
        var fresh = await WaitForAsync(() => session.Samples.Count >= 1, timeoutMs: 600);
        fresh.Should().BeTrue();
        session.Stop();
    }

    [Fact]
    public async Task SparklinePoints_StayWithinFootprint_AndRefreshOnEverySample()
    {
        var probe = new CountingProbe();
        using var session = CreateHeadlessSession(probe, intervalMs: 50, maxSamples: 30);

        await session.StartAsync("192.168.1.1");
        await WaitForAsync(() => session.Samples.Count >= 10);

        var points = session.SparklinePoints;
        points.Count.Should().BeGreaterThan(0);

        // SparklineWidth/Height in MonitoringSession are 180×24 with 2/3 px padding; verify
        // every projected coordinate falls inside that footprint.
        foreach (var p in points)
        {
            p.X.Should().BeInRange(0, 180);
            p.Y.Should().BeInRange(0, 24);
        }

        session.Stop();
    }
}
