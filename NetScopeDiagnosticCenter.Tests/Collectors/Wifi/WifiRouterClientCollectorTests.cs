using System.Reflection;
using NetScopeDiagnosticCenter.Collectors.Wifi;
using NetScopeDiagnosticCenter.Core.Models.Wifi;
using NetScopeDiagnosticCenter.Infrastructure;

namespace NetScopeDiagnosticCenter.Tests.Collectors.Wifi;

public sealed class WifiRouterClientCollectorTests
{
    [Fact]
    public void ParseIpNetToMediaClients_CorrelatesRouterRowsWithLocalNames()
    {
        var rows = new Dictionary<string, string>
        {
            ["1.3.6.1.2.1.4.22.1.2.7.192.168.0.102"] = "8C 55 70 B6 F4 27",
            ["1.3.6.1.2.1.4.22.1.2.7.192.168.1.10"] = "AA BB CC DD EE FF"
        };
        WifiLanDevice[] localDevices =
        [
            new(
                IpAddress: "192.168.0.102",
                MacAddress: "8C:55:70:B6:F4:27",
                HostName: "LivingRoom-TV",
                Vendor: "Example",
                IsGateway: false,
                IsThisPc: false,
                NameSource: "SSDP / UPnP")
        ];

        var clients = ParseIpNetToMediaClients(rows, "192.168.0.", localDevices);

        clients.Should().ContainSingle();
        var client = clients.Single();
        client.IpAddress.Should().Be("192.168.0.102");
        client.MacDisplay.Should().Be("8C:55:70:B6:F4:27");
        client.HostDisplay.Should().Be("LivingRoom-TV");
        client.LocalMatchLabel.Should().Be("Matched local scan");
        client.InterfaceIndex.Should().Be("7");
        client.IsWirelessAssociation.Should().BeFalse();
        client.WirelessAssociationDisplay.Should().Be("ARP/IP evidence only");
    }

    [Fact]
    public void ParseIpNetToMediaClients_UsesRequestedSourceLabel()
    {
        var rows = new Dictionary<string, string>
        {
            ["1.3.6.1.2.1.4.22.1.2.7.192.168.0.117"] = "D0 96 86 0C 3D 92"
        };

        var clients = ParseIpNetToMediaClients(
            rows,
            "192.168.0.",
            Array.Empty<WifiLanDevice>(),
            "SNMP v3 authPriv ipNetToMedia");

        clients.Should().ContainSingle();
        clients.Single().SourceDisplay.Should().Be("Router/AP SNMP v3 authPriv ipNetToMedia");
        clients.Single().IsWirelessAssociation.Should().BeFalse();
    }

    [Theory]
    [InlineData("8C 55 70 B6 F4 27", "8C:55:70:B6:F4:27")]
    [InlineData("8c:55:70:b6:f4:27", "8C:55:70:B6:F4:27")]
    [InlineData("0x8c5570b6f427", "8C:55:70:B6:F4:27")]
    [InlineData("8c5570b6f427", "8C:55:70:B6:F4:27")]
    public void NormalizeSnmpMac_AcceptsCommonFormats(string raw, string expected)
    {
        NormalizeSnmpMac(raw).Should().Be(expected);
    }

    [Theory]
    [InlineData("00 00 00 00 00 00")]
    [InlineData("")]
    [InlineData("not-a-mac")]
    public void NormalizeSnmpMac_RejectsUnusableValues(string raw)
    {
        NormalizeSnmpMac(raw).Should().BeNull();
    }

    [Fact]
    public void TryParseIpNetToMediaOid_ExtractsInterfaceAndIp()
    {
        TryParseIpNetToMediaOid(
                "1.3.6.1.2.1.4.22.1.2.12.192.168.0.210",
                out var interfaceIndex,
                out var ip)
            .Should().BeTrue();

        interfaceIndex.Should().Be("12");
        ip.Should().Be("192.168.0.210");
    }

    private static IReadOnlyList<WifiRouterClient> ParseIpNetToMediaClients(
        IReadOnlyDictionary<string, string> rawRows,
        string basePrefix,
        IReadOnlyCollection<WifiLanDevice> localDevices)
    {
        var method = typeof(WifiRouterClientCollector).GetMethod(
            "ParseIpNetToMediaClients",
            BindingFlags.Static | BindingFlags.NonPublic,
            binder: null,
            types: [typeof(IReadOnlyDictionary<string, string>), typeof(string), typeof(IReadOnlyCollection<WifiLanDevice>)],
            modifiers: null);
        method.Should().NotBeNull();

        return (IReadOnlyList<WifiRouterClient>)method!.Invoke(null, [rawRows, basePrefix, localDevices])!;
    }

    private static IReadOnlyList<WifiRouterClient> ParseIpNetToMediaClients(
        IReadOnlyDictionary<string, string> rawRows,
        string basePrefix,
        IReadOnlyCollection<WifiLanDevice> localDevices,
        string source)
    {
        var method = typeof(WifiRouterClientCollector).GetMethod(
            "ParseIpNetToMediaClients",
            BindingFlags.Static | BindingFlags.NonPublic,
            binder: null,
            types: [typeof(IReadOnlyDictionary<string, string>), typeof(string), typeof(IReadOnlyCollection<WifiLanDevice>), typeof(string)],
            modifiers: null);
        method.Should().NotBeNull();

        return (IReadOnlyList<WifiRouterClient>)method!.Invoke(null, [rawRows, basePrefix, localDevices, source])!;
    }

    private static string? NormalizeSnmpMac(string raw)
    {
        var method = typeof(WifiRouterClientCollector).GetMethod(
            "NormalizeSnmpMac",
            BindingFlags.Static | BindingFlags.NonPublic);
        method.Should().NotBeNull();

        return (string?)method!.Invoke(null, [raw]);
    }

    private static bool TryParseIpNetToMediaOid(string oid, out string interfaceIndex, out string ip)
    {
        var method = typeof(WifiRouterClientCollector).GetMethod(
            "TryParseIpNetToMediaOid",
            BindingFlags.Static | BindingFlags.NonPublic);
        method.Should().NotBeNull();

        object?[] args = [oid, null, null];
        var result = (bool)method!.Invoke(null, args)!;
        interfaceIndex = (string)args[1]!;
        ip = (string)args[2]!;
        return result;
    }

    private sealed class NullLogger : ILoggingService
    {
        public void Debug(string message) { }
        public void Info(string message) { }
        public void Warning(string message) { }
        public void Error(string message, Exception? exception = null) { }
    }
}
