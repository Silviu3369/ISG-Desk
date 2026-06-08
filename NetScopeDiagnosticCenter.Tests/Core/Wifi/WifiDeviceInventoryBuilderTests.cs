using NetScopeDiagnosticCenter.Core.Models.Wifi;
using NetScopeDiagnosticCenter.Core.Wifi;

namespace NetScopeDiagnosticCenter.Tests.Core.Wifi;

public sealed class WifiDeviceInventoryBuilderTests
{
    [Fact]
    public void Build_MergesLocalAndRouterRowsByMacAndAppliesManualOverrides()
    {
        var key = WifiDeviceInventoryBuilder.BuildStableKey("8c-55-70-b6-f4-27", "192.168.0.102");
        WifiLanDevice[] localDevices =
        [
            new(
                IpAddress: "192.168.0.102",
                MacAddress: "8c-55-70-b6-f4-27",
                HostName: "LivingRoom-TV",
                Vendor: "Samsung Electronics",
                IsGateway: false,
                IsThisPc: false,
                NameSource: "SSDP / UPnP")
        ];
        WifiRouterClient[] routerClients =
        [
            new(
                IpAddress: "192.168.0.102",
                MacAddress: "8C:55:70:B6:F4:27",
                HostName: null,
                Vendor: null,
                InterfaceIndex: "7",
                IsAlsoDetectedLocally: true,
                Source: "SNMP ipNetToMedia")
        ];
        var friendlyNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [key] = "Living room TV"
        };
        var manualTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [key] = "Printer"
        };
        var observedAt = new DateTimeOffset(2026, 6, 4, 15, 30, 0, TimeSpan.FromHours(2));

        var inventory = WifiDeviceInventoryBuilder.Build(localDevices, routerClients, friendlyNames, manualTypes, observedAt);

        inventory.Should().ContainSingle();
        var item = inventory.Single();
        item.Key.Should().Be(key);
        item.DisplayName.Should().Be("Living room TV");
        item.LearnedName.Should().Be("LivingRoom-TV");
        item.ManualName.Should().Be("Living room TV");
        item.MacDisplay.Should().Be("8C:55:70:B6:F4:27");
        item.DeviceType.Should().Be("Printer");
        item.AutoDeviceType.Should().Be("TV / Media");
        item.AutoDeviceTypeDisplay.Should().Be("TV / Media");
        item.ManualDeviceType.Should().Be("Printer");
        item.ManualDeviceTypeDisplay.Should().Be("Printer");
        item.DeviceTypeSourceDisplay.Should().Be("Manual override");
        item.Confidence.Should().Be("Manual");
        item.Sources.Should().Contain("Local");
        item.Sources.Should().Contain("Router/AP");
        item.Sources.Should().Contain("Manual");
        item.Sources.Should().Contain("Manual type");
        item.Evidence.Should().Contain("ifIndex 7");
        item.Evidence.Should().Contain("automatic fingerprint was TV / Media");
        item.LearnedNameDisplay.Should().Be("LivingRoom-TV");
        item.LocalDetectedDisplay.Should().Be("Yes");
        item.RouterReportedDisplay.Should().Be("Yes");
        item.IsWirelessCandidate.Should().BeTrue();
        item.WirelessStatusDisplay.Should().Be("Likely wireless client");
        item.WirelessEvidenceDisplay.Should().Contain("TV / Media");
        item.ObservedAtDisplay.Should().Be("2026-06-04 15:30:00 +02:00");
        item.IsLocalDetected.Should().BeTrue();
        item.IsRouterReported.Should().BeTrue();
    }

    [Fact]
    public void Build_FingerprintsGatewayAccessPointFromRole()
    {
        WifiLanDevice[] localDevices =
        [
            new(
                IpAddress: "192.168.0.1",
                MacAddress: "20:B8:2B:6D:0E:61",
                HostName: "Default gateway",
                Vendor: "Router Vendor",
                IsGateway: true,
                IsThisPc: false,
                IsWifiAccessPoint: true,
                AccessPointBssid: "20:B8:2B:6D:0E:61",
                RoleSource: "Default gateway MAC matches the connected Wi-Fi BSSID.")
        ];

        var inventory = WifiDeviceInventoryBuilder.Build(
            localDevices,
            Array.Empty<WifiRouterClient>(),
            new Dictionary<string, string>());

        var item = inventory.Single();
        item.DisplayName.Should().Be("Default gateway");
        item.Role.Should().Be("Internet gateway / Wi-Fi AP");
        item.DeviceType.Should().Be("Router/AP");
        item.Confidence.Should().Be("High");
        item.IsWirelessCandidate.Should().BeTrue();
        item.WirelessStatusDisplay.Should().Be("Wi-Fi infrastructure");
        item.Evidence.Should().Contain("BSSID");
    }

    [Fact]
    public void Build_AppliesSessionPresenceWithoutChangingIdentity()
    {
        var key = WifiDeviceInventoryBuilder.BuildStableKey("AA:BB:CC:DD:EE:FF", "192.168.0.50");
        var firstSeen = new DateTimeOffset(2026, 6, 4, 9, 10, 0, TimeSpan.FromHours(2));
        var lastSeen = new DateTimeOffset(2026, 6, 4, 9, 25, 0, TimeSpan.FromHours(2));
        WifiLanDevice[] localDevices =
        [
            new(
                IpAddress: "192.168.0.50",
                MacAddress: "AA:BB:CC:DD:EE:FF",
                HostName: "Office-printer",
                Vendor: "Printer Vendor",
                IsGateway: false,
                IsThisPc: false,
                NameSource: "DNS cache")
        ];
        var presence = new Dictionary<string, WifiDevicePresence>(StringComparer.OrdinalIgnoreCase)
        {
            [key] = new(firstSeen, lastSeen, 3)
        };

        var inventory = WifiDeviceInventoryBuilder.Build(
            localDevices,
            Array.Empty<WifiRouterClient>(),
            new Dictionary<string, string>(),
            new Dictionary<string, string>(),
            presence,
            lastSeen);

        var item = inventory.Single();
        item.Key.Should().Be(key);
        item.DeviceType.Should().Be("Printer");
        item.IsWirelessCandidate.Should().BeFalse();
        item.WirelessStatusDisplay.Should().Be("Not wireless-proven");
        item.FirstSeenAt.Should().Be(firstSeen);
        item.LastSeenAt.Should().Be(lastSeen);
        item.SeenCount.Should().Be(3);
        item.FirstSeenDisplay.Should().Be("2026-06-04 09:10:00 +02:00");
        item.LastSeenDisplay.Should().Be("2026-06-04 09:25:00 +02:00");
        item.SeenCountDisplay.Should().Be("3");
    }

    [Fact]
    public void Build_RepresentsRouterOnlyClientsWithoutPretendingNames()
    {
        WifiRouterClient[] routerClients =
        [
            new(
                IpAddress: "192.168.0.117",
                MacAddress: "D0:96:86:0C:3D:92",
                HostName: null,
                Vendor: null,
                InterfaceIndex: "7",
                IsAlsoDetectedLocally: false,
                Source: "SNMP ipNetToMedia")
        ];

        var inventory = WifiDeviceInventoryBuilder.Build(
            Array.Empty<WifiLanDevice>(),
            routerClients,
            new Dictionary<string, string>());

        var item = inventory.Single();
        item.DisplayName.Should().Be("Unnamed device");
        item.LearnedNameDisplay.Should().Be("-");
        item.ManualNameDisplay.Should().Be("-");
        item.LocalDetectedDisplay.Should().Be("No");
        item.RouterReportedDisplay.Should().Be("Yes");
        item.DeviceType.Should().Be("Network client");
        item.Confidence.Should().Be("Medium");
        item.IsWirelessCandidate.Should().BeFalse();
        item.WirelessEvidenceDisplay.Should().Contain("not Wi-Fi association");
        item.Sources.Should().Contain("Router/AP");
        item.IsLocalDetected.Should().BeFalse();
        item.IsRouterReported.Should().BeTrue();
    }

    [Fact]
    public void Build_ConfirmedWirelessAssociationSourceIsWirelessCandidate()
    {
        WifiRouterClient[] routerClients =
        [
            new(
                IpAddress: "192.168.0.210",
                MacAddress: "04:6C:59:1C:D2:43",
                HostName: "SILVIU",
                Vendor: null,
                InterfaceIndex: "wlan0",
                IsAlsoDetectedLocally: false,
                Source: "AP wireless client association table",
                IsWirelessAssociation: true)
        ];

        var inventory = WifiDeviceInventoryBuilder.Build(
            Array.Empty<WifiLanDevice>(),
            routerClients,
            new Dictionary<string, string>());

        var item = inventory.Single();
        item.IsWirelessCandidate.Should().BeTrue();
        item.WirelessStatusDisplay.Should().Be("Confirmed wireless client");
        item.WirelessEvidenceDisplay.Should().Contain("association");
    }

    [Fact]
    public void Build_SourceTextAloneDoesNotConfirmWirelessAssociation()
    {
        WifiRouterClient[] routerClients =
        [
            new(
                IpAddress: "192.168.0.211",
                MacAddress: "04:6C:59:1C:D2:44",
                HostName: "Ambiguous",
                Vendor: null,
                InterfaceIndex: "wlan0",
                IsAlsoDetectedLocally: false,
                Source: "AP wireless client association table")
        ];

        var inventory = WifiDeviceInventoryBuilder.Build(
            Array.Empty<WifiLanDevice>(),
            routerClients,
            new Dictionary<string, string>());

        var item = inventory.Single();
        item.IsWirelessCandidate.Should().BeFalse();
        item.WirelessStatusDisplay.Should().Be("Not wireless-proven");
    }
}
