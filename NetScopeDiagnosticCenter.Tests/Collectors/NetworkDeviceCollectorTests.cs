using NetScopeDiagnosticCenter.Collectors;
using NetScopeDiagnosticCenter.Core.Models;
using NetScopeDiagnosticCenter.Infrastructure;

namespace NetScopeDiagnosticCenter.Tests.Collectors;

public sealed class NetworkDeviceCollectorTests
{
    [Fact]
    public async Task IdentifyAsync_InvalidTarget_ReturnsValidationResultBeforeNetwork()
    {
        var collector = new NetworkDeviceCollector(new PowerShellRunner(new NullLogger()), new SnmpClientService());

        var result = await collector.IdentifyAsync(new SnmpSessionOptions
        {
            Target = "https://example.com/device",
            Community = "private-community"
        });

        result.Verdict.Should().Be("Network device target is invalid.");
        result.Severity.Should().Be("Warning");
        result.SnmpStatus.Should().Be("Not tested");
        result.DnsStatus.Should().Be("Invalid target");
        result.Evidence.Should().ContainSingle(item => item.Contains("host name or IP address"));
        result.Recommendations.Should().ContainSingle(item => item.Contains("without protocol"));
        result.Recommendations.Should().NotContain(item => item.Contains("not reachable", StringComparison.OrdinalIgnoreCase));
        result.UnknownFindings.Should().ContainSingle(item => item.Contains("target input format is invalid"));
        result.AffectedLayer.Should().Be("Target input");
        result.OwnerSuggestion.Should().Be("Technician");
        result.Confidence.Should().Be("High");
    }

    [Fact]
    public async Task IdentifyAsync_PreCancelledToken_ThrowsOperationCanceled()
    {
        var collector = new NetworkDeviceCollector(new PowerShellRunner(new NullLogger()), new SnmpClientService());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await FluentActions.Invoking(() => collector.IdentifyAsync(
                new SnmpSessionOptions { Target = "192.168.1.1" },
                cancellation.Token))
            .Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task IdentifyAsync_PublicIp_ReturnsSafetyResultBeforeReachability()
    {
        var collector = new NetworkDeviceCollector(new PowerShellRunner(new NullLogger()), new SnmpClientService());

        var result = await collector.IdentifyAsync(new SnmpSessionOptions
        {
            Target = "8.8.8.8",
            Community = "public"
        });

        result.Verdict.Should().Be("Target rejected by safety policy.");
        result.Severity.Should().Be("Critical");
        result.SnmpStatus.Should().Be("Not tested");
        result.DnsStatus.Should().Be("Rejected");
        result.Ports.Should().BeEmpty();
        result.PingReachable.Should().BeFalse();
        result.Evidence.Should().ContainSingle(item => item.Contains("authorized private IPv4"));
        result.Limitations.Should().ContainSingle(item => item.Contains("RFC-1918"));
        result.Recommendations.Should().ContainSingle(item => item.Contains("RFC-1918"));
        result.Recommendations.Should().NotContain(item => item.Contains("not reachable", StringComparison.OrdinalIgnoreCase));
        result.ProbableFindings.Should().BeEmpty();
        result.UnknownFindings.Should().ContainSingle(item => item.Contains("outside the authorized private IPv4 scope"));
        result.AffectedLayer.Should().Be("Target validation");
        result.OwnerSuggestion.Should().Be("Technician");
        result.Confidence.Should().Be("High");
    }

    [Fact]
    public async Task IdentifyAsync_SnmpV3MissingCredentials_DoesNotReportDeviceOffline()
    {
        var collector = new NetworkDeviceCollector(new PowerShellRunner(new NullLogger()), new SnmpClientService());

        var result = await collector.IdentifyAsync(new SnmpSessionOptions
        {
            Target = "192.168.1.1",
            Protocol = SnmpProtocolVersion.V3AuthPriv,
            UserName = "monitor",
            AuthPassword = "auth-secret",
            PrivacyPassword = ""
        });

        result.Verdict.Should().Be("SNMPv3 credentials are incomplete.");
        result.Severity.Should().Be("Warning");
        result.SnmpStatus.Should().Be("Credentials missing");
        result.Ports.Should().BeEmpty();
        result.Recommendations.Should().ContainSingle(item => item.Contains("SNMPv3 authPriv"));
        result.Recommendations.Should().NotContain(item => item.Contains("not reachable", StringComparison.OrdinalIgnoreCase));
        result.ProbableFindings.Should().ContainSingle(item => item.Contains("credential input is incomplete"));
        result.UnknownFindings.Should().BeEmpty();
        result.AffectedLayer.Should().Be("SNMP credentials");
        result.Confidence.Should().Be("High");
    }

    private sealed class NullLogger : ILoggingService
    {
        public void Debug(string message) { }
        public void Info(string message) { }
        public void Warning(string message) { }
        public void Error(string message, Exception? exception = null) { }
    }

    // ---- Discovery classification decision table (two-tier scan) ----

    private static NetworkDeviceResult Discovered(
        string? vendor = null,
        string? netbios = null,
        string? rdns = null,
        bool isGateway = false,
        params int[] openPorts) => new()
    {
        Address = "192.168.1.50",
        MacVendor = vendor ?? string.Empty,
        NetBiosName = netbios ?? string.Empty,
        ReverseDnsName = rdns ?? string.Empty,
        IsGateway = isGateway,
        Ports = openPorts.Select(port => new PortProbeResult { Port = port, TcpSucceeded = true }).ToList()
    };

    [Fact]
    public void Classify_PrinterPort_WinsWithHighConfidence()
    {
        var type = NetworkDeviceCollector.ClassifyDiscoveredDevice(Discovered(openPorts: [9100, 80]), out var confidence);

        type.Should().Be("Printer");
        confidence.Should().Be("High");
    }

    [Fact]
    public void Classify_Gateway_IsRouter()
    {
        var type = NetworkDeviceCollector.ClassifyDiscoveredDevice(Discovered(isGateway: true, openPorts: [80, 443]), out var confidence);

        type.Should().Be("Router / gateway");
        confidence.Should().Be("High");
    }

    [Fact]
    public void Classify_SmbWithNetbios_IsWindowsPc()
    {
        var type = NetworkDeviceCollector.ClassifyDiscoveredDevice(
            Discovered(netbios: "DESKTOP-AB12", openPorts: [445]), out var confidence);

        type.Should().Be("Windows PC / server");
        confidence.Should().Be("Medium");
    }

    [Fact]
    public void Classify_CastPort_IsTvMedia()
    {
        var type = NetworkDeviceCollector.ClassifyDiscoveredDevice(Discovered(openPorts: [8009]), out _);

        type.Should().Be("TV / media (cast)");
    }

    [Theory]
    [InlineData("Apple, Inc.", "Apple device (iPhone/iPad/Mac)")]
    [InlineData("Samsung Electronics", "Probably phone / tablet")]
    [InlineData("Espressif Inc.", "IoT / smart home")]
    [InlineData("TP-Link Technologies", "Probably network device")]
    [InlineData("Hewlett Packard", "Probably printer")]
    public void Classify_VendorHints_AreHonestlyLowConfidence(string vendor, string expectedType)
    {
        var type = NetworkDeviceCollector.ClassifyDiscoveredDevice(Discovered(vendor: vendor), out var confidence);

        type.Should().Be(expectedType);
        confidence.Should().Be("Low");
    }

    [Fact]
    public void Classify_NoSignals_IsUnknownOnline()
    {
        var type = NetworkDeviceCollector.ClassifyDiscoveredDevice(Discovered(), out var confidence);

        type.Should().Be("Unknown device (online)");
        confidence.Should().Be("Low");
    }

    [Fact]
    public void ApplySelfIdentity_MarksOwnAddress_AsThisPc()
    {
        var self = Discovered(rdns: "Silviu.telenet.be", openPorts: [445]);
        self.Address = "192.168.0.210";

        NetworkDeviceCollector.ApplySelfIdentity(self, "192.168.0.210", "SILVIU");

        self.DeviceType.Should().Be("This PC (running the scan)");
        self.NetBiosName.Should().Be("SILVIU");
        self.DisplayName.Should().Be("SILVIU", "the machine name beats the ISP's reverse-DNS name");
        self.ConfirmationStatus.Should().Be("This computer");

        var other = Discovered(openPorts: [445]);
        other.Address = "192.168.0.50";
        NetworkDeviceCollector.ApplySelfIdentity(other, "192.168.0.210", "SILVIU");
        other.DeviceType.Should().NotBe("This PC (running the scan)", "only the scanner's own address gets the self label");
    }

    [Fact]
    public void DisplayName_PrefersLabelThenNamesThenVendor()
    {
        var device = Discovered(vendor: "Apple, Inc.", netbios: "DESKTOP-X", rdns: "host.lan");

        device.DisplayName.Should().Be("DESKTOP-X", "NetBIOS outranks reverse DNS and vendor");

        device.FriendlyLabel = "Laptop contabilitate";
        device.DisplayName.Should().Be("Laptop contabilitate", "the technician label outranks everything");

        var vendorOnly = Discovered(vendor: "Apple, Inc.");
        vendorOnly.DisplayName.Should().Be("Apple, Inc. device");
    }
}
