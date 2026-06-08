using NetScopeDiagnosticCenter.Collectors;
using NetScopeDiagnosticCenter.Infrastructure;
using NetScopeDiagnosticCenter.Infrastructure.Wlan;

namespace NetScopeDiagnosticCenter.Tests.Collectors.Wifi;

public sealed class WifiCollectorTests
{
    [Fact]
    public async Task GetWifiInfoAsync_WithMultipleWlanInterfaces_UsesSelectedAdapterDescription()
    {
        var selectedGuid = Guid.NewGuid();
        var otherGuid = Guid.NewGuid();
        using var wlan = new FakeWlanApi(
            new[]
            {
                new WlanInterfaceSnapshot(otherGuid, "Other Wireless Adapter", WlanInterfaceState.Connected),
                new WlanInterfaceSnapshot(selectedGuid, "Intel Selected Wi-Fi", WlanInterfaceState.Connected),
            });
        wlan.Connections[otherGuid] = Connection("wrong-ssid", 44);
        wlan.Connections[selectedGuid] = Connection("selected-ssid", 82);
        wlan.Channels[selectedGuid] = 36;

        var collector = new WifiCollector(new PowerShellRunner(new NullLogger()), wlan);

        var result = await collector.GetWifiInfoAsync("Wi-Fi", "Intel Selected Wi-Fi");

        result.Should().NotBeNull();
        result!.Ssid.Should().Be("selected-ssid");
        result.InterfaceName.Should().Be("Wi-Fi");
        result.InterfaceDescription.Should().Be("Intel Selected Wi-Fi");
        result.Channel.Should().Be(36);
    }

    [Fact]
    public async Task GetWifiInfoAsync_WithMultipleWlanInterfaces_DoesNotUseUnmatchedAdapter()
    {
        using var wlan = new FakeWlanApi(
            new[]
            {
                new WlanInterfaceSnapshot(Guid.NewGuid(), "First Wireless Adapter", WlanInterfaceState.Connected),
                new WlanInterfaceSnapshot(Guid.NewGuid(), "Second Wireless Adapter", WlanInterfaceState.Connected),
            });

        var collector = new WifiCollector(new PowerShellRunner(new NullLogger()), wlan);

        var result = await collector.GetWifiInfoAsync("Wi-Fi 2", "Missing Wireless Adapter");

        result.Should().NotBeNull();
        result!.InterfaceName.Should().Be("Wi-Fi 2");
        result.InterfaceDescription.Should().Be("Missing Wireless Adapter");
        result.CollectorStatus.Should().Be("Wi-Fi information unavailable for selected adapter");
        wlan.ConnectionQueryCount.Should().Be(0);
    }

    private static WlanCurrentConnectionSnapshot Connection(string ssid, int signalPercent) => new(
        ssid,
        $"{ssid}-profile",
        "AA:BB:CC:DD:EE:FF",
        Dot11PhyType.He,
        signalPercent,
        866_000_000,
        433_000_000,
        Dot11AuthAlgorithm.RsnaPsk,
        Dot11CipherAlgorithm.Ccmp,
        SecurityEnabled: true,
        OneXEnabled: false,
        WlanInterfaceState.Connected);

    private sealed class FakeWlanApi : IWlanApi
    {
        private readonly IReadOnlyList<WlanInterfaceSnapshot> _interfaces;

        public FakeWlanApi(IReadOnlyList<WlanInterfaceSnapshot> interfaces)
        {
            _interfaces = interfaces;
        }

        public bool IsAvailable => true;
        public Dictionary<Guid, WlanCurrentConnectionSnapshot?> Connections { get; } = new();
        public Dictionary<Guid, int?> Channels { get; } = new();
        public int ConnectionQueryCount { get; private set; }

        public IReadOnlyList<WlanInterfaceSnapshot> EnumerateInterfaces() => _interfaces;

        public WlanCurrentConnectionSnapshot? QueryCurrentConnection(Guid interfaceGuid)
        {
            ConnectionQueryCount++;
            return Connections.TryGetValue(interfaceGuid, out var connection) ? connection : null;
        }

        public int? QueryCurrentRssiDbm(Guid interfaceGuid) => null;

        public int? QueryCurrentChannel(Guid interfaceGuid) =>
            Channels.TryGetValue(interfaceGuid, out var channel) ? channel : null;

        public Task<bool> ScanAsync(Guid interfaceGuid, CancellationToken cancellationToken) =>
            Task.FromResult(true);

        public IReadOnlyList<WlanBssSnapshot> GetVisibleBssEntries(Guid interfaceGuid) =>
            Array.Empty<WlanBssSnapshot>();

        public IReadOnlyList<string> GetProfileNames(Guid interfaceGuid) =>
            Array.Empty<string>();

        public bool DeleteProfile(Guid interfaceGuid, string profileName) => false;

        public void Dispose()
        {
        }
    }

    private sealed class NullLogger : ILoggingService
    {
        public void Debug(string message) { }
        public void Info(string message) { }
        public void Warning(string message) { }
        public void Error(string message, Exception? exception = null) { }
    }
}
