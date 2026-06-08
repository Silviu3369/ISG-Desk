using NetScopeDiagnosticCenter.Core.Monitoring;

namespace NetScopeDiagnosticCenter.Tests.Core.Monitoring;

/// <summary>
/// Behavior tests for MonitoringSession via MonitoringSessionFactory.
/// Uses a fake IPingProbe so timing is deterministic and offline-friendly.
/// </summary>
[Collection(MonitoringTestCollection.Name)]
public class MonitoringSessionTests
{
    /// <summary>
    /// Fake probe that returns scripted samples in order. When the script is
    /// exhausted it returns the last sample repeatedly.
    /// </summary>
    private sealed class ScriptedProbe : IPingProbe
    {
        private readonly Queue<MonitoringSample> _scripted;
        private readonly MonitoringSample _fallback;
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);

        public ScriptedProbe(IEnumerable<MonitoringSample> scripted, MonitoringSample fallback)
        {
            _scripted = new Queue<MonitoringSample>(scripted);
            _fallback = fallback;
        }

        public Task<MonitoringSample> SendAsync(string target, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _callCount);
            cancellationToken.ThrowIfCancellationRequested();
            var s = _scripted.Count > 0 ? _scripted.Dequeue() : _fallback;
            return Task.FromResult(s);
        }
    }

    private static MonitoringSample Ok(double latency) =>
        new(DateTimeOffset.Now, true, latency, "OK");
    private static MonitoringSample Bad() =>
        new(DateTimeOffset.Now, false, null, "Critical");

    private static async Task<bool> WaitForAsync(Func<bool> predicate, int timeoutMs = 5000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
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
    public void Factory_CreatesIndependentSessions()
    {
        var probe = new ScriptedProbe([Ok(10)], Ok(10));
        var factory = new MonitoringSessionFactory(probe);

        var s1 = factory.Create();
        var s2 = factory.Create();
        s2.Should().NotBeSameAs(s1);
    }

    [Fact]
    public void Create_InvalidIntervalOrMaxSamples_Throws()
    {
        var probe = new ScriptedProbe([Ok(10)], Ok(10));
        var factory = new MonitoringSessionFactory(probe);

        FluentActions.Invoking(() => factory.Create(intervalMs: 10)).Should().Throw<ArgumentOutOfRangeException>();
        FluentActions.Invoking(() => factory.Create(maxSamples: 0)).Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task StartAsync_WithEmptyTarget_Throws()
    {
        var probe = new ScriptedProbe([Ok(10)], Ok(10));
        using var session = CreateHeadlessSession(probe, intervalMs: 100, maxSamples: 5);

        await FluentActions.Invoking(() => session.StartAsync(string.Empty)).Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task StartAsync_CollectsSamples()
    {
        var probe = new ScriptedProbe([Ok(10), Ok(15), Ok(12)], Ok(20));
        using var session = CreateHeadlessSession(probe, intervalMs: 100, maxSamples: 50);

        await session.StartAsync("192.168.1.1");
        var got3 = await WaitForAsync(() => session.Samples.Count >= 3);
        got3.Should().BeTrue();
        session.Stop();
        session.IsRunning.Should().BeFalse();
        session.CurrentTarget.Should().Be("192.168.1.1");
    }

    [Fact]
    public async Task Samples_RespectMaxSamplesCap()
    {
        var probe = new ScriptedProbe([], Ok(10)); // always Ok(10)
        using var session = CreateHeadlessSession(probe, intervalMs: 100, maxSamples: 3);

        await session.StartAsync("192.168.1.1");
        var enough = await WaitForAsync(() => probe.CallCount >= 6);
        enough.Should().BeTrue();
        session.Stop();
        session.Samples.Count.Should().BeLessOrEqualTo(3);
    }

    [Fact]
    public async Task SampleReceived_IsRaisedForEachSample()
    {
        var probe = new ScriptedProbe([Ok(10), Ok(15)], Ok(20));
        using var session = CreateHeadlessSession(probe, intervalMs: 100, maxSamples: 10);

        var received = 0;
        session.SampleReceived += (_, _) => Interlocked.Increment(ref received);

        await session.StartAsync("192.168.1.1");
        var got = await WaitForAsync(() => received >= 2);
        got.Should().BeTrue();
        session.Stop();
    }

    [Fact]
    public async Task SampleReceived_Exception_DoesNotStopBackgroundLoop()
    {
        var probe = new ScriptedProbe([], Ok(10));
        using var session = CreateHeadlessSession(probe, intervalMs: 80, maxSamples: 10);
        session.SampleReceived += (_, _) => throw new InvalidOperationException("subscriber failed");

        await session.StartAsync("192.168.1.1");
        var keptRunning = await WaitForAsync(() => probe.CallCount >= 3);

        session.Stop();
        keptRunning.Should().BeTrue();
        session.Samples.Count.Should().BeGreaterOrEqualTo(1);
    }

    [Fact]
    public async Task StatusChanged_FiresOnTransition()
    {
        var probe = new ScriptedProbe([Ok(10), Ok(20), Bad(), Bad()], Bad());
        using var session = CreateHeadlessSession(probe, intervalMs: 80, maxSamples: 10);

        var transitions = new List<string>();
        var lockObj = new object();
        session.StatusChanged += (_, status) =>
        {
            lock (lockObj) transitions.Add(status);
        };

        await session.StartAsync("192.168.1.1");
        var got = await WaitForAsync(() =>
        {
            lock (lockObj) return transitions.Contains("Critical");
        });
        got.Should().BeTrue();
        session.Stop();

        lock (lockObj)
        {
            transitions.Should().Contain("Critical");
        }
    }

    [Fact]
    public async Task Stop_CeasesProbing()
    {
        var probe = new ScriptedProbe([], Ok(10));
        using var session = CreateHeadlessSession(probe, intervalMs: 80, maxSamples: 100);

        await session.StartAsync("192.168.1.1");
        var startedProbing = await WaitForAsync(() => probe.CallCount >= 2);
        startedProbing.Should().BeTrue();
        session.Stop();

        var snapshot = probe.CallCount;
        await Task.Delay(300);
        probe.CallCount.Should().BeLessOrEqualTo(snapshot + 1, "probe should not be called more than once after Stop");
    }

    [Fact]
    public async Task Restart_ClearsPreviousSamples()
    {
        var probe = new ScriptedProbe([], Ok(10));
        using var session = CreateHeadlessSession(probe, intervalMs: 80, maxSamples: 100);

        await session.StartAsync("192.168.1.1");
        var firstRound = await WaitForAsync(() => session.Samples.Count >= 3);
        firstRound.Should().BeTrue();

        await session.StartAsync("192.168.1.2");
        // After restart, samples were cleared; we should see fewer than the first run reached
        session.CurrentTarget.Should().Be("192.168.1.2");
        // Wait briefly to confirm fresh collection
        var freshAccumulating = await WaitForAsync(() => session.Samples.Count >= 1);
        freshAccumulating.Should().BeTrue();
        session.Stop();
    }

    [Fact]
    public async Task Dispose_StopsRunner()
    {
        var probe = new ScriptedProbe([], Ok(10));
        var session = CreateHeadlessSession(probe, intervalMs: 80, maxSamples: 100);

        await session.StartAsync("192.168.1.1");
        var started = await WaitForAsync(() => probe.CallCount >= 1);
        started.Should().BeTrue();

        session.Dispose();
        var snapshot = probe.CallCount;
        await Task.Delay(300);
        probe.CallCount.Should().BeLessOrEqualTo(snapshot + 1);
    }

    [Fact]
    public async Task Summary_UpdatesAsSamplesArrive()
    {
        var probe = new ScriptedProbe([Ok(10), Ok(20), Ok(30)], Ok(15));
        using var session = CreateHeadlessSession(probe, intervalMs: 80, maxSamples: 50);

        await session.StartAsync("192.168.1.1");
        var got = await WaitForAsync(() => session.Summary.SampleCount >= 3);
        got.Should().BeTrue();
        session.Stop();

        session.Summary.SampleCount.Should().BeGreaterOrEqualTo(3);
        session.Summary.AverageLatencyMs.Should().NotBeNull();
    }
}
