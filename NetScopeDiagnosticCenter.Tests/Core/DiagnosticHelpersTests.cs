using NetScopeDiagnosticCenter.Core;
using NetScopeDiagnosticCenter.Core.Models;

namespace NetScopeDiagnosticCenter.Tests.Core;

public class DiagnosticHelpersTests
{
    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("  ", true)]
    [InlineData("OK", true)]
    [InlineData("ok", true)]
    [InlineData("Warning", false)]
    [InlineData("Critical", false)]
    [InlineData("Failed", false)]
    public void IsCollectorOk_ReturnsExpected(string? status, bool expected)
    {
        DiagnosticHelpers.IsCollectorOk(status).Should().Be(expected);
    }

    [Fact]
    public void BuildDiagnosisComparison_MissingEitherRun_ReturnsEmpty()
    {
        var run = MakeDiagnosis(80, "OK", "Healthy");

        DiagnosticHelpers.BuildDiagnosisComparison(null, run).Should().BeEmpty();
        DiagnosticHelpers.BuildDiagnosisComparison(run, null).Should().BeEmpty();
        DiagnosticHelpers.BuildDiagnosisComparison(null, null).Should().BeEmpty();
    }

    [Theory]
    [InlineData(92, 100, "score +8 — improved")]
    [InlineData(100, 85, "score -15 — degraded")]
    [InlineData(74, 74, "score unchanged")]
    public void BuildDiagnosisComparison_ReportsScoreTrend(int previousScore, int currentScore, string expectedTrend)
    {
        var previous = MakeDiagnosis(previousScore, "Warning", "DNS failure");
        var current = MakeDiagnosis(currentScore, "OK", "No clear network fault detected");

        var text = DiagnosticHelpers.BuildDiagnosisComparison(previous, current);

        text.Should().Contain($"{previousScore}/100");
        text.Should().Contain("Warning — DNS failure");
        text.Should().Contain(expectedTrend);
    }

    private static NetworkDiagnosisResult MakeDiagnosis(int score, string severity, string title) => new()
    {
        HealthScore = new HealthScoreResult { Score = score, Status = severity },
        Verdict = new DiagnosisVerdict { Title = title, Severity = severity }
    };

    [Fact]
    public void FormatNullable_HasValue_ReturnsFormatted()
    {
        DiagnosticHelpers.FormatNullable(12.345).Should().Be(12.345.ToString("N2"));
    }

    [Fact]
    public void FormatNullable_Null_ReturnsUnknown()
    {
        DiagnosticHelpers.FormatNullable(null).Should().Be("Unknown");
    }

    [Theory]
    [InlineData(true, "Yes")]
    [InlineData(false, "No")]
    public void FormatBool_HasValue_ReturnsExpected(bool value, string expected)
    {
        DiagnosticHelpers.FormatBool(value).Should().Be(expected);
    }

    [Fact]
    public void FormatBool_Null_ReturnsUnknown()
    {
        DiagnosticHelpers.FormatBool(null).Should().Be("Unknown");
    }

    [Theory]
    [InlineData(new[] { "OK", "Warning", "Critical" }, "Critical")]
    [InlineData(new[] { "OK", "Warning" }, "Warning")]
    [InlineData(new[] { "OK", "OK" }, "OK")]
    [InlineData(new[] { "Unknown", "" }, "Unknown")]
    [InlineData(new string[] { }, "Unknown")]
    public void WorstStatus_ReturnsHighestSeverity(string[] statuses, string expected)
    {
        DiagnosticHelpers.WorstStatus(statuses).Should().Be(expected);
    }

    [Fact]
    public void WorstStatus_TwoArguments_Works()
    {
        DiagnosticHelpers.WorstStatus("OK", "Critical").Should().Be("Critical");
    }

    [Fact]
    public void FirstNonEmpty_FirstNonEmptyValueReturned()
    {
        DiagnosticHelpers.FirstNonEmpty("", "first", "second").Should().Be("first");
    }

    [Fact]
    public void FirstNonEmpty_AllEmpty_ReturnsEmpty()
    {
        DiagnosticHelpers.FirstNonEmpty("  ", "").Should().Be(string.Empty);
    }

    [Fact]
    public void FirstNonEmpty_NoArgs_ReturnsEmpty()
    {
        DiagnosticHelpers.FirstNonEmpty().Should().Be(string.Empty);
    }

    [Fact]
    public void FirstCriticalDetails_NoCritical_ReturnsEmpty()
    {
        var ok = new TestResult { Status = "OK", Details = "all good" };
        DiagnosticHelpers.FirstCriticalDetails(ok).Should().BeEmpty();
    }

    [Fact]
    public void FirstCriticalDetails_HasCritical_ReturnsDetails()
    {
        var ok = new TestResult { Status = "OK", Details = "ok details" };
        var bad = new TestResult { Status = "Critical", Details = "bad details" };
        DiagnosticHelpers.FirstCriticalDetails(ok, bad).Should().Be("bad details");
    }

    [Fact]
    public void FirstCollectorError_AllOk_ReturnsEmpty()
    {
        DiagnosticHelpers.FirstCollectorError("OK", string.Empty, "  ").Should().BeEmpty();
    }

    [Fact]
    public void FirstCollectorError_HasError_ReturnsFirst()
    {
        DiagnosticHelpers.FirstCollectorError("OK", "Failed: timeout", "Other").Should().Be("Failed: timeout");
    }
}
