namespace NetScopeDiagnosticCenter.Core.Models.Wifi;

public sealed record WifiDevicePresence(
    DateTimeOffset FirstSeenAt,
    DateTimeOffset LastSeenAt,
    int SeenCount)
{
    public WifiDevicePresence MarkSeen(DateTimeOffset now) =>
        this with
        {
            LastSeenAt = now,
            SeenCount = Math.Max(SeenCount, 0) + 1
        };
}
