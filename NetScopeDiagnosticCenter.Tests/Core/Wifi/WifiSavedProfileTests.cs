using NetScopeDiagnosticCenter.Core.Models.Wifi;

namespace NetScopeDiagnosticCenter.Tests.Core.Wifi;

public sealed class WifiSavedProfileTests
{
    [Fact]
    public void FromNames_TrimsDropsBlankDeduplicatesAndSorts()
    {
        var profiles = WifiSavedProfile.FromNames([
            " Office ",
            "",
            "guest",
            "OFFICE",
            "IoT",
            "   "
        ]);

        profiles.Select(profile => profile.DisplayName)
            .Should().Equal("guest", "IoT", "Office");
    }

    [Fact]
    public void FromNames_NullInputReturnsEmptyList()
    {
        WifiSavedProfile.FromNames(null).Should().BeEmpty();
    }
}
