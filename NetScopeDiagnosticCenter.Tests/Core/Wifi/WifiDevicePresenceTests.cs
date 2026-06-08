using NetScopeDiagnosticCenter.Core.Models.Wifi;

namespace NetScopeDiagnosticCenter.Tests.Core.Wifi;

public sealed class WifiDevicePresenceTests
{
    [Fact]
    public void MarkSeen_KeepsFirstSeenAndAdvancesLastSeenAndCount()
    {
        var firstSeen = new DateTimeOffset(2026, 6, 4, 9, 10, 0, TimeSpan.FromHours(2));
        var secondSeen = new DateTimeOffset(2026, 6, 4, 9, 15, 0, TimeSpan.FromHours(2));
        var presence = new WifiDevicePresence(firstSeen, firstSeen, 1);

        var updated = presence.MarkSeen(secondSeen);

        updated.FirstSeenAt.Should().Be(firstSeen);
        updated.LastSeenAt.Should().Be(secondSeen);
        updated.SeenCount.Should().Be(2);
    }
}
