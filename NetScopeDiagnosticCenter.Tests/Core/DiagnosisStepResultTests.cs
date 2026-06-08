using NetScopeDiagnosticCenter.Core.Models;

namespace NetScopeDiagnosticCenter.Tests.Core;

public sealed class DiagnosisStepResultTests
{
    [Theory]
    [InlineData(0, "not timed")]
    [InlineData(95.4, "run 95 ms")]
    [InlineData(1684, "run 1.7 s")]
    public void DurationDisplayText_MakesRuntimeExplicit(double durationMs, string expected)
    {
        var step = new DiagnosisStepResult { DurationMs = durationMs };

        step.DurationDisplayText.Should().Be(expected);
    }
}
