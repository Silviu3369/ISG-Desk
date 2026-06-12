using NetScopeDiagnosticCenter.Core.Monitoring;

namespace NetScopeDiagnosticCenter.Tests.Core.Monitoring;

public class MonitoringSummaryTests
{
    private static MonitoringSample Sample(double? latency, bool ok = true) =>
        new(DateTimeOffset.Now, ok, latency, ok ? "OK" : "Critical");

    [Fact]
    public void Empty_HasZeroEverything()
    {
        var s = MonitoringSummary.Empty;
        s.SampleCount.Should().Be(0);
        s.LossPercent.Should().Be(0);
        s.AverageLatencyMs.Should().BeNull();
        s.JitterMs.Should().BeNull();
    }

    [Fact]
    public void From_NoSamples_ReturnsEmpty()
    {
        var s = MonitoringSummary.From(new List<MonitoringSample>());
        s.Should().Be(MonitoringSummary.Empty);
    }

    [Fact]
    public void From_AllSuccess_LossIsZero()
    {
        var samples = new List<MonitoringSample>
        {
            Sample(10), Sample(15), Sample(12)
        };
        var s = MonitoringSummary.From(samples);
        s.SampleCount.Should().Be(3);
        s.SuccessCount.Should().Be(3);
        s.FailureCount.Should().Be(0);
        s.LossPercent.Should().Be(0);
        s.AverageLatencyMs.Should().BeApproximately(12.3, 0.5);
        s.MinLatencyMs.Should().Be(10);
        s.MaxLatencyMs.Should().Be(15);
    }

    [Fact]
    public void From_PartialFailures_ComputesLossPercent()
    {
        var samples = new List<MonitoringSample>
        {
            Sample(10), Sample(null, ok: false), Sample(15), Sample(null, ok: false)
        };
        var s = MonitoringSummary.From(samples);
        s.SampleCount.Should().Be(4);
        s.SuccessCount.Should().Be(2);
        s.FailureCount.Should().Be(2);
        s.LossPercent.Should().Be(50);
    }

    [Fact]
    public void From_AllFailures_NoLatencyStats()
    {
        var samples = new List<MonitoringSample>
        {
            Sample(null, ok: false), Sample(null, ok: false)
        };
        var s = MonitoringSummary.From(samples);
        s.LossPercent.Should().Be(100);
        s.AverageLatencyMs.Should().BeNull();
        s.JitterMs.Should().BeNull();
    }

    [Fact]
    public void From_Jitter_ComputedAsMeanAbsoluteDelta()
    {
        // latencies: 10, 20, 15, 25 → deltas: 10, 5, 10 → mean = 25/3 ≈ 8.3
        var samples = new List<MonitoringSample>
        {
            Sample(10), Sample(20), Sample(15), Sample(25)
        };
        var s = MonitoringSummary.From(samples);
        s.JitterMs.Should().BeApproximately(8.3, 0.5);
    }

    [Fact]
    public void From_SingleSample_JitterIsNull()
    {
        var s = MonitoringSummary.From(new List<MonitoringSample> { Sample(10) });
        s.JitterMs.Should().BeNull();
    }

    [Fact]
    public void LossDisplay_NoSamples_SaysWaitingNotZeroLoss()
    {
        // The sidebar must not claim "loss 0%" before the monitor has measured anything.
        MonitoringSummary.Empty.LossDisplay.Should().Be("waiting for samples");
    }

    [Fact]
    public void LossDisplay_WithSamples_FormatsLossPercent()
    {
        var samples = new List<MonitoringSample>
        {
            Sample(10), Sample(null, ok: false), Sample(15), Sample(null, ok: false)
        };
        MonitoringSummary.From(samples).LossDisplay.Should().Be("loss 50%");
    }

    [Fact]
    public void TooltipText_NoSamples_ExplainsWaitingState()
    {
        MonitoringSummary.Empty.TooltipText.Should().Contain("No samples yet");
    }

    [Fact]
    public void TooltipText_WithSamples_IncludesWindowStatistics()
    {
        var samples = new List<MonitoringSample>
        {
            Sample(10), Sample(20), Sample(15), Sample(null, ok: false)
        };
        var text = MonitoringSummary.From(samples).TooltipText;
        text.Should().Contain("4 sample(s)");
        text.Should().Contain("avg 15.0 ms");
        text.Should().Contain("min 10.0 ms");
        text.Should().Contain("max 20.0 ms");
        text.Should().Contain("loss 25.0%");
        text.Should().Contain("(1 failed)");
    }

    [Fact]
    public void TooltipText_AllFailures_ShowsDashForLatencies()
    {
        var samples = new List<MonitoringSample>
        {
            Sample(null, ok: false), Sample(null, ok: false)
        };
        var text = MonitoringSummary.From(samples).TooltipText;
        text.Should().Contain("avg —");
        text.Should().Contain("loss 100.0%");
    }
}
