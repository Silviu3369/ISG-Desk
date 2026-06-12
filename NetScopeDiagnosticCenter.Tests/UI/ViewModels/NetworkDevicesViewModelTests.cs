using System.IO;
using NetScopeDiagnosticCenter.Collectors;
using NetScopeDiagnosticCenter.Collectors.Wifi;
using NetScopeDiagnosticCenter.Core.Models;
using NetScopeDiagnosticCenter.Infrastructure;
using NetScopeDiagnosticCenter.UI.ViewModels;

namespace NetScopeDiagnosticCenter.Tests.UI.ViewModels;

/// <summary>
/// Tests for NetworkDevicesViewModel state management and SNMP option building.
/// Async commands (real SNMP/PowerShell I/O) are excluded — covered by manual runs.
/// </summary>
public class NetworkDevicesViewModelTests
{
    private sealed class FakeHost : NetworkDevicesViewModel.IHost
    {
        public List<string> StatusMessages { get; } = [];
        public List<bool> BusyTransitions { get; } = [];
        public List<NetworkDeviceResult> AttachedDevices { get; } = [];
        public List<NetworkDeviceScanResult> AttachedScans { get; } = [];

        public void NotifyStatus(string message) => StatusMessages.Add(message);
        public void NotifyBusy(bool busy) => BusyTransitions.Add(busy);
        public void AttachNetworkDevice(NetworkDeviceResult device) => AttachedDevices.Add(device);
        public void AttachNetworkDeviceScan(NetworkDeviceScanResult scan) => AttachedScans.Add(scan);
    }

    private sealed class NullLogger : ILoggingService
    {
        public void Debug(string message) { }
        public void Info(string message) { }
        public void Warning(string message) { }
        public void Error(string message, Exception? exception = null) { }
    }

    private sealed class FakeNetworkDeviceCollector : NetworkDeviceCollector
    {
        private readonly Func<SnmpSessionOptions, NetworkDeviceResult> _identifyFactory;
        private readonly Func<SnmpSessionOptions, NetworkDeviceResult> _interfacesFactory;
        private readonly Func<NetworkScanRangeInfo>? _detectFactory;
        private readonly Func<SnmpSessionOptions, NetworkDeviceScanResult> _localScanFactory;
        private readonly Func<string, SnmpSessionOptions, NetworkDeviceScanResult> _rangeScanFactory;

        public FakeNetworkDeviceCollector(
            Func<SnmpSessionOptions, NetworkDeviceResult>? identifyFactory = null,
            Func<SnmpSessionOptions, NetworkDeviceResult>? interfacesFactory = null,
            Func<NetworkScanRangeInfo>? detectFactory = null,
            Func<SnmpSessionOptions, NetworkDeviceScanResult>? localScanFactory = null,
            Func<string, SnmpSessionOptions, NetworkDeviceScanResult>? rangeScanFactory = null)
            : base(new PowerShellRunner(new NullLogger()), new SnmpClientService())
        {
            _identifyFactory = identifyFactory ?? (options => new NetworkDeviceResult
            {
                Target = options.Target,
                Address = options.Target,
                Verdict = "SNMP device identity confirmed.",
                Severity = "OK",
                SnmpProtocol = options.Protocol,
                SnmpStatus = "Responded"
            });
            _interfacesFactory = interfacesFactory ?? (options => new NetworkDeviceResult
            {
                Target = options.Target,
                Address = options.Target,
                Verdict = "Interface counters were read.",
                Severity = "OK",
                SnmpProtocol = options.Protocol,
                SnmpStatus = "Responded",
                Interfaces =
                [
                    new NetworkDeviceInterfaceInfo { Index = 1, Name = "Gi1/0/1", OperStatus = "Up" }
                ]
            });
            _detectFactory = detectFactory;
            _localScanFactory = localScanFactory ?? (options => new NetworkDeviceScanResult
            {
                Source = "192.168.1.0/24",
                Verdict = "Found 1 SNMP network devices.",
                Severity = "OK",
                SnmpProtocol = options.Protocol,
                Devices = [new NetworkDeviceResult { Address = "192.168.1.10", Verdict = "SNMP device identity confirmed.", Severity = "OK" }]
            });
            _rangeScanFactory = rangeScanFactory ?? ((range, options) => new NetworkDeviceScanResult
            {
                Source = range,
                Verdict = "Found 1 SNMP network devices.",
                Severity = "OK",
                SnmpProtocol = options.Protocol,
                Devices = [new NetworkDeviceResult { Address = "192.168.1.20", Verdict = "SNMP device identity confirmed.", Severity = "OK" }]
            });
        }

        public List<SnmpSessionOptions> IdentifyRequests { get; } = [];
        public List<SnmpSessionOptions> InterfaceRequests { get; } = [];
        public List<SnmpSessionOptions> LocalScanRequests { get; } = [];
        public List<(string Range, SnmpSessionOptions Options)> RangeScanRequests { get; } = [];
        public int DetectRequests { get; private set; }

        public override Task<NetworkDeviceResult> IdentifyAsync(
            SnmpSessionOptions options,
            CancellationToken cancellationToken = default)
        {
            IdentifyRequests.Add(Clone(options));
            return Task.FromResult(_identifyFactory(options));
        }

        public override Task<NetworkDeviceResult> ReadInterfacesAsync(
            SnmpSessionOptions options,
            CancellationToken cancellationToken = default)
        {
            InterfaceRequests.Add(Clone(options));
            return Task.FromResult(_interfacesFactory(options));
        }

        public override Task<NetworkScanRangeInfo> DetectLocalScanRangeAsync(
            CancellationToken cancellationToken = default)
        {
            DetectRequests++;
            return Task.FromResult(_detectFactory?.Invoke() ?? new NetworkScanRangeInfo
            {
                LocalIpAddress = "192.168.1.44",
                AdapterName = "Ethernet",
                ScanRange = "192.168.1.0/24"
            });
        }

        public override Task<NetworkDeviceScanResult> ScanLocalSubnetAsync(
            SnmpSessionOptions options,
            CancellationToken cancellationToken = default,
            Action<NetworkDeviceScanResult>? progress = null)
        {
            LocalScanRequests.Add(Clone(options));
            var result = _localScanFactory(options);
            progress?.Invoke(result);
            return Task.FromResult(result);
        }

        public override Task<NetworkDeviceScanResult> ScanRangeAsync(
            string rangeInput,
            SnmpSessionOptions options,
            CancellationToken cancellationToken = default,
            Action<NetworkDeviceScanResult>? progress = null)
        {
            RangeScanRequests.Add((rangeInput, Clone(options)));
            var result = _rangeScanFactory(rangeInput, options);
            progress?.Invoke(result);
            return Task.FromResult(result);
        }

        private static SnmpSessionOptions Clone(SnmpSessionOptions options) => new()
        {
            Protocol = options.Protocol,
            Target = options.Target,
            Community = options.Community,
            UserName = options.UserName,
            AuthProtocol = options.AuthProtocol,
            AuthPassword = options.AuthPassword,
            PrivacyProtocol = options.PrivacyProtocol,
            PrivacyPassword = options.PrivacyPassword,
            TimeoutMs = options.TimeoutMs
        };
    }

    private static (NetworkDevicesViewModel vm, FakeHost host, SnmpCredentialStore store) Build(
        SnmpCredentialStore? store = null,
        NetworkDeviceCollector? collector = null,
        WifiDeviceFriendlyNameStore? labelStore = null)
    {
        var psRunner = new PowerShellRunner(new NullLogger());
        var snmp = new SnmpClientService();
        collector ??= new NetworkDeviceCollector(psRunner, snmp);
        var host = new FakeHost();
        store ??= new SnmpCredentialStore(new SecureCredentialService(
            new AppStorageService(Path.Combine(Path.GetTempPath(), "NetScopeTests-NetworkDevices-" + Guid.NewGuid().ToString("N")))));
        var vm = new NetworkDevicesViewModel(collector, new NullLogger(), host, store, labelStore);
        return (vm, host, store);
    }

    private static WifiDeviceFriendlyNameStore BuildLabelStore() =>
        new(new AppStorageService(Path.Combine(Path.GetTempPath(), "NetScopeTests-DeviceLabels-" + Guid.NewGuid().ToString("N"))));

    [Fact]
    public void Construction_DefaultsAreSensible()
    {
        var (vm, _, _) = Build();

        vm.NetworkDeviceSelectedSnmpProtocol.Should().Be(SnmpProtocolVersion.V2C);
        vm.NetworkDeviceCommunity.Should().Be("public");
        vm.NetworkDeviceSnmpV3AuthProtocol.Should().Be("SHA256");
        vm.NetworkDeviceSnmpV3PrivacyProtocol.Should().Be("AES128");
        vm.IsNetworkDeviceSnmpV2Selected.Should().BeTrue();
        vm.IsNetworkDeviceSnmpV3Selected.Should().BeFalse();
        vm.NetworkDeviceInterfaceFilter.Should().Be("All");
        vm.NetworkDeviceDetectedIpDisplay.Should().Be("Unknown");
        vm.NetworkDeviceDetectedRangeDisplay.Should().Be("Unknown");
    }

    [Fact]
    public void LookupLists_AreSensible()
    {
        var (vm, _, _) = Build();

        vm.NetworkDeviceSnmpProtocols.Should().Contain(SnmpProtocolVersion.V2C);
        vm.NetworkDeviceSnmpProtocols.Should().Contain(SnmpProtocolVersion.V3AuthPriv);
        vm.NetworkDeviceSnmpAuthProtocols.Should().Contain("SHA256");
        vm.NetworkDeviceSnmpPrivacyProtocols.Should().Contain("AES128");
        vm.NetworkDeviceInterfaceFilters.Should().Contain("All");
        vm.NetworkDeviceInterfaceFilters.Should().Contain("Needs Attention");
        vm.NetworkDeviceInterfaceFilters.Should().Contain("Down");
    }

    [Fact]
    public void SelectedSnmpProtocol_SwitchingFiresIsV3IsV2()
    {
        var (vm, _, _) = Build();
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        vm.NetworkDeviceSelectedSnmpProtocol = SnmpProtocolVersion.V3AuthPriv;

        vm.IsNetworkDeviceSnmpV3Selected.Should().BeTrue();
        vm.IsNetworkDeviceSnmpV2Selected.Should().BeFalse();
        changed.Should().Contain(nameof(NetworkDevicesViewModel.IsNetworkDeviceSnmpV3Selected));
        changed.Should().Contain(nameof(NetworkDevicesViewModel.IsNetworkDeviceSnmpV2Selected));
    }

    [Fact]
    public void BuildSnmpOptions_PassesAllFields()
    {
        var (vm, _, _) = Build();
        vm.NetworkDeviceCommunity = "ro-community";
        vm.NetworkDeviceSelectedSnmpProtocol = SnmpProtocolVersion.V3AuthPriv;
        vm.NetworkDeviceSnmpV3UserName = "monitor";
        vm.NetworkDeviceSnmpV3AuthPassword = "auth-secret";
        vm.NetworkDeviceSnmpV3PrivacyPassword = "priv-secret";

        var opts = vm.BuildNetworkDeviceSnmpOptions("10.0.0.1");

        opts.Target.Should().Be("10.0.0.1");
        opts.Protocol.Should().Be(SnmpProtocolVersion.V3AuthPriv);
        opts.Community.Should().Be("ro-community");
        opts.UserName.Should().Be("monitor");
        opts.AuthPassword.Should().Be("auth-secret");
        opts.PrivacyPassword.Should().Be("priv-secret");
        opts.TimeoutMs.Should().Be(2500);
    }

    [Fact]
    public void InterfaceFilter_RefreshesFilteredAndSummary()
    {
        var (vm, _, _) = Build();
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        vm.NetworkDeviceInterfaceFilter = "Down";

        changed.Should().Contain(nameof(NetworkDevicesViewModel.FilteredNetworkDeviceInterfaces));
        changed.Should().Contain(nameof(NetworkDevicesViewModel.NetworkDeviceInterfaceFilterSummary));
        changed.Should().Contain(nameof(NetworkDevicesViewModel.FilteredNetworkDeviceInterfacesEmptyText));
        changed.Should().Contain(nameof(NetworkDevicesViewModel.HasFilteredNetworkDeviceInterfaces));
        changed.Should().Contain(nameof(NetworkDevicesViewModel.HasNoFilteredNetworkDeviceInterfaces));
    }

    [Fact]
    public void InterfaceFilter_InvalidValue_FallsBackToAll()
    {
        var (vm, _, _) = Build();

        vm.NetworkDeviceInterfaceFilter = "not-a-filter";

        vm.NetworkDeviceInterfaceFilter.Should().Be("All");
    }

    [Fact]
    public void FilteredInterfaces_AppliesEachFilter()
    {
        var (vm, _, _) = Build();
        var device = new NetworkDeviceResult
        {
            Address = "10.0.0.1",
            Interfaces =
            [
                new NetworkDeviceInterfaceInfo { Index = 1, OperStatus = "Up", SpeedMbps = 1000, NeedsAttention = false },
                new NetworkDeviceInterfaceInfo { Index = 2, OperStatus = "Down", SpeedMbps = 1000, NeedsAttention = true },
                new NetworkDeviceInterfaceInfo { Index = 3, OperStatus = "Up", SpeedMbps = 100, NeedsAttention = true },
                new NetworkDeviceInterfaceInfo { Index = 4, OperStatus = "Up", SpeedMbps = 1000, Errors = 5, NeedsAttention = true },
                new NetworkDeviceInterfaceInfo { Index = 5, OperStatus = "Up", SpeedMbps = 1000, UtilizationPercent = 90 },
                new NetworkDeviceInterfaceInfo { Index = 6, OperStatus = "Up", SpeedMbps = null }
            ]
        };
        vm.LastNetworkDevice = device;

        vm.NetworkDeviceInterfaceFilter = "All";
        vm.FilteredNetworkDeviceInterfaces.Should().HaveCount(6);

        vm.NetworkDeviceInterfaceFilter = "Needs Attention";
        vm.FilteredNetworkDeviceInterfaces.Should().HaveCount(3);

        vm.NetworkDeviceInterfaceFilter = "Down";
        vm.FilteredNetworkDeviceInterfaces.Should().ContainSingle(i => i.Index == 2);

        vm.NetworkDeviceInterfaceFilter = "100 Mbps";
        vm.FilteredNetworkDeviceInterfaces.Should().ContainSingle(i => i.Index == 3);

        vm.NetworkDeviceInterfaceFilter = "Errors / Discards";
        vm.FilteredNetworkDeviceInterfaces.Should().ContainSingle(i => i.Index == 4);

        vm.NetworkDeviceInterfaceFilter = "High Traffic";
        vm.FilteredNetworkDeviceInterfaces.Should().ContainSingle(i => i.Index == 5);

        vm.NetworkDeviceInterfaceFilter = "Unknown Speed";
        vm.FilteredNetworkDeviceInterfaces.Should().ContainSingle(i => i.Index == 6);
    }

    [Fact]
    public void InterfaceFilterSummary_FormatsXOfY()
    {
        var (vm, _, _) = Build();
        vm.LastNetworkDevice = new NetworkDeviceResult
        {
            Address = "10.0.0.1",
            Interfaces =
            [
                new NetworkDeviceInterfaceInfo { Index = 1, OperStatus = "Up", SpeedMbps = 1000 },
                new NetworkDeviceInterfaceInfo { Index = 2, OperStatus = "Down", NeedsAttention = true }
            ]
        };

        vm.NetworkDeviceInterfaceFilter = "All";
        vm.NetworkDeviceInterfaceFilterSummary.Should().Be("2 of 2 interfaces shown");

        vm.NetworkDeviceInterfaceFilter = "Down";
        vm.NetworkDeviceInterfaceFilterSummary.Should().Be("1 of 2 interfaces shown");
    }

    [Fact]
    public void InterfaceTableState_NoInterfaces_ShowsIfMibEmptyText()
    {
        var (vm, _, _) = Build();

        vm.LastNetworkDevice = new NetworkDeviceResult();

        vm.HasFilteredNetworkDeviceInterfaces.Should().BeFalse();
        vm.HasNoFilteredNetworkDeviceInterfaces.Should().BeTrue();
        vm.FilteredNetworkDeviceInterfacesEmptyText.Should().Be("No IF-MIB interface rows were returned for this result.");
    }

    [Fact]
    public void InterfaceTableState_FilterWithoutMatches_ShowsFilterEmptyText()
    {
        var (vm, _, _) = Build();
        vm.LastNetworkDevice = new NetworkDeviceResult
        {
            Interfaces =
            [
                new NetworkDeviceInterfaceInfo { Index = 1, OperStatus = "Up", SpeedMbps = 1000 }
            ]
        };

        vm.NetworkDeviceInterfaceFilter = "Down";

        vm.HasFilteredNetworkDeviceInterfaces.Should().BeFalse();
        vm.HasNoFilteredNetworkDeviceInterfaces.Should().BeTrue();
        vm.FilteredNetworkDeviceInterfacesEmptyText.Should().Be("No interfaces match the 'Down' filter.");
    }

    [Fact]
    public void PortsNeedingAttentionFlags_ReflectCurrentResult()
    {
        var (vm, _, _) = Build();
        var attention = new NetworkDeviceInterfaceInfo { Index = 2, Name = "Gi1/0/2", NeedsAttention = true };

        vm.LastNetworkDevice = new NetworkDeviceResult();
        vm.HasNetworkDevicePortsNeedingAttention.Should().BeFalse();
        vm.HasNoNetworkDevicePortsNeedingAttention.Should().BeTrue();

        vm.LastNetworkDevice = new NetworkDeviceResult
        {
            PortsNeedingAttention = [attention]
        };
        vm.HasNetworkDevicePortsNeedingAttention.Should().BeTrue();
        vm.HasNoNetworkDevicePortsNeedingAttention.Should().BeFalse();
    }

    [Fact]
    public void LastNetworkDevice_WithFilterThatHidesNewResult_ResetsFilterToAll()
    {
        var (vm, _, _) = Build();
        vm.NetworkDeviceInterfaceFilter = "Down";

        vm.LastNetworkDevice = new NetworkDeviceResult
        {
            Address = "10.0.0.1",
            Interfaces =
            [
                new NetworkDeviceInterfaceInfo { Index = 1, OperStatus = "Up", SpeedMbps = 1000 },
                new NetworkDeviceInterfaceInfo { Index = 2, OperStatus = "Up", SpeedMbps = 1000 }
            ]
        };

        vm.NetworkDeviceInterfaceFilter.Should().Be("All");
        vm.FilteredNetworkDeviceInterfaces.Should().HaveCount(2);
        vm.NetworkDeviceInterfaceFilterSummary.Should().Be("2 of 2 interfaces shown");
    }

    [Fact]
    public void LastNetworkDevice_WithFilterThatStillMatches_KeepsFilter()
    {
        var (vm, _, _) = Build();
        vm.NetworkDeviceInterfaceFilter = "Down";

        vm.LastNetworkDevice = new NetworkDeviceResult
        {
            Address = "10.0.0.1",
            Interfaces =
            [
                new NetworkDeviceInterfaceInfo { Index = 1, OperStatus = "Up", SpeedMbps = 1000 },
                new NetworkDeviceInterfaceInfo { Index = 2, OperStatus = "Down", SpeedMbps = 1000, NeedsAttention = true }
            ]
        };

        vm.NetworkDeviceInterfaceFilter.Should().Be("Down");
        vm.FilteredNetworkDeviceInterfaces.Should().ContainSingle(item => item.Index == 2);
    }

    [Fact]
    public void SettingLastNetworkDevice_RefreshesInterfaceBindings()
    {
        var (vm, _, _) = Build();
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        vm.LastNetworkDevice = new NetworkDeviceResult { Address = "10.0.0.1" };

        changed.Should().Contain(nameof(NetworkDevicesViewModel.FilteredNetworkDeviceInterfaces));
        changed.Should().Contain(nameof(NetworkDevicesViewModel.NetworkDeviceInterfaceFilterSummary));
        changed.Should().Contain(nameof(NetworkDevicesViewModel.FilteredNetworkDeviceInterfacesEmptyText));
        changed.Should().Contain(nameof(NetworkDevicesViewModel.HasNetworkDevicePortsNeedingAttention));
        changed.Should().Contain(nameof(NetworkDevicesViewModel.HasNoNetworkDevicePortsNeedingAttention));
        changed.Should().Contain(nameof(NetworkDevicesViewModel.HasFilteredNetworkDeviceInterfaces));
        changed.Should().Contain(nameof(NetworkDevicesViewModel.HasNoFilteredNetworkDeviceInterfaces));
    }

    [Fact]
    public void SettingLastNetworkDevice_RefreshesDiagnosticListFlags()
    {
        var (vm, _, _) = Build();
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        vm.LastNetworkDevice = new NetworkDeviceResult
        {
            ConfirmedFindings = ["SNMP responded."],
            ProbableFindings = [],
            UnknownFindings = ["Interface counters unavailable."],
            Evidence = ["Resolved target to 192.168.1.10."],
            Limitations = ["SNMP is read-only."],
            Recommendations = ["Check UDP 161."]
        };

        vm.HasNetworkDeviceConfirmedFindings.Should().BeTrue();
        vm.HasNoNetworkDeviceProbableFindings.Should().BeTrue();
        vm.HasNetworkDeviceUnknownFindings.Should().BeTrue();
        vm.HasNetworkDeviceEvidence.Should().BeTrue();
        vm.HasNetworkDeviceLimitations.Should().BeTrue();
        vm.HasNetworkDeviceRecommendations.Should().BeTrue();
        changed.Should().Contain(nameof(NetworkDevicesViewModel.HasNetworkDeviceConfirmedFindings));
        changed.Should().Contain(nameof(NetworkDevicesViewModel.HasNoNetworkDeviceProbableFindings));
        changed.Should().Contain(nameof(NetworkDevicesViewModel.HasNetworkDeviceEvidence));
        changed.Should().Contain(nameof(NetworkDevicesViewModel.HasNetworkDeviceLimitations));
        changed.Should().Contain(nameof(NetworkDevicesViewModel.HasNetworkDeviceRecommendations));
    }

    [Fact]
    public void SettingLastNetworkDevice_WithEmptyEvidenceAndLimitations_SetsEmptyFlags()
    {
        var (vm, _, _) = Build();

        vm.LastNetworkDevice = new NetworkDeviceResult();

        vm.HasNoNetworkDeviceEvidence.Should().BeTrue();
        vm.HasNoNetworkDeviceLimitations.Should().BeTrue();
        vm.HasNetworkDeviceEvidence.Should().BeFalse();
        vm.HasNetworkDeviceLimitations.Should().BeFalse();
    }

    [Fact]
    public void BuildNetworkDeviceTicketSummary_NoData_ReturnsEmpty()
    {
        var (vm, _, _) = Build();

        var summary = vm.BuildNetworkDeviceTicketSummary(new DateTimeOffset(2026, 6, 2, 8, 30, 0, TimeSpan.Zero));

        summary.Should().BeEmpty();
    }

    [Fact]
    public void BuildNetworkDeviceTicketSummary_IncludesFindingsRecommendationsScanAndAscii()
    {
        var (vm, _, _) = Build();
        vm.LastNetworkDevice = new NetworkDeviceResult
        {
            Target = "switch-01",
            Address = "192.168.1.10",
            Verdict = "Read 2 interfaces; 1 need attention.",
            Severity = "Warning",
            DeviceType = "Switch",
            Confidence = "High",
            DnsStatus = "Resolved",
            PingStatus = "Reachable",
            MacAddress = "00-11-22-33-44-55",
            MacVendor = "Contoso",
            ConfirmedFindings = ["SNMP responded using SNMP v2c."],
            ProbableFindings = ["Interface 2 has physical layer symptoms."],
            UnknownFindings = ["Vendor inventory was not queried."],
            Evidence = ["Resolved target switch-01 to 192.168.1.10.", "SNMP identity returned switch-01."],
            Limitations = ["SNMP is read-only and does not change device configuration."],
            Recommendations = ["Check cable and switch counter history."],
            Identity = new SnmpDeviceInfo
            {
                Address = "192.168.1.10",
                Protocol = SnmpProtocolVersion.V2C,
                SysName = "switch-01",
                SysDescr = "Managed switch",
                SysLocation = "Closet A",
                SysContact = "network@example.com",
                SysUpTime = "12d 4h 3m"
            },
            Interfaces =
            [
                new NetworkDeviceInterfaceInfo { Index = 1, Name = "Gi1/0/1", AdminStatus = "Up", OperStatus = "Up", SpeedText = "1,000 Mbps", Verdict = "OK" },
                new NetworkDeviceInterfaceInfo { Index = 2, Name = "Gi1/0/2", AdminStatus = "Up", OperStatus = "Down", SpeedText = "Unknown", Verdict = "Port down" }
            ],
            PortsNeedingAttention =
            [
                new NetworkDeviceInterfaceInfo
                {
                    Index = 2,
                    Name = "Gi1/0/2",
                    AttentionReason = "Port down",
                    RecommendedNextCheck = "Check endpoint power and cabling."
                }
            ]
        };
        vm.LastNetworkDeviceScan = new NetworkDeviceScanResult
        {
            Verdict = "Found 1 SNMP network devices.",
            ScannedHosts = 10,
            Devices =
            [
                new NetworkDeviceResult
                {
                    Address = "192.168.1.10",
                    DeviceType = "Switch",
                    Identity = new SnmpDeviceInfo { SysName = "switch-01" },
                    ConfirmationStatus = "Confirmed by SNMP"
                }
            ]
        };

        var summary = vm.BuildNetworkDeviceTicketSummary(new DateTimeOffset(2026, 6, 2, 8, 30, 0, TimeSpan.Zero));

        summary.Should().Contain("ISG Desk - Network Device Summary (2026-06-02 08:30:00)");
        summary.Should().Contain("CONFIRMED FINDINGS");
        summary.Should().Contain("- SNMP responded using SNMP v2c.");
        summary.Should().Contain("EVIDENCE");
        summary.Should().Contain("- Resolved target switch-01 to 192.168.1.10.");
        summary.Should().Contain("LIMITATIONS");
        summary.Should().Contain("- SNMP is read-only and does not change device configuration.");
        summary.Should().Contain("RECOMMENDED NEXT CHECKS");
        summary.Should().Contain("- Check cable and switch counter history.");
        summary.Should().Contain("SNMP IDENTITY");
        summary.Should().Contain("- Name       : switch-01");
        summary.Should().Contain("- Contact    : network@example.com");
        summary.Should().Contain("NEEDS ATTENTION (1)");
        summary.Should().Contain("LAN SCAN - Found 1 SNMP network devices.");
        summary.Where(character => character > 127).Should().BeEmpty();
    }

    [Fact]
    public void BuildNetworkDeviceTicketSummary_UsesUnknownForEmptyInterfaceFields()
    {
        var (vm, _, _) = Build();
        vm.LastNetworkDevice = new NetworkDeviceResult
        {
            Interfaces =
            [
                new NetworkDeviceInterfaceInfo
                {
                    Name = "",
                    AdminStatus = "",
                    OperStatus = "",
                    SpeedText = "",
                    Verdict = ""
                }
            ],
            PortsNeedingAttention =
            [
                new NetworkDeviceInterfaceInfo
                {
                    Name = "",
                    AttentionReason = "",
                    RecommendedNextCheck = ""
                }
            ]
        };

        var summary = vm.BuildNetworkDeviceTicketSummary(new DateTimeOffset(2026, 6, 2, 8, 30, 0, TimeSpan.Zero));

        summary.Should().Contain("- Unknown | admin Unknown/oper Unknown | Unknown | err 0 disc 0 | Unknown");
        summary.Should().Contain("- Unknown: Unknown - Unknown");
    }

    [Fact]
    public void SettingLastNetworkDeviceScan_StoresScanResult()
    {
        var (vm, _, _) = Build();
        var first = new NetworkDeviceResult { Address = "10.0.0.10" };
        var scan = new NetworkDeviceScanResult
        {
            Source = "10.0.0.0/24",
            Devices = [first]
        };

        vm.LastNetworkDeviceScan = scan;

        vm.LastNetworkDeviceScan.Should().BeSameAs(scan);
        vm.SelectedNetworkDeviceScanResult.Should().BeSameAs(first);
        vm.NetworkDeviceTarget.Should().Be("10.0.0.10");
        vm.HasNetworkDeviceScanResults.Should().BeTrue();
        vm.HasNoNetworkDeviceScanResults.Should().BeFalse();
        vm.HasSelectedNetworkDeviceScanResult.Should().BeTrue();
        vm.SelectedNetworkDeviceScanResultSummary.Should().Contain("10.0.0.10");
    }

    [Fact]
    public void LastNetworkDeviceScan_EmptyScanClearsScanDerivedTarget()
    {
        var (vm, _, _) = Build();
        vm.LastNetworkDeviceScan = new NetworkDeviceScanResult
        {
            Source = "10.0.0.0/24",
            Devices = [new NetworkDeviceResult { Address = "10.0.0.10" }]
        };

        vm.LastNetworkDeviceScan = new NetworkDeviceScanResult
        {
            Source = "10.0.0.0/24",
            Verdict = "No SNMP network devices responded.",
            Severity = "Warning"
        };

        vm.SelectedNetworkDeviceScanResult.Should().BeNull();
        vm.NetworkDeviceTarget.Should().BeEmpty();
        vm.HasNetworkDeviceScanResults.Should().BeFalse();
        vm.HasNoNetworkDeviceScanResults.Should().BeTrue();
        vm.HasSelectedNetworkDeviceScanResult.Should().BeFalse();
        vm.NetworkDeviceScanResultsEmptyText.Should().Contain("No live devices");
    }

    [Fact]
    public void LastNetworkDeviceScan_EmptyScanKeepsManualTarget()
    {
        var (vm, _, _) = Build();
        vm.NetworkDeviceTarget = "10.0.0.99";

        vm.LastNetworkDeviceScan = new NetworkDeviceScanResult
        {
            Source = "10.0.0.0/24",
            Verdict = "No SNMP network devices responded.",
            Severity = "Warning"
        };

        vm.SelectedNetworkDeviceScanResult.Should().BeNull();
        vm.NetworkDeviceTarget.Should().Be("10.0.0.99");
        vm.HasNoNetworkDeviceScanResults.Should().BeTrue();
    }

    [Fact]
    public void SelectedNetworkDeviceScanResult_UpdatesManualTarget()
    {
        var (vm, _, _) = Build();
        var device = new NetworkDeviceResult { Address = "10.0.0.25" };

        vm.SelectedNetworkDeviceScanResult = device;

        vm.NetworkDeviceTarget.Should().Be("10.0.0.25");
        vm.HasSelectedNetworkDeviceScanResult.Should().BeTrue();
        vm.SelectedNetworkDeviceScanResultSummary.Should().Contain("10.0.0.25");
    }

    [Fact]
    public void SelectedNetworkDeviceScanResult_NotifiesNetworkDeviceTarget()
    {
        var (vm, _, _) = Build();
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        vm.SelectedNetworkDeviceScanResult = new NetworkDeviceResult { Address = "10.0.0.25" };

        changed.Should().Contain(nameof(vm.NetworkDeviceTarget));
        changed.Should().Contain(nameof(vm.HasSelectedNetworkDeviceScanResult));
    }

    [Fact]
    public void NetworkDeviceScanResultsEmptyText_UsesSkippedReasonWhenScanWasRejected()
    {
        var (vm, _, _) = Build();

        vm.LastNetworkDeviceScan = new NetworkDeviceScanResult
        {
            SkippedReason = "SNMPv3 authPriv requires username, auth password, and privacy password."
        };

        vm.HasNoNetworkDeviceScanResults.Should().BeTrue();
        vm.NetworkDeviceScanResultsEmptyText.Should().Be("SNMPv3 authPriv requires username, auth password, and privacy password.");
    }

    [Fact]
    public void CancelCommand_NotifiesStatus()
    {
        var (vm, host, _) = Build();
        vm.CancelNetworkDeviceScanCommand.Execute(null);
        host.StatusMessages.Should().Contain(s => s.Contains("Stopping network device scan"));
    }

    [Fact]
    public void Commands_AreNotNull()
    {
        var (vm, _, _) = Build();
        vm.IdentifyNetworkDeviceCommand.Should().NotBeNull();
        vm.ReadNetworkDeviceInterfacesCommand.Should().NotBeNull();
        vm.ReadSelectedNetworkDeviceInterfacesCommand.Should().NotBeNull();
        vm.DetectNetworkDeviceScanRangeCommand.Should().NotBeNull();
        vm.ScanLocalNetworkDevicesCommand.Should().NotBeNull();
        vm.ScanNetworkDeviceRangeCommand.Should().NotBeNull();
        vm.CancelNetworkDeviceScanCommand.Should().NotBeNull();
        vm.CopyNetworkDeviceSummaryCommand.Should().NotBeNull();
        vm.ForgetNetworkDeviceSnmpCredentialCommand.Should().NotBeNull();
    }

    [Fact]
    public async Task IdentifyAsync_EmptyTarget_NotifiesAndDoesNothing()
    {
        var (vm, host, _) = Build();
        vm.NetworkDeviceTarget = "";

        await ((NetScopeDiagnosticCenter.UI.AsyncRelayCommand)vm.IdentifyNetworkDeviceCommand).ExecuteAsync(null);

        host.StatusMessages.Should().Contain(s => s.Contains("Enter a network device target"));
        host.BusyTransitions.Should().BeEmpty();
        host.AttachedDevices.Should().BeEmpty();
    }

    [Fact]
    public async Task IdentifyAsync_ValidTarget_UsesCollectorAndAttachesResult()
    {
        var collector = new FakeNetworkDeviceCollector(options => new NetworkDeviceResult
        {
            Target = options.Target,
            Address = options.Target,
            Verdict = "SNMP device identity confirmed.",
            Severity = "OK",
            SnmpProtocol = options.Protocol,
            SnmpStatus = "Responded"
        });
        var (vm, host, _) = Build(collector: collector);
        vm.NetworkDeviceTarget = " 192.168.1.10 ";

        await ((NetScopeDiagnosticCenter.UI.AsyncRelayCommand)vm.IdentifyNetworkDeviceCommand).ExecuteAsync(null);

        collector.IdentifyRequests.Should().ContainSingle();
        collector.IdentifyRequests[0].Target.Should().Be("192.168.1.10");
        collector.IdentifyRequests[0].TimeoutMs.Should().Be(2500);
        vm.LastNetworkDevice.Should().NotBeNull();
        vm.LastNetworkDevice!.Address.Should().Be("192.168.1.10");
        host.AttachedDevices.Should().ContainSingle().Which.Should().BeSameAs(vm.LastNetworkDevice);
        host.BusyTransitions.Should().ContainInOrder(true, false);
        host.StatusMessages.Should().Contain(message => message.Contains("SNMP device identity confirmed"));
    }

    [Fact]
    public async Task IdentifyAsync_SaveNetworkDeviceSnmpCredentials_PersistsEncryptedCredential()
    {
        var collector = new FakeNetworkDeviceCollector(options => new NetworkDeviceResult
        {
            Target = options.Target,
            Address = options.Target,
            Verdict = "SNMP device identity confirmed.",
            Severity = "OK",
            SnmpProtocol = options.Protocol,
            SnmpStatus = "Responded"
        });
        var (vm, _, store) = Build(collector: collector);
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        vm.NetworkDeviceTarget = "192.168.1.10";
        vm.NetworkDeviceSelectedSnmpProtocol = SnmpProtocolVersion.V3AuthPriv;
        vm.NetworkDeviceSnmpV3UserName = "monitor";
        vm.NetworkDeviceSnmpV3AuthPassword = "auth-secret";
        vm.NetworkDeviceSnmpV3PrivacyPassword = "priv-secret";
        vm.SaveNetworkDeviceSnmpCredentials = true;

        await ((NetScopeDiagnosticCenter.UI.AsyncRelayCommand)vm.IdentifyNetworkDeviceCommand).ExecuteAsync(null);

        store.HasSavedCredentials.Should().BeTrue();
        vm.HasSavedNetworkDeviceSnmpCredentials.Should().BeTrue();
        changed.Should().Contain(nameof(NetworkDevicesViewModel.HasSavedNetworkDeviceSnmpCredentials));
    }

    [Fact]
    public void ForgetNetworkDeviceSnmpCredentialCommand_ClearsSavedCredentialsAndPasswords()
    {
        var (vm, host, store) = Build();
        vm.NetworkDeviceSelectedSnmpProtocol = SnmpProtocolVersion.V3AuthPriv;
        vm.NetworkDeviceSnmpV3UserName = "monitor";
        vm.NetworkDeviceSnmpV3AuthPassword = "auth-secret";
        vm.NetworkDeviceSnmpV3PrivacyPassword = "priv-secret";
        vm.SaveNetworkDeviceSnmpCredentials = true;
        store.Save();

        vm.ForgetNetworkDeviceSnmpCredentialCommand.Execute(null);

        store.HasSavedCredentials.Should().BeFalse();
        vm.HasSavedNetworkDeviceSnmpCredentials.Should().BeFalse();
        vm.SaveNetworkDeviceSnmpCredentials.Should().BeFalse();
        vm.NetworkDeviceSnmpV3UserName.Should().BeEmpty();
        vm.NetworkDeviceSnmpV3AuthPassword.Should().BeEmpty();
        vm.NetworkDeviceSnmpV3PrivacyPassword.Should().BeEmpty();
        host.StatusMessages.Should().Contain(message => message.Contains("removed"));
    }

    [Fact]
    public async Task IdentifyAsync_InvalidTarget_DoesNotCallCollector()
    {
        var collector = new FakeNetworkDeviceCollector();
        var (vm, host, _) = Build(collector: collector);
        vm.NetworkDeviceTarget = "https://example.com/switch";

        await ((NetScopeDiagnosticCenter.UI.AsyncRelayCommand)vm.IdentifyNetworkDeviceCommand).ExecuteAsync(null);

        collector.IdentifyRequests.Should().BeEmpty();
        host.BusyTransitions.Should().BeEmpty();
        vm.LastNetworkDevice.Should().NotBeNull();
        vm.LastNetworkDevice!.Verdict.Should().Be("Network device target is invalid.");
        vm.LastNetworkDevice.SnmpStatus.Should().Be("Not tested");
        host.AttachedDevices.Should().ContainSingle().Which.Should().BeSameAs(vm.LastNetworkDevice);
    }

    [Fact]
    public async Task IdentifyAsync_PublicIp_DoesNotCallCollector()
    {
        var collector = new FakeNetworkDeviceCollector();
        var (vm, host, _) = Build(collector: collector);
        vm.NetworkDeviceTarget = "8.8.8.8";

        await ((NetScopeDiagnosticCenter.UI.AsyncRelayCommand)vm.IdentifyNetworkDeviceCommand).ExecuteAsync(null);

        collector.IdentifyRequests.Should().BeEmpty();
        host.BusyTransitions.Should().BeEmpty();
        vm.LastNetworkDevice.Should().NotBeNull();
        vm.LastNetworkDevice!.Verdict.Should().Be("Target rejected by safety policy.");
        vm.LastNetworkDevice.Severity.Should().Be("Critical");
        vm.LastNetworkDevice.Evidence.Should().ContainSingle(message => message.Contains("authorized private IPv4"));
    }

    [Fact]
    public async Task IdentifyAsync_SnmpV3MissingCredentials_DoesNotCallCollector()
    {
        var collector = new FakeNetworkDeviceCollector();
        var (vm, host, _) = Build(collector: collector);
        vm.NetworkDeviceTarget = "192.168.1.10";
        vm.NetworkDeviceSelectedSnmpProtocol = SnmpProtocolVersion.V3AuthPriv;
        vm.NetworkDeviceSnmpV3UserName = "monitor";
        vm.NetworkDeviceSnmpV3AuthPassword = "auth-secret";
        vm.NetworkDeviceSnmpV3PrivacyPassword = "";

        await ((NetScopeDiagnosticCenter.UI.AsyncRelayCommand)vm.IdentifyNetworkDeviceCommand).ExecuteAsync(null);

        collector.IdentifyRequests.Should().BeEmpty();
        host.BusyTransitions.Should().BeEmpty();
        vm.LastNetworkDevice.Should().NotBeNull();
        vm.LastNetworkDevice!.Verdict.Should().Be("SNMPv3 credentials are incomplete.");
        vm.LastNetworkDevice.SnmpStatus.Should().Be("Credentials missing");
    }

    [Fact]
    public async Task ReadInterfacesAsync_EmptyTarget_NotifiesAndDoesNothing()
    {
        var (vm, host, _) = Build();
        vm.NetworkDeviceTarget = "";

        await ((NetScopeDiagnosticCenter.UI.AsyncRelayCommand)vm.ReadNetworkDeviceInterfacesCommand).ExecuteAsync(null);

        host.StatusMessages.Should().Contain(s => s.Contains("Enter a network device target"));
        host.BusyTransitions.Should().BeEmpty();
    }

    [Fact]
    public async Task ReadInterfacesAsync_ValidTarget_UsesCollectorAndRefreshesInterfaceBindings()
    {
        var collector = new FakeNetworkDeviceCollector();
        var (vm, host, _) = Build(collector: collector);
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        vm.NetworkDeviceTarget = "192.168.1.20";

        await ((NetScopeDiagnosticCenter.UI.AsyncRelayCommand)vm.ReadNetworkDeviceInterfacesCommand).ExecuteAsync(null);

        collector.InterfaceRequests.Should().ContainSingle();
        collector.InterfaceRequests[0].Target.Should().Be("192.168.1.20");
        vm.LastNetworkDevice.Should().NotBeNull();
        vm.LastNetworkDevice!.Interfaces.Should().ContainSingle();
        vm.FilteredNetworkDeviceInterfaces.Should().ContainSingle();
        changed.Should().Contain(nameof(NetworkDevicesViewModel.FilteredNetworkDeviceInterfaces));
        changed.Should().Contain(nameof(NetworkDevicesViewModel.NetworkDeviceInterfaceFilterSummary));
        host.AttachedDevices.Should().ContainSingle().Which.Should().BeSameAs(vm.LastNetworkDevice);
    }

    [Fact]
    public async Task ReadInterfacesAsync_CollectorThrows_PublishesFailureResultAndRedactsSecrets()
    {
        var collector = new FakeNetworkDeviceCollector(
            interfacesFactory: _ => throw new InvalidOperationException("SNMP failed for ro-community monitor auth-secret priv-secret"));
        var (vm, host, _) = Build(collector: collector);
        vm.LastNetworkDevice = new NetworkDeviceResult
        {
            Address = "192.168.1.9",
            Interfaces = [new NetworkDeviceInterfaceInfo { Index = 99, OperStatus = "Up" }]
        };
        vm.NetworkDeviceTarget = "192.168.1.20";
        vm.NetworkDeviceCommunity = "ro-community";
        vm.NetworkDeviceSelectedSnmpProtocol = SnmpProtocolVersion.V3AuthPriv;
        vm.NetworkDeviceSnmpV3UserName = "monitor";
        vm.NetworkDeviceSnmpV3AuthPassword = "auth-secret";
        vm.NetworkDeviceSnmpV3PrivacyPassword = "priv-secret";

        await ((NetScopeDiagnosticCenter.UI.AsyncRelayCommand)vm.ReadNetworkDeviceInterfacesCommand).ExecuteAsync(null);

        collector.InterfaceRequests.Should().ContainSingle();
        vm.LastNetworkDevice.Should().NotBeNull();
        vm.LastNetworkDevice!.Verdict.Should().Be("Interface read failed.");
        vm.LastNetworkDevice.Severity.Should().Be("Critical");
        vm.LastNetworkDevice.Interfaces.Should().BeEmpty();
        vm.LastNetworkDevice.Evidence.Should().ContainSingle();
        vm.LastNetworkDevice.Evidence[0].Should().Contain("[redacted]");
        vm.LastNetworkDevice.Evidence[0].Should().NotContain("ro-community");
        vm.LastNetworkDevice.Evidence[0].Should().NotContain("monitor");
        vm.LastNetworkDevice.Evidence[0].Should().NotContain("auth-secret");
        vm.LastNetworkDevice.Evidence[0].Should().NotContain("priv-secret");
        host.StatusMessages.Should().NotContain(message => message.Contains("ro-community", StringComparison.OrdinalIgnoreCase));
        host.StatusMessages.Should().NotContain(message => message.Contains("auth-secret", StringComparison.OrdinalIgnoreCase));
        host.AttachedDevices.Should().ContainSingle().Which.Should().BeSameAs(vm.LastNetworkDevice);
        host.BusyTransitions.Should().ContainInOrder(true, false);
    }

    [Fact]
    public async Task ReadSelectedInterfacesAsync_NoSelection_NotifiesAndDoesNothing()
    {
        var (vm, host, _) = Build();
        vm.SelectedNetworkDeviceScanResult = null;

        await ((NetScopeDiagnosticCenter.UI.AsyncRelayCommand)vm.ReadSelectedNetworkDeviceInterfacesCommand).ExecuteAsync(null);

        host.StatusMessages.Should().Contain(s => s.Contains("Select a device from the scan results"));
    }

    [Fact]
    public async Task ReadSelectedInterfacesAsync_UsesSelectedScanAddress()
    {
        var collector = new FakeNetworkDeviceCollector();
        var (vm, host, _) = Build(collector: collector);
        vm.SelectedNetworkDeviceScanResult = new NetworkDeviceResult { Address = "192.168.1.30" };

        await ((NetScopeDiagnosticCenter.UI.AsyncRelayCommand)vm.ReadSelectedNetworkDeviceInterfacesCommand).ExecuteAsync(null);

        collector.InterfaceRequests.Should().ContainSingle();
        collector.InterfaceRequests[0].Target.Should().Be("192.168.1.30");
        host.AttachedDevices.Should().ContainSingle().Which.Should().BeSameAs(vm.LastNetworkDevice);
    }

    [Fact]
    public async Task ReadSelectedInterfacesAsync_PublicIp_DoesNotCallCollector()
    {
        var collector = new FakeNetworkDeviceCollector();
        var (vm, host, _) = Build(collector: collector);
        vm.SelectedNetworkDeviceScanResult = new NetworkDeviceResult { Address = "8.8.8.8" };

        await ((NetScopeDiagnosticCenter.UI.AsyncRelayCommand)vm.ReadSelectedNetworkDeviceInterfacesCommand).ExecuteAsync(null);

        collector.InterfaceRequests.Should().BeEmpty();
        vm.LastNetworkDevice.Should().NotBeNull();
        vm.LastNetworkDevice!.Verdict.Should().Be("Target rejected by safety policy.");
        vm.LastNetworkDevice.Severity.Should().Be("Critical");
        host.AttachedDevices.Should().ContainSingle().Which.Should().BeSameAs(vm.LastNetworkDevice);
    }

    [Fact]
    public async Task ReadSelectedInterfacesAsync_SnmpV3MissingCredentials_DoesNotCallCollector()
    {
        var collector = new FakeNetworkDeviceCollector();
        var (vm, host, _) = Build(collector: collector);
        vm.SelectedNetworkDeviceScanResult = new NetworkDeviceResult { Address = "192.168.1.30" };
        vm.NetworkDeviceSelectedSnmpProtocol = SnmpProtocolVersion.V3AuthPriv;
        vm.NetworkDeviceSnmpV3UserName = "monitor";
        vm.NetworkDeviceSnmpV3AuthPassword = "auth-secret";
        vm.NetworkDeviceSnmpV3PrivacyPassword = "";

        await ((NetScopeDiagnosticCenter.UI.AsyncRelayCommand)vm.ReadSelectedNetworkDeviceInterfacesCommand).ExecuteAsync(null);

        collector.InterfaceRequests.Should().BeEmpty();
        vm.LastNetworkDevice.Should().NotBeNull();
        vm.LastNetworkDevice!.Verdict.Should().Be("SNMPv3 credentials are incomplete.");
        vm.LastNetworkDevice.SnmpStatus.Should().Be("Credentials missing");
        host.AttachedDevices.Should().ContainSingle().Which.Should().BeSameAs(vm.LastNetworkDevice);
    }

    [Fact]
    public async Task ScanRangeAsync_EmptyRange_NotifiesAndDoesNothing()
    {
        var (vm, host, _) = Build();
        vm.NetworkDeviceScanRange = "";

        await ((NetScopeDiagnosticCenter.UI.AsyncRelayCommand)vm.ScanNetworkDeviceRangeCommand).ExecuteAsync(null);

        host.StatusMessages.Should().Contain(s => s.Contains("Enter a scan range"));
        host.BusyTransitions.Should().BeEmpty();
    }

    [Fact]
    public async Task DetectScanRangeAsync_UpdatesDetectedRangeAndBusyState()
    {
        var collector = new FakeNetworkDeviceCollector(detectFactory: () => new NetworkScanRangeInfo
        {
            LocalIpAddress = "192.168.10.44",
            AdapterName = "Ethernet",
            ScanRange = "192.168.10.0/24"
        });
        var (vm, host, _) = Build(collector: collector);
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        await ((NetScopeDiagnosticCenter.UI.AsyncRelayCommand)vm.DetectNetworkDeviceScanRangeCommand).ExecuteAsync(null);

        collector.DetectRequests.Should().Be(1);
        vm.NetworkDeviceDetectedIp.Should().Be("192.168.10.44");
        vm.NetworkDeviceDetectedRange.Should().Be("192.168.10.0/24");
        vm.NetworkDeviceDetectedIpDisplay.Should().Be("192.168.10.44");
        vm.NetworkDeviceDetectedRangeDisplay.Should().Be("192.168.10.0/24");
        vm.NetworkDeviceScanRange.Should().Be("192.168.10.0/24");
        changed.Should().Contain(nameof(NetworkDevicesViewModel.NetworkDeviceDetectedIpDisplay));
        changed.Should().Contain(nameof(NetworkDevicesViewModel.NetworkDeviceDetectedRangeDisplay));
        host.BusyTransitions.Should().ContainInOrder(true, false);
        host.StatusMessages.Should().Contain(message => message.Contains("Detected local IP"));
    }

    [Fact]
    public async Task ScanLocalNetworkDevicesAsync_RunsCollectorAttachesAndSelectsFirstDevice()
    {
        var first = new NetworkDeviceResult { Address = "192.168.1.10", Verdict = "SNMP device identity confirmed.", Severity = "OK" };
        var collector = new FakeNetworkDeviceCollector(localScanFactory: options => new NetworkDeviceScanResult
        {
            Source = "192.168.1.0/24",
            Verdict = "Found 1 SNMP network devices.",
            Severity = "OK",
            SnmpProtocol = options.Protocol,
            Devices = [first]
        });
        var (vm, host, _) = Build(collector: collector);

        await ((NetScopeDiagnosticCenter.UI.AsyncRelayCommand)vm.ScanLocalNetworkDevicesCommand).ExecuteAsync(null);

        collector.LocalScanRequests.Should().ContainSingle();
        collector.LocalScanRequests[0].Protocol.Should().Be(SnmpProtocolVersion.V2C);
        vm.LastNetworkDeviceScan!.Devices.Should().ContainSingle().Which.Should().BeSameAs(first);
        vm.SelectedNetworkDeviceScanResult.Should().BeSameAs(first);
        vm.NetworkDeviceTarget.Should().Be("192.168.1.10");
        vm.IsNetworkDeviceScanRunning.Should().BeFalse();
        host.AttachedScans.Should().Contain(vm.LastNetworkDeviceScan);
        host.BusyTransitions.Should().ContainInOrder(true, false);
    }

    [Fact]
    public async Task ScanNetworkDeviceRangeAsync_RunsCollectorWithManualRange()
    {
        var first = new NetworkDeviceResult { Address = "10.0.0.20", Verdict = "SNMP device identity confirmed.", Severity = "OK" };
        var collector = new FakeNetworkDeviceCollector(rangeScanFactory: (range, options) => new NetworkDeviceScanResult
        {
            Source = range,
            Verdict = "Found 1 SNMP network devices.",
            Severity = "OK",
            SnmpProtocol = options.Protocol,
            Devices = [first]
        });
        var (vm, host, _) = Build(collector: collector);
        vm.NetworkDeviceScanRange = "10.0.0.0/24";

        await ((NetScopeDiagnosticCenter.UI.AsyncRelayCommand)vm.ScanNetworkDeviceRangeCommand).ExecuteAsync(null);

        collector.RangeScanRequests.Should().ContainSingle();
        collector.RangeScanRequests[0].Range.Should().Be("10.0.0.0/24");
        collector.RangeScanRequests[0].Options.Protocol.Should().Be(SnmpProtocolVersion.V2C);
        vm.SelectedNetworkDeviceScanResult.Should().BeSameAs(first);
        vm.NetworkDeviceTarget.Should().Be("10.0.0.20");
        host.AttachedScans.Should().Contain(vm.LastNetworkDeviceScan!);
        host.BusyTransitions.Should().ContainInOrder(true, false);
    }

    [Fact]
    public async Task ScanNetworkDeviceRangeAsync_NoDevicesClearsScanDerivedTarget()
    {
        var collector = new FakeNetworkDeviceCollector(rangeScanFactory: (range, options) => new NetworkDeviceScanResult
        {
            Source = range,
            Verdict = "No SNMP network devices responded.",
            Severity = "Warning",
            SnmpProtocol = options.Protocol
        });
        var (vm, host, _) = Build(collector: collector);
        vm.LastNetworkDeviceScan = new NetworkDeviceScanResult
        {
            Source = "10.0.0.0/24",
            Devices = [new NetworkDeviceResult { Address = "10.0.0.20" }]
        };
        vm.NetworkDeviceScanRange = "10.0.0.0/24";

        await ((NetScopeDiagnosticCenter.UI.AsyncRelayCommand)vm.ScanNetworkDeviceRangeCommand).ExecuteAsync(null);

        collector.RangeScanRequests.Should().ContainSingle();
        vm.LastNetworkDeviceScan!.Devices.Should().BeEmpty();
        vm.SelectedNetworkDeviceScanResult.Should().BeNull();
        vm.NetworkDeviceTarget.Should().BeEmpty();
        host.AttachedScans.Should().Contain(vm.LastNetworkDeviceScan);
    }

    [Fact]
    public async Task ScanLocalNetworkDevicesAsync_SnmpV3MissingCredentials_DoesNotScan()
    {
        var collector = new FakeNetworkDeviceCollector();
        var (vm, host, _) = Build(collector: collector);
        vm.NetworkDeviceSelectedSnmpProtocol = SnmpProtocolVersion.V3AuthPriv;
        vm.NetworkDeviceSnmpV3UserName = "monitor";
        vm.NetworkDeviceSnmpV3AuthPassword = "";
        vm.NetworkDeviceSnmpV3PrivacyPassword = "privacy-secret";

        await ((NetScopeDiagnosticCenter.UI.AsyncRelayCommand)vm.ScanLocalNetworkDevicesCommand).ExecuteAsync(null);

        collector.LocalScanRequests.Should().BeEmpty();
        host.BusyTransitions.Should().BeEmpty();
        vm.LastNetworkDeviceScan!.Verdict.Should().Be("SNMPv3 credentials are incomplete.");
        vm.LastNetworkDeviceScan.SkippedReason.Should().Contain("SNMPv3 authPriv requires");
        host.AttachedScans.Should().ContainSingle().Which.Should().BeSameAs(vm.LastNetworkDeviceScan);
    }

    [Fact]
    public async Task ScanNetworkDeviceRangeAsync_SnmpV3MissingCredentials_DoesNotScan()
    {
        var collector = new FakeNetworkDeviceCollector();
        var (vm, host, _) = Build(collector: collector);
        vm.NetworkDeviceScanRange = "10.0.0.0/24";
        vm.NetworkDeviceSelectedSnmpProtocol = SnmpProtocolVersion.V3AuthPriv;
        vm.NetworkDeviceSnmpV3UserName = "monitor";
        vm.NetworkDeviceSnmpV3AuthPassword = "auth-secret";
        vm.NetworkDeviceSnmpV3PrivacyPassword = "";

        await ((NetScopeDiagnosticCenter.UI.AsyncRelayCommand)vm.ScanNetworkDeviceRangeCommand).ExecuteAsync(null);

        collector.RangeScanRequests.Should().BeEmpty();
        host.BusyTransitions.Should().BeEmpty();
        vm.LastNetworkDeviceScan!.Verdict.Should().Be("SNMPv3 credentials are incomplete.");
        host.AttachedScans.Should().ContainSingle().Which.Should().BeSameAs(vm.LastNetworkDeviceScan);
    }

    [Fact]
    public void Constructor_NullArguments_Throw()
    {
        var collector = new NetworkDeviceCollector(new PowerShellRunner(new NullLogger()), new SnmpClientService());

        var store = new SnmpCredentialStore(new SecureCredentialService(
            new AppStorageService(Path.Combine(Path.GetTempPath(), "NetScopeTests-NetworkDevices-" + Guid.NewGuid().ToString("N")))));

        FluentActions.Invoking(() => new NetworkDevicesViewModel(null!, new NullLogger(), new FakeHost(), store))
            .Should().Throw<ArgumentNullException>();

        FluentActions.Invoking(() => new NetworkDevicesViewModel(collector, null!, new FakeHost(), store))
            .Should().Throw<ArgumentNullException>();

        FluentActions.Invoking(() => new NetworkDevicesViewModel(collector, new NullLogger(), null!, store))
            .Should().Throw<ArgumentNullException>();

        FluentActions.Invoking(() => new NetworkDevicesViewModel(collector, new NullLogger(), new FakeHost(), null!))
            .Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task ScanLocal_AppliesSavedLabels_AndHeadlineFollowsScan()
    {
        var labelStore = BuildLabelStore();
        labelStore.SaveName(WifiDeviceFriendlyNameStore.BuildKey("AA:BB:CC:DD:EE:01", "192.168.1.10"), "Imprimanta etaj 2");
        var collector = new FakeNetworkDeviceCollector(localScanFactory: _ => new NetworkDeviceScanResult
        {
            Source = "192.168.1.0/24",
            Verdict = "Found 1 device(s) in 192.168.1.0/24; 0 answered SNMP.",
            Severity = "OK",
            Devices =
            [
                new NetworkDeviceResult { Address = "192.168.1.10", MacAddress = "AA:BB:CC:DD:EE:01", Verdict = "Online", Severity = "OK" }
            ]
        });
        var (vm, _, _) = Build(collector: collector, labelStore: labelStore);

        await ((NetScopeDiagnosticCenter.UI.AsyncRelayCommand)vm.ScanLocalNetworkDevicesCommand).ExecuteAsync(null);

        var device = vm.LastNetworkDeviceScan!.Devices.Single();
        device.FriendlyLabel.Should().Be("Imprimanta etaj 2");
        device.DisplayName.Should().Be("Imprimanta etaj 2", "the technician label outranks every detected name");
        vm.HeadlineVerdict.Should().Contain("Found 1 device(s)");
        vm.HeadlineSeverity.Should().Be("OK");
    }

    [Fact]
    public async Task SaveAndClearDeviceLabel_PersistAndUpdateTheRow()
    {
        var labelStore = BuildLabelStore();
        var (vm, host, _) = Build(collector: new FakeNetworkDeviceCollector(), labelStore: labelStore);

        await ((NetScopeDiagnosticCenter.UI.AsyncRelayCommand)vm.ScanLocalNetworkDevicesCommand).ExecuteAsync(null);
        vm.SelectedNetworkDeviceScanResult.Should().NotBeNull();

        vm.SelectedDeviceLabelText = "Switch rack parter";
        vm.SaveDeviceLabelCommand.Execute(null);

        vm.SelectedNetworkDeviceScanResult!.FriendlyLabel.Should().Be("Switch rack parter");
        labelStore.Load().Values.Should().Contain("Switch rack parter");
        host.StatusMessages.Should().Contain(message => message.Contains("Label saved"));

        vm.ClearDeviceLabelCommand.Execute(null);

        vm.SelectedNetworkDeviceScanResult.FriendlyLabel.Should().BeEmpty();
        labelStore.Load().Should().BeEmpty();
    }

    [Fact]
    public async Task Headline_FollowsLatestAction()
    {
        var (vm, _, _) = Build(collector: new FakeNetworkDeviceCollector());
        vm.NetworkDeviceTarget = "192.168.1.5";

        await ((NetScopeDiagnosticCenter.UI.AsyncRelayCommand)vm.IdentifyNetworkDeviceCommand).ExecuteAsync(null);
        vm.HeadlineVerdict.Should().Be("SNMP device identity confirmed.");

        await ((NetScopeDiagnosticCenter.UI.AsyncRelayCommand)vm.ScanLocalNetworkDevicesCommand).ExecuteAsync(null);
        vm.HeadlineVerdict.Should().Contain("Found 1");

        vm.NetworkDeviceTarget = "192.168.1.6";
        await ((NetScopeDiagnosticCenter.UI.AsyncRelayCommand)vm.IdentifyNetworkDeviceCommand).ExecuteAsync(null);
        vm.HeadlineVerdict.Should().Be("SNMP device identity confirmed.", "the single-device check is now the latest action");
    }
}
