using System.Globalization;
using System.Windows.Media;
using NetScopeDiagnosticCenter.UI.Converters;

namespace NetScopeDiagnosticCenter.Tests.UI.Converters;

public sealed class WifiRecommendationAccentConverterTests
{
    [Theory]
    [InlineData("Security: network is OPEN.", "#DC2626")]
    [InlineData("Capability: adapter supports Wi-Fi 6E.", "#2563EB")]
    [InlineData("Density: 32 APs are visible.", "#2563EB")]
    [InlineData("Connect to a Wi-Fi network to see analysis.", "#2563EB")]
    [InlineData("Signal: -82 dBm is poor.", "#F59E0B")]
    [InlineData("Channel: channel 1 has 7 APs.", "#F59E0B")]
    [InlineData("DFS: channel 52 can be vacated.", "#F59E0B")]
    public void Convert_MapsRecommendationPrefixesToExpectedAccent(string recommendation, string expectedColor)
    {
        var converter = new WifiRecommendationAccentConverter();

        var brush = converter.Convert(
            recommendation,
            typeof(Brush),
            parameter: null!,
            culture: CultureInfo.InvariantCulture)
            .Should().BeOfType<SolidColorBrush>().Subject;

        brush.Color.Should().Be((Color)ColorConverter.ConvertFromString(expectedColor));
    }

    [Fact]
    public void Convert_WithBackgroundParameter_ReturnsSoftTint()
    {
        var converter = new WifiRecommendationAccentConverter();

        var brush = converter.Convert(
            "Security: network is OPEN.",
            typeof(Brush),
            "bg",
            CultureInfo.InvariantCulture)
            .Should().BeOfType<SolidColorBrush>().Subject;

        brush.Color.Should().Be((Color)ColorConverter.ConvertFromString("#FEF2F2"));
    }
}
