using NetScopeDiagnosticCenter.UI.Controls;

namespace NetScopeDiagnosticCenter.Tests.UI.Controls;

public sealed class WifiSignalTimeGraphScaleTests
{
    [Theory]
    [InlineData(-30, 10)]
    [InlineData(-90, 210)]
    [InlineData(-10, 10)]
    [InlineData(-110, 210)]
    public void RssiToY_ClampsToGraphBounds(double rssiDbm, double expectedY)
    {
        WifiSignalTimeGraphScale.RssiToY(rssiDbm, top: 10, bottom: 210)
            .Should().BeApproximately(expectedY, 0.001);
    }

    [Fact]
    public void BuildSeriesPoints_RightAlignsShorterSeries()
    {
        var points = WifiSignalTimeGraphScale.BuildSeriesPoints(
            new[] { -70, -60 },
            maxSeriesLength: 5,
            left: 0,
            right: 400,
            top: 0,
            bottom: 120);

        points.Should().HaveCount(2);
        points[0].X.Should().BeApproximately(300, 0.001);
        points[1].X.Should().BeApproximately(400, 0.001);
    }

    [Fact]
    public void BuildSeriesPoints_SingleSampleAppearsAtLatestEdge()
    {
        var points = WifiSignalTimeGraphScale.BuildSeriesPoints(
            new[] { -55 },
            maxSeriesLength: 1,
            left: 0,
            right: 400,
            top: 0,
            bottom: 120);

        points.Should().ContainSingle();
        points[0].X.Should().BeApproximately(400, 0.001);
    }

    [Fact]
    public void BuildSeriesPoints_EmptyHistoryReturnsNoPoints()
    {
        WifiSignalTimeGraphScale.BuildSeriesPoints(
                Array.Empty<int>(),
                maxSeriesLength: 5,
                left: 0,
                right: 400,
                top: 0,
                bottom: 120)
            .Should().BeEmpty();
    }
}
