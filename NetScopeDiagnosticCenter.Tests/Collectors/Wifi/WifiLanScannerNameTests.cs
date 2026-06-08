using System.Reflection;
using NetScopeDiagnosticCenter.Collectors.Wifi;

namespace NetScopeDiagnosticCenter.Tests.Collectors.Wifi;

public sealed class WifiLanScannerNameTests
{
    [Fact]
    public void ParseNetBiosName_ReturnsUniqueWorkstationName()
    {
        const string output = """
NetBIOS Remote Machine Name Table

Name               Type         Status
---------------------------------------------
OFFICE-PRINTER <00> UNIQUE      Registered
WORKGROUP      <00> GROUP       Registered
OFFICE-PRINTER <20> UNIQUE      Registered

MAC Address = AA-BB-CC-DD-EE-FF
""";

        ParseNetBiosName(output).Should().Be("OFFICE-PRINTER");
    }

    [Fact]
    public void ParseNetBiosName_IgnoresGroupOnlyNames()
    {
        const string output = """
NetBIOS Remote Machine Name Table

Name               Type         Status
---------------------------------------------
WORKGROUP      <00> GROUP       Registered
__MSBROWSE__   <01> GROUP       Registered
""";

        ParseNetBiosName(output).Should().BeNull();
    }

    [Fact]
    public void ParseNetBiosName_UsesServerServiceNameWhenWorkstationNameIsMissing()
    {
        const string output = """
NetBIOS Remote Machine Name Table

Name               Type         Status
---------------------------------------------
NAS-01         <20> UNIQUE      Registered
WORKGROUP      <00> GROUP       Registered

MAC Address = AA-BB-CC-DD-EE-FF
""";

        ParseNetBiosName(output).Should().Be("NAS-01");
    }

    [Fact]
    public void ParsePingName_ReturnsReverseLookupName()
    {
        const string output = """
Pinging laptop-01.home [192.168.0.42] with 32 bytes of data:
Reply from 192.168.0.42: bytes=32 time=2ms TTL=64
""";

        ParsePingName(output, "192.168.0.42").Should().Be("laptop-01");
    }

    [Fact]
    public void ParsePingName_RejectsPlainIpEcho()
    {
        const string output = """
Pinging 192.168.0.42 with 32 bytes of data:
Request timed out.
""";

        ParsePingName(output, "192.168.0.42").Should().BeNull();
    }

    [Theory]
    [InlineData("Living-Room-TV._googlecast._tcp.local.", "Living-Room-TV")]
    [InlineData("SILVIU-PC.local.", "SILVIU-PC")]
    [InlineData("Office Printer._ipp._tcp.local", "Office Printer")]
    public void ExtractMdnsFriendlyName_ReturnsDeviceName(string rawName, string expected)
    {
        ExtractMdnsFriendlyName(rawName).Should().Be(expected);
    }

    [Theory]
    [InlineData("_googlecast._tcp.local.")]
    [InlineData("_services._dns-sd._udp.local.")]
    public void ExtractMdnsFriendlyName_IgnoresServiceNames(string rawName)
    {
        ExtractMdnsFriendlyName(rawName).Should().BeNull();
    }

    [Fact]
    public void ParseSsdpHeaders_ReturnsLocationCaseInsensitively()
    {
        const string response = """
HTTP/1.1 200 OK
CACHE-CONTROL: max-age=1800
LOCATION: http://192.168.0.55:8008/ssdp/device-desc.xml
SERVER: Linux/5.4 UPnP/1.0 Test/1.0
ST: upnp:rootdevice

""";

        var headers = ParseSsdpHeaders(response);

        headers["location"].Should().Be("http://192.168.0.55:8008/ssdp/device-desc.xml");
    }

    [Fact]
    public void ParseSsdpFriendlyName_ReturnsDeviceFriendlyName()
    {
        const string xml = """
<?xml version="1.0"?>
<root xmlns="urn:schemas-upnp-org:device-1-0">
  <device>
    <friendlyName>Living Room TV</friendlyName>
    <manufacturer>Example</manufacturer>
    <modelName>Media Box</modelName>
  </device>
</root>
""";

        ParseSsdpFriendlyName(xml).Should().Be("Living Room TV");
    }

    [Fact]
    public void TryCreateSafePrivateUri_AcceptsOnlySamePrivateSubnetHttpLocation()
    {
        TryCreateSafePrivateUri("http://192.168.0.55:8008/desc.xml", "192.168.0.", out var safe)
            .Should().BeTrue();
        safe!.Host.Should().Be("192.168.0.55");

        TryCreateSafePrivateUri("http://8.8.8.8/desc.xml", "192.168.0.", out _)
            .Should().BeFalse();
        TryCreateSafePrivateUri("https://192.168.0.55/desc.xml", "192.168.0.", out _)
            .Should().BeFalse();
    }

    [Fact]
    public void DetectNetworkRole_WhenGatewayMacMatchesBssid_ConfirmsAccessPoint()
    {
        var role = DetectNetworkRole(
            ip: "192.168.0.1",
            mac: "AA-BB-CC-00-00-01",
            gatewayIp: "192.168.0.1",
            connectedBssid: "AA:BB:CC:00:00:01",
            isThisPc: false);

        GetProperty<bool>(role, "IsWifiAccessPoint").Should().BeTrue();
        GetProperty<bool>(role, "IsLikelyWifiAccessPoint").Should().BeFalse();
        GetProperty<string>(role, "Source").Should().Contain("matches");
    }

    [Fact]
    public void DetectNetworkRole_WhenGatewaySharesOuiWithBssid_MarksLikelyAccessPoint()
    {
        var role = DetectNetworkRole(
            ip: "192.168.0.1",
            mac: "AA-BB-CC-00-00-02",
            gatewayIp: "192.168.0.1",
            connectedBssid: "AA:BB:CC:00:00:01",
            isThisPc: false);

        GetProperty<bool>(role, "IsWifiAccessPoint").Should().BeFalse();
        GetProperty<bool>(role, "IsLikelyWifiAccessPoint").Should().BeTrue();
        GetProperty<string>(role, "Source").Should().Contain("OUI AA:BB:CC");
    }

    [Fact]
    public void DetectNetworkRole_WhenGatewayDiffersFromBssid_DoesNotClaimAccessPoint()
    {
        var role = DetectNetworkRole(
            ip: "192.168.0.1",
            mac: "20-B8-2B-6D-0E-61",
            gatewayIp: "192.168.0.1",
            connectedBssid: "0E:96:86:0C:3D:95",
            isThisPc: false);

        GetProperty<bool>(role, "IsWifiAccessPoint").Should().BeFalse();
        GetProperty<bool>(role, "IsLikelyWifiAccessPoint").Should().BeFalse();
        GetProperty<string>(role, "Source").Should().Contain("Default IPv4 gateway");
        GetProperty<string>(role, "Source").Should().Contain("0E:96:86:0C:3D:95");
    }

    [Fact]
    public void ParseNetBiosName_RejectsIpAddressOrEmptyOutput()
    {
        ParseNetBiosName("").Should().BeNull();
        ParseNetBiosName("192.168.1.20 <00> UNIQUE Registered").Should().BeNull();
    }

    private static string? ParseNetBiosName(string output)
    {
        var method = typeof(WifiLanScanner).GetMethod(
            "ParseNetBiosName",
            BindingFlags.Static | BindingFlags.NonPublic);
        method.Should().NotBeNull();

        return (string?)method!.Invoke(null, new object?[] { output });
    }

    private static string? ParsePingName(string output, string ip)
    {
        var method = typeof(WifiLanScanner).GetMethod(
            "ParsePingName",
            BindingFlags.Static | BindingFlags.NonPublic);
        method.Should().NotBeNull();

        return (string?)method!.Invoke(null, new object?[] { output, ip });
    }

    private static string? ExtractMdnsFriendlyName(string rawName)
    {
        var method = typeof(WifiLanScanner).GetMethod(
            "ExtractMdnsFriendlyName",
            BindingFlags.Static | BindingFlags.NonPublic);
        method.Should().NotBeNull();

        return (string?)method!.Invoke(null, new object?[] { rawName });
    }

    private static Dictionary<string, string> ParseSsdpHeaders(string response)
    {
        var method = typeof(WifiLanScanner).GetMethod(
            "ParseSsdpHeaders",
            BindingFlags.Static | BindingFlags.NonPublic);
        method.Should().NotBeNull();

        return (Dictionary<string, string>)method!.Invoke(null, new object?[] { response })!;
    }

    private static string? ParseSsdpFriendlyName(string xml)
    {
        var method = typeof(WifiLanScanner).GetMethod(
            "ParseSsdpFriendlyName",
            BindingFlags.Static | BindingFlags.NonPublic);
        method.Should().NotBeNull();

        return (string?)method!.Invoke(null, new object?[] { xml });
    }

    private static bool TryCreateSafePrivateUri(string rawLocation, string basePrefix, out Uri? uri)
    {
        var method = typeof(WifiLanScanner).GetMethod(
            "TryCreateSafePrivateUri",
            BindingFlags.Static | BindingFlags.NonPublic);
        method.Should().NotBeNull();

        object?[] args = [rawLocation, basePrefix, null];
        var result = (bool)method!.Invoke(null, args)!;
        uri = (Uri?)args[2];
        return result;
    }

    private static object DetectNetworkRole(
        string ip,
        string? mac,
        string? gatewayIp,
        string? connectedBssid,
        bool isThisPc)
    {
        var method = typeof(WifiLanScanner).GetMethod(
            "DetectNetworkRole",
            BindingFlags.Static | BindingFlags.NonPublic);
        method.Should().NotBeNull();

        var role = method!.Invoke(null, new object?[] { ip, mac, gatewayIp, connectedBssid, isThisPc });
        role.Should().NotBeNull();
        return role!;
    }

    private static T GetProperty<T>(object instance, string propertyName)
    {
        var property = instance.GetType().GetProperty(propertyName);
        property.Should().NotBeNull();

        return property!.GetValue(instance).Should().BeAssignableTo<T>().Subject;
    }
}
