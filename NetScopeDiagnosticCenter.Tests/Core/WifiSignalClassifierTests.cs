using NetScopeDiagnosticCenter.Core;

namespace NetScopeDiagnosticCenter.Tests.Core;

public class WifiSignalClassifierTests
{
    [Theory]
    [InlineData(100, "Excellent")]
    [InlineData(80, "Excellent")]
    [InlineData(79, "Good")]
    [InlineData(60, "Good")]
    [InlineData(59, "Fair")]
    [InlineData(40, "Fair")]
    [InlineData(39, "Weak")]
    [InlineData(20, "Weak")]
    [InlineData(19, "Very Weak")]
    [InlineData(0, "Very Weak")]
    public void Classify_ReturnsExpectedLabel(int signal, string expected)
    {
        WifiSignalClassifier.Classify(signal).Should().Be(expected);
    }

    [Fact]
    public void Classify_NullSignal_ReturnsUnknown()
    {
        WifiSignalClassifier.Classify(null).Should().Be("Unknown");
    }

    [Theory]
    [InlineData(100, "OK")]
    [InlineData(60, "OK")]
    [InlineData(59, "Warning")]
    [InlineData(40, "Warning")]
    [InlineData(39, "Critical")]
    [InlineData(0, "Critical")]
    public void GetSeverity_ReturnsExpected(int signal, string expected)
    {
        WifiSignalClassifier.GetSeverity(signal).Should().Be(expected);
    }

    [Fact]
    public void GetSeverity_NullSignal_ReturnsUnknown()
    {
        WifiSignalClassifier.GetSeverity(null).Should().Be("Unknown");
    }

    [Theory]
    [InlineData(100, 0)]
    [InlineData(80, 0)]
    [InlineData(70, 5)]
    [InlineData(60, 5)]
    [InlineData(50, 20)]
    [InlineData(40, 20)]
    [InlineData(30, 40)]
    [InlineData(20, 40)]
    [InlineData(10, 60)]
    [InlineData(0, 60)]
    public void GetHealthPenalty_ReturnsExpected(int signal, int expected)
    {
        WifiSignalClassifier.GetHealthPenalty(signal).Should().Be(expected);
    }

    [Fact]
    public void GetHealthPenalty_NullSignal_ReturnsZero()
    {
        WifiSignalClassifier.GetHealthPenalty(null).Should().Be(0);
    }
}
