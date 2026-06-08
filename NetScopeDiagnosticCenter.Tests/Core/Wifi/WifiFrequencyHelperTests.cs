using NetScopeDiagnosticCenter.Core.Models.Wifi;
using NetScopeDiagnosticCenter.Core.Wifi;

namespace NetScopeDiagnosticCenter.Tests.Core.Wifi;

public class WifiFrequencyHelperTests
{
    [Theory]
    [InlineData(2412, WifiBand.TwoPointFourGhz, 1)]
    [InlineData(2417, WifiBand.TwoPointFourGhz, 2)]
    [InlineData(2437, WifiBand.TwoPointFourGhz, 6)]
    [InlineData(2462, WifiBand.TwoPointFourGhz, 11)]
    [InlineData(2472, WifiBand.TwoPointFourGhz, 13)]
    [InlineData(2484, WifiBand.TwoPointFourGhz, 14)]
    public void FromFrequencyMhz_24GhzBand_ResolvesCorrectChannel(int mhz, WifiBand expectedBand, int expectedChannel)
    {
        var (band, channel) = WifiFrequencyHelper.FromFrequencyMhz(mhz);
        band.Should().Be(expectedBand);
        channel.Should().Be(expectedChannel);
    }

    [Theory]
    [InlineData(5180, WifiBand.FiveGhz, 36)]
    [InlineData(5200, WifiBand.FiveGhz, 40)]
    [InlineData(5260, WifiBand.FiveGhz, 52)]    // DFS start
    [InlineData(5500, WifiBand.FiveGhz, 100)]   // U-NII-2C
    [InlineData(5745, WifiBand.FiveGhz, 149)]   // U-NII-3
    [InlineData(5825, WifiBand.FiveGhz, 165)]
    public void FromFrequencyMhz_5GhzBand_ResolvesCorrectChannel(int mhz, WifiBand expectedBand, int expectedChannel)
    {
        var (band, channel) = WifiFrequencyHelper.FromFrequencyMhz(mhz);
        band.Should().Be(expectedBand);
        channel.Should().Be(expectedChannel);
    }

    [Theory]
    [InlineData(5955, WifiBand.SixGhz, 2)]      // Wi-Fi 6E start (we offset from 5950)
    [InlineData(6175, WifiBand.SixGhz, 46)]
    [InlineData(7115, WifiBand.SixGhz, 234)]    // U-NII-8 end
    public void FromFrequencyMhz_6GhzBand_ResolvesCorrectChannel(int mhz, WifiBand expectedBand, int expectedChannel)
    {
        var (band, channel) = WifiFrequencyHelper.FromFrequencyMhz(mhz);
        band.Should().Be(expectedBand);
        channel.Should().Be(expectedChannel);
    }

    [Theory]
    [InlineData(1000)]
    [InlineData(2399)]
    [InlineData(5000)]
    [InlineData(7200)]
    public void FromFrequencyMhz_OutsideKnownBands_ReturnsUnknown(int mhz)
    {
        var (band, channel) = WifiFrequencyHelper.FromFrequencyMhz(mhz);
        band.Should().Be(WifiBand.Unknown);
        channel.Should().Be(0);
    }

    [Fact]
    public void FromFrequencyKhz_ConvertsKhzInternally()
    {
        var (band, channel, mhz) = WifiFrequencyHelper.FromFrequencyKhz(2_412_000);
        band.Should().Be(WifiBand.TwoPointFourGhz);
        channel.Should().Be(1);
        mhz.Should().Be(2412);
    }

    [Theory]
    [InlineData(52)]
    [InlineData(56)]
    [InlineData(100)]
    [InlineData(140)]
    [InlineData(144)]
    public void IsDfsChannel_5GhzDfsRange_ReturnsTrue(int channel)
    {
        WifiFrequencyHelper.IsDfsChannel(WifiBand.FiveGhz, channel).Should().BeTrue();
    }

    [Theory]
    [InlineData(36)]    // U-NII-1, non-DFS
    [InlineData(48)]    // U-NII-1, non-DFS
    [InlineData(149)]   // U-NII-3, non-DFS
    [InlineData(165)]   // U-NII-3, non-DFS
    public void IsDfsChannel_5GhzNonDfsRange_ReturnsFalse(int channel)
    {
        WifiFrequencyHelper.IsDfsChannel(WifiBand.FiveGhz, channel).Should().BeFalse();
    }

    [Theory]
    [InlineData(WifiBand.TwoPointFourGhz, 6)]   // 2.4 GHz has no DFS
    [InlineData(WifiBand.SixGhz, 100)]          // 6 GHz has no DFS
    public void IsDfsChannel_NotFiveGhz_AlwaysFalse(WifiBand band, int channel)
    {
        WifiFrequencyHelper.IsDfsChannel(band, channel).Should().BeFalse();
    }

    [Fact]
    public void GetOverlappingChannels_24Ghz_ReturnsFiveChannelWindow()
    {
        // Channel 1 → 1,2,3 (clamped at low end).
        WifiFrequencyHelper.GetOverlappingChannels(WifiBand.TwoPointFourGhz, 1)
            .Should().BeEquivalentTo(new[] { 1, 2, 3 });
        // Channel 6 → 4,5,6,7,8 — five-channel symmetric window.
        WifiFrequencyHelper.GetOverlappingChannels(WifiBand.TwoPointFourGhz, 6)
            .Should().BeEquivalentTo(new[] { 4, 5, 6, 7, 8 });
        // Channel 13 → 11,12,13,14 (clamped at upper edge).
        WifiFrequencyHelper.GetOverlappingChannels(WifiBand.TwoPointFourGhz, 13)
            .Should().BeEquivalentTo(new[] { 11, 12, 13, 14 });
    }

    [Theory]
    [InlineData(WifiBand.FiveGhz, 36)]
    [InlineData(WifiBand.SixGhz, 100)]
    public void GetOverlappingChannels_NonOverlappingBands_ReturnsJustOneChannel(WifiBand band, int channel)
    {
        WifiFrequencyHelper.GetOverlappingChannels(band, channel)
            .Should().BeEquivalentTo(new[] { channel });
    }

    [Theory]
    [InlineData(0, WifiCongestionLevel.Free)]
    [InlineData(1, WifiCongestionLevel.Light)]
    [InlineData(2, WifiCongestionLevel.Light)]
    [InlineData(3, WifiCongestionLevel.Moderate)]
    [InlineData(4, WifiCongestionLevel.Moderate)]
    [InlineData(5, WifiCongestionLevel.Heavy)]
    [InlineData(20, WifiCongestionLevel.Heavy)]
    public void ClassifyCongestion_BucketsCorrectly(int count, WifiCongestionLevel expected)
    {
        WifiFrequencyHelper.ClassifyCongestion(count).Should().Be(expected);
    }

    [Theory]
    [InlineData(-30, WifiSignalLevel.Excellent)]
    [InlineData(-65, WifiSignalLevel.Excellent)]
    [InlineData(-66, WifiSignalLevel.Good)]
    [InlineData(-75, WifiSignalLevel.Good)]
    [InlineData(-76, WifiSignalLevel.Fair)]
    [InlineData(-85, WifiSignalLevel.Fair)]
    [InlineData(-86, WifiSignalLevel.Poor)]
    [InlineData(-100, WifiSignalLevel.Poor)]
    public void ClassifySignal_BucketsCorrectly(int dbm, WifiSignalLevel expected)
    {
        WifiFrequencyHelper.ClassifySignal(dbm).Should().Be(expected);
    }

    [Fact]
    public void ClassifySignal_Null_ReturnsUnknown()
    {
        WifiFrequencyHelper.ClassifySignal(null).Should().Be(WifiSignalLevel.Unknown);
    }
}
