using System.IO;
using NetScopeDiagnosticCenter.Collectors;
using NetScopeDiagnosticCenter.Core;
using NetScopeDiagnosticCenter.Core.Models;
using NetScopeDiagnosticCenter.Infrastructure;
using NetScopeDiagnosticCenter.UI.ViewModels;

namespace NetScopeDiagnosticCenter.Tests.UI.ViewModels;

/// <summary>
/// Tests for PrintersViewModel state management and SNMP credential lifecycle.
/// Async commands that touch the network/SNMP/PowerShell stack are not unit-tested
/// here; they require integration with real services and are covered by manual runs.
/// </summary>
public class PrintersViewModelTests : IDisposable
{
    private sealed class FakeHost : PrintersViewModel.IHost
    {
        public List<string> StatusMessages { get; } = [];
        public List<bool> BusyTransitions { get; } = [];
        public List<PrinterDiscoveryResult> Attachments { get; } = [];
        public List<string> RememberedTargets { get; } = [];
        public List<string> RememberedServers { get; } = [];

        public void NotifyStatus(string message) => StatusMessages.Add(message);
        public void NotifyBusy(bool busy) => BusyTransitions.Add(busy);
        public void AttachPrinterDiscovery(PrinterDiscoveryResult discovery) => Attachments.Add(discovery);
        public void RememberPrinterTarget(string target) => RememberedTargets.Add(target);
        public void RememberPrintServer(string server) => RememberedServers.Add(server);
    }

    private sealed class NullLogger : ILoggingService
    {
        public void Debug(string message) { }
        public void Info(string message) { }
        public void Warning(string message) { }
        public void Error(string message, Exception? exception = null) { }
    }

    private sealed class FakeSnmpPrinterCollector : SnmpPrinterCollector
    {
        private readonly Func<SnmpSessionOptions, SnmpDeviceInfo> _factory;

        public FakeSnmpPrinterCollector(Func<SnmpSessionOptions, SnmpDeviceInfo> factory)
            : base(new SnmpClientService())
        {
            _factory = factory;
        }

        public List<string> Targets { get; } = [];

        public override Task<SnmpDeviceInfo> IdentifyAsync(
            SnmpSessionOptions options,
            CancellationToken cancellationToken = default)
        {
            Targets.Add(options.Target);
            return Task.FromResult(_factory(options));
        }
    }

    private sealed class FakePrinterDiscoveryCollector : PrinterDiscoveryCollector
    {
        private readonly IReadOnlyList<PrinterNetworkAdapterInfo> _adapters;
        private readonly Func<PrinterNetworkAdapterInfo?, PrinterDiscoveryResult>? _localScanFactory;
        private readonly Func<PrinterScanRequest, PrinterDiscoveryResult>? _rangeScanFactory;

        public FakePrinterDiscoveryCollector(
            IReadOnlyList<PrinterNetworkAdapterInfo> adapters,
            Func<PrinterNetworkAdapterInfo?, PrinterDiscoveryResult>? localScanFactory = null,
            Func<PrinterScanRequest, PrinterDiscoveryResult>? rangeScanFactory = null)
            : base(new PowerShellRunner(new NullLogger()))
        {
            _adapters = adapters;
            _localScanFactory = localScanFactory;
            _rangeScanFactory = rangeScanFactory;
        }

        public int DetectLocalAdaptersCount { get; private set; }
        public int ScanLocalSubnetCount { get; private set; }
        public List<PrinterScanRequest> RangeRequests { get; } = [];

        public override Task<IReadOnlyList<PrinterNetworkAdapterInfo>> DetectLocalAdaptersAsync(
            CancellationToken cancellationToken = default)
        {
            DetectLocalAdaptersCount++;
            return Task.FromResult(_adapters);
        }

        public override Task<PrinterDiscoveryResult> ScanLocalSubnetAsync(
            PrinterNetworkAdapterInfo? preferredAdapter,
            CancellationToken cancellationToken = default)
        {
            ScanLocalSubnetCount++;
            var result = _localScanFactory?.Invoke(preferredAdapter) ?? new PrinterDiscoveryResult
            {
                Mode = "LocalSubnetScan",
                Verdict = "No fake local scan configured.",
                Severity = "Warning"
            };
            return Task.FromResult(result);
        }

        public override Task<PrinterDiscoveryResult> ScanRangeAsync(
            PrinterScanRequest request,
            CancellationToken cancellationToken = default)
        {
            RangeRequests.Add(request);
            var result = _rangeScanFactory?.Invoke(request) ?? new PrinterDiscoveryResult
            {
                Mode = "RangeScan",
                Source = request.Input,
                Verdict = "No fake range scan configured.",
                Severity = "Warning"
            };
            return Task.FromResult(result);
        }
    }

    private sealed class FakeLocalPrinterCollector : LocalPrinterCollector
    {
        private PrinterDiscoveryResult _result;

        public FakeLocalPrinterCollector(PrinterDiscoveryResult result)
            : base(new PowerShellRunner(new NullLogger()))
        {
            _result = result;
        }

        public int CollectCount { get; private set; }
        public List<string> DefaultPrinterRequests { get; } = [];
        public PrinterActionResult DefaultActionResult { get; set; } = new()
        {
            Success = true,
            Message = "Default printer updated."
        };

        public void SetCollectResult(PrinterDiscoveryResult result) => _result = result;

        public override Task<PrinterDiscoveryResult> CollectAsync(CancellationToken cancellationToken = default)
        {
            CollectCount++;
            return Task.FromResult(_result);
        }

        public override Task<PrinterActionResult> SetDefaultPrinterAsync(
            string printerName,
            CancellationToken cancellationToken = default)
        {
            DefaultPrinterRequests.Add(printerName);
            return Task.FromResult(DefaultActionResult);
        }
    }

    private sealed class FakePrintServerCollector : PrintServerCollector
    {
        private readonly Func<string, PrinterDiscoveryResult> _factory;

        public FakePrintServerCollector(Func<string, PrinterDiscoveryResult> factory)
            : base(new PowerShellRunner(new NullLogger()))
        {
            _factory = factory;
        }

        public List<string> DiscoveredServers { get; } = [];

        public override Task<PrinterDiscoveryResult> DiscoverFromPrintServerAsync(
            string printServer,
            CancellationToken cancellationToken = default)
        {
            DiscoveredServers.Add(printServer);
            return Task.FromResult(_factory(printServer));
        }
    }

    private sealed class FakePrinterQueueInstaller : PrinterQueueInstaller
    {
        private readonly Func<string, PrinterInstallResult> _factory;

        public FakePrinterQueueInstaller(Func<string, PrinterInstallResult> factory)
            : base(new PowerShellRunner(new NullLogger()))
        {
            _factory = factory;
        }

        public List<string> Connections { get; } = [];

        public override Task<PrinterInstallResult> InstallSharedQueueAsync(
            string connectionName,
            CancellationToken cancellationToken = default)
        {
            Connections.Add(connectionName);
            return Task.FromResult(_factory(connectionName));
        }
    }

    private readonly string _isolatedAppData;

    public PrintersViewModelTests()
    {
        _isolatedAppData = Path.Combine(Path.GetTempPath(), "NetScopeTests-Printers-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_isolatedAppData);
    }

    public void Dispose()
    {
        try { Directory.Delete(_isolatedAppData, recursive: true); } catch { /* best-effort */ }
    }

    private (PrintersViewModel vm, FakeHost host, SecureCredentialService cred) Build(
        string initialPrinterTarget = "",
        string initialPrintServer = "",
        PrinterDiscoveryCollector? printerDiscoveryCollector = null,
        SnmpPrinterCollector? snmpPrinterCollector = null,
        LocalPrinterCollector? localPrinterCollector = null,
        PrintServerCollector? printServerCollector = null,
        PrinterQueueInstaller? printerQueueInstaller = null,
        Func<IReadOnlyList<AdPrintServerInfo>>? adPrintServerLocator = null)
    {
        var storage = new AppStorageService(_isolatedAppData);
        var psRunner = new PowerShellRunner(new NullLogger());
        var snmp = new SnmpClientService();
        var cred = new SecureCredentialService(storage);
        var store = new SnmpCredentialStore(cred);
        var host = new FakeHost();
        var vm = new PrintersViewModel(
            printerDiscoveryCollector ?? new PrinterDiscoveryCollector(psRunner),
            localPrinterCollector ?? new LocalPrinterCollector(psRunner),
            printServerCollector ?? new PrintServerCollector(psRunner),
            snmpPrinterCollector ?? new SnmpPrinterCollector(snmp),
            printerQueueInstaller ?? new PrinterQueueInstaller(psRunner),
            store,
            new NullLogger(),
            host,
            initialPrinterTarget,
            initialPrintServer,
            adPrintServerLocator ?? (() => []));
        return (vm, host, cred);
    }

    [Fact]
    public void Construction_AppliesInitialTargets()
    {
        var (vm, _, _) = Build("192.168.1.50", "PRT-SRV-01");
        vm.PrinterTarget.Should().Be("192.168.1.50");
        vm.SelectedPrinterTarget.Should().Be("192.168.1.50");
        vm.PrintServerName.Should().Be("PRT-SRV-01");
    }

    [Fact]
    public void Construction_DefaultsAreSensible()
    {
        var (vm, _, _) = Build();
        vm.SelectedSnmpProtocol.Should().Be(SnmpProtocolVersion.V2C);
        vm.SnmpCommunity.Should().Be("public");
        vm.SnmpV3AuthProtocol.Should().Be("SHA256");
        vm.SnmpV3PrivacyProtocol.Should().Be("AES128");
        vm.IsSnmpV2Selected.Should().BeTrue();
        vm.IsSnmpV3Selected.Should().BeFalse();
    }

    [Fact]
    public void SelectedSnmpProtocol_SwitchingFiresIsV3IsV2()
    {
        var (vm, _, _) = Build();
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        vm.SelectedSnmpProtocol = SnmpProtocolVersion.V3AuthPriv;

        vm.IsSnmpV3Selected.Should().BeTrue();
        vm.IsSnmpV2Selected.Should().BeFalse();
        changed.Should().Contain(nameof(PrintersViewModel.IsSnmpV3Selected));
        changed.Should().Contain(nameof(PrintersViewModel.IsSnmpV2Selected));
    }

    [Fact]
    public void PrinterTarget_NonEmptyAlsoUpdatesSelectedTargetAndSource()
    {
        var (vm, _, _) = Build();
        vm.PrinterTarget = "  10.0.0.5  ";

        vm.SelectedPrinterTarget.Should().Be("10.0.0.5");
        vm.SelectedPrinterTargetSource.Should().Be("Manual");
    }

    [Fact]
    public void PrinterTarget_Empty_DoesNotOverrideSelection()
    {
        var (vm, _, _) = Build();
        vm.SelectedPrinterTarget = "preset";
        vm.SelectedPrinterTargetSource = "preset-source";

        vm.PrinterTarget = "";

        vm.SelectedPrinterTarget.Should().Be("preset");
        vm.SelectedPrinterTargetSource.Should().Be("preset-source");
    }

    [Fact]
    public void SelectedPrintServerPrinter_WithIp_PopulatesTargetAndSource()
    {
        var (vm, _, _) = Build();
        var printer = new PrinterInfo { Name = "HP-Office", PortHostAddress = "192.168.1.100" };

        vm.SelectedPrintServerPrinter = printer;

        vm.SelectedPrinterTarget.Should().Be("192.168.1.100");
        vm.SelectedPrinterTargetSource.Should().Contain("HP-Office");
    }

    [Fact]
    public void SelectedPrinterScanResult_WithAddress_PopulatesTarget()
    {
        var (vm, _, _) = Build();
        var row = new PrinterScanResult { Address = "10.0.0.42" };

        vm.SelectedPrinterScanResult = row;

        vm.SelectedPrinterTarget.Should().Be("10.0.0.42");
        vm.SelectedPrinterTargetSource.Should().Be("Printer IP scan");
    }

    [Fact]
    public void LastPrinterDiscovery_WithRows_AutoSelectsFirstScanResult()
    {
        var (vm, _, _) = Build();
        var discovery = new PrinterDiscoveryResult
        {
            ScanResults = [new PrinterScanResult { Address = "10.0.0.10" }],
            Printers = [new PrinterInfo { Name = "p1" }]
        };

        vm.LastPrinterDiscovery = discovery;

        vm.SelectedPrinterScanResult.Should().NotBeNull();
        vm.SelectedPrinterScanResult!.Address.Should().Be("10.0.0.10");
        vm.SelectedPrintServerPrinter.Should().NotBeNull();
    }

    [Fact]
    public void BuildSnmpOptions_PassesAllFields()
    {
        var (vm, _, _) = Build();
        vm.SnmpCommunity = "shared-community";
        vm.SelectedSnmpProtocol = SnmpProtocolVersion.V3AuthPriv;
        vm.SnmpV3UserName = "monitor";
        vm.SnmpV3AuthPassword = "auth-pwd";
        vm.SnmpV3PrivacyPassword = "priv-pwd";

        var opts = vm.BuildSnmpOptions("192.168.1.1");

        opts.Target.Should().Be("192.168.1.1");
        opts.Protocol.Should().Be(SnmpProtocolVersion.V3AuthPriv);
        opts.Community.Should().Be("shared-community");
        opts.UserName.Should().Be("monitor");
        opts.AuthPassword.Should().Be("auth-pwd");
        opts.PrivacyPassword.Should().Be("priv-pwd");
    }

    [Fact]
    public void Construction_LoadsSavedSnmpV3Credential()
    {
        // Pre-seed: write SNMPv3 to disk via SecureCredentialService
        var storage = new AppStorageService(_isolatedAppData);
        var cred = new SecureCredentialService(storage);
        cred.SaveSnmpV3(new SnmpSessionOptions
        {
            Protocol = SnmpProtocolVersion.V3AuthPriv,
            UserName = "saved-user",
            AuthProtocol = "SHA512",
            AuthPassword = "saved-auth",
            PrivacyProtocol = "AES256",
            PrivacyPassword = "saved-priv"
        });

        // Now build a VM in the same isolated folder — its ctor should hydrate SNMPv3
        var (vm, _, _) = Build();

        vm.HasSavedSnmpCredentials.Should().BeTrue();
        vm.SnmpV3UserName.Should().Be("saved-user");
        vm.SnmpV3AuthProtocol.Should().Be("SHA512");
        vm.SnmpV3AuthPassword.Should().Be("saved-auth");
        vm.SnmpV3PrivacyProtocol.Should().Be("AES256");
        vm.SnmpV3PrivacyPassword.Should().Be("saved-priv");
    }

    [Fact]
    public void ForgetSnmpCredentialCommand_ClearsSavedCredentialsAndPasswords()
    {
        var (vm, host, cred) = Build();
        // Pre-seed via the service so the VM picks up the saved flag, then re-seed VM password fields
        cred.SaveSnmpV3(new SnmpSessionOptions
        {
            Protocol = SnmpProtocolVersion.V3AuthPriv,
            UserName = "u",
            AuthPassword = "a",
            PrivacyPassword = "p"
        });
        vm.SnmpV3AuthPassword = "a";
        vm.SnmpV3PrivacyPassword = "p";
        vm.SaveSnmpCredentials = true;

        vm.ForgetSnmpCredentialCommand.Execute(null);

        vm.SnmpV3AuthPassword.Should().BeEmpty();
        vm.SnmpV3PrivacyPassword.Should().BeEmpty();
        vm.HasSavedSnmpCredentials.Should().BeFalse();
        vm.SaveSnmpCredentials.Should().BeFalse();
        cred.HasSavedSnmpV3Credential.Should().BeFalse();
        host.StatusMessages.Should().Contain(s => s.Contains("removed"));
    }

    [Fact]
    public void CancelPrinterScanCommand_NotifiesStatus()
    {
        var (vm, host, _) = Build();
        vm.CancelPrinterScanCommand.Execute(null);
        host.StatusMessages.Should().Contain(s => s.Contains("Stopping printer scan"));
    }

    [Theory]
    [InlineData("", "(empty)")]
    [InlineData("  ", "(empty)")]
    [InlineData("public", "********")]
    [InlineData("complex-secret", "********")]
    public void MaskCommunity_HidesNonEmptyValue(string input, string expected)
    {
        PrintersViewModel.MaskCommunity(input).Should().Be(expected);
    }

    [Fact]
    public void Commands_AreNotNull()
    {
        var (vm, _, _) = Build();
        vm.DetectPrinterIpCommand.Should().NotBeNull();
        vm.LoadLocalPrintersCommand.Should().NotBeNull();
        vm.DetectPrintServerCommand.Should().NotBeNull();
        vm.DiscoverPrintServerCommand.Should().NotBeNull();
        vm.ScanPrintersCommand.Should().NotBeNull();
        vm.ScanPrinterRangeCommand.Should().NotBeNull();
        vm.IdentifySelectedPrinterSnmpCommand.Should().NotBeNull();
        vm.InstallSelectedPrinterQueueCommand.Should().NotBeNull();
        vm.ForgetSnmpCredentialCommand.Should().NotBeNull();
        vm.CancelPrinterScanCommand.Should().NotBeNull();
        vm.AutoIdentifyScannedPrintersSnmpCommand.Should().NotBeNull();
        vm.CancelPrinterSnmpIdentifyCommand.Should().NotBeNull();
        vm.RemoveSelectedPrinterScanResultCommand.Should().NotBeNull();
        vm.SetDefaultPrinterCommand.Should().NotBeNull();
    }

    [Fact]
    public async Task DetectPrinterIpCommand_UpdatesOverviewWithDetectedAdapter()
    {
        var adapter = new PrinterNetworkAdapterInfo
        {
            LocalIpAddress = "192.168.10.44",
            AdapterName = "Ethernet",
            PrefixLength = 24,
            Gateway = "192.168.10.1",
            RouteMetric = 10,
            InterfaceMetric = 20,
            DetectedSubnet = "192.168.10.0/24",
            DetectedSubnetHosts = 254,
            SafeScanRange = "192.168.10.0/24"
        };
        var collector = new FakePrinterDiscoveryCollector([adapter]);
        var (vm, host, _) = Build(printerDiscoveryCollector: collector);

        await ((NetScopeDiagnosticCenter.UI.AsyncRelayCommand)vm.DetectPrinterIpCommand).ExecuteAsync(null);

        collector.DetectLocalAdaptersCount.Should().Be(1);
        vm.SelectedPrinterNetworkAdapter.Should().BeSameAs(adapter);
        vm.LastPrinterDiscovery.Should().NotBeNull();
        vm.LastPrinterDiscovery!.Mode.Should().Be("AdapterOverview");
        vm.LastPrinterDiscovery.Source.Should().Be(Environment.MachineName);
        vm.LastPrinterDiscovery.Verdict.Should().Contain("Printer overview refreshed");
        vm.LastPrinterDiscovery.Severity.Should().Be("OK");
        vm.LastPrinterDiscovery.LocalIpAddress.Should().Be("192.168.10.44");
        vm.LastPrinterDiscovery.Gateway.Should().Be("192.168.10.1");
        vm.LastPrinterDiscovery.SafeScanRange.Should().Be("192.168.10.0/24");
        host.Attachments.Should().Contain(vm.LastPrinterDiscovery);
        host.BusyTransitions.Should().ContainInOrder(true, false);
        host.StatusMessages.Should().Contain(message => message.Contains("Detected 192.168.10.44"));
    }

    [Fact]
    public async Task DetectPrinterIpCommand_NoAdapter_StoresRealWarningState()
    {
        var collector = new FakePrinterDiscoveryCollector([]);
        var (vm, host, _) = Build(printerDiscoveryCollector: collector);

        await ((NetScopeDiagnosticCenter.UI.AsyncRelayCommand)vm.DetectPrinterIpCommand).ExecuteAsync(null);

        vm.SelectedPrinterNetworkAdapter.Should().BeNull();
        vm.LastPrinterDiscovery.Should().NotBeNull();
        vm.LastPrinterDiscovery!.Mode.Should().Be("AdapterOverview");
        vm.LastPrinterDiscovery.Severity.Should().Be("Warning");
        vm.LastPrinterDiscovery.Verdict.Should().Be("No usable IPv4 default-route adapter was detected.");
        vm.LastPrinterDiscovery.SafeScanRange.Should().BeEmpty();
        vm.LastPrinterDiscovery.Limitations.Should().Contain("Printer scan needs a private IPv4 adapter with a usable local route.");
        host.Attachments.Should().Contain(vm.LastPrinterDiscovery);
        host.StatusMessages.Should().Contain(message => message.Contains("No usable IPv4"));
    }

    [Fact]
    public void SelectedPrinterNetworkAdapter_ManualSelectionRefreshesOverviewSnapshot()
    {
        var first = new PrinterNetworkAdapterInfo
        {
            LocalIpAddress = "192.168.10.44",
            AdapterName = "Ethernet",
            Gateway = "192.168.10.1",
            DetectedSubnet = "192.168.10.0/24",
            DetectedSubnetHosts = 254,
            SafeScanRange = "192.168.10.0/24"
        };
        var second = new PrinterNetworkAdapterInfo
        {
            LocalIpAddress = "10.20.30.40",
            AdapterName = "Wi-Fi",
            Gateway = "10.20.30.1",
            DetectedSubnet = "10.20.30.0/24",
            DetectedSubnetHosts = 254,
            SafeScanRange = "10.20.30.0/24"
        };
        var (vm, host, _) = Build();

        vm.SelectedPrinterNetworkAdapter = first;
        vm.SelectedPrinterNetworkAdapter = second;

        vm.LastPrinterDiscovery.Should().NotBeNull();
        vm.LastPrinterDiscovery!.LocalIpAddress.Should().Be("10.20.30.40");
        vm.LastPrinterDiscovery.LocalAdapterName.Should().Be("Wi-Fi");
        vm.LastPrinterDiscovery.Gateway.Should().Be("10.20.30.1");
        vm.LastPrinterDiscovery.SafeScanRange.Should().Be("10.20.30.0/24");
        vm.LastPrinterDiscovery.Verdict.Should().Contain("10.20.30.40");
        host.Attachments.Last().Should().BeSameAs(vm.LastPrinterDiscovery);
    }

    [Fact]
    public async Task ScanPrintersCommand_DetectsAdapterRunsSafeScanAndSelectsFirstResult()
    {
        var adapter = new PrinterNetworkAdapterInfo
        {
            LocalIpAddress = "192.168.10.44",
            AdapterName = "Ethernet",
            PrefixLength = 24,
            Gateway = "192.168.10.1",
            SafeScanRange = "192.168.10.0/24"
        };
        var scanRow = new PrinterScanResult
        {
            Address = "192.168.10.50",
            Details = "Open ports: 9100",
            Classification = "Likely printer"
        };
        var collector = new FakePrinterDiscoveryCollector(
            [adapter],
            localScanFactory: preferredAdapter => new PrinterDiscoveryResult
            {
                Mode = "RangeScan",
                Source = preferredAdapter?.SafeScanRange ?? string.Empty,
                Verdict = "Found 1 devices with printer-related ports.",
                Severity = "OK",
                ScanResults = [scanRow]
            });
        var (vm, host, _) = Build(printerDiscoveryCollector: collector);

        await ((NetScopeDiagnosticCenter.UI.AsyncRelayCommand)vm.ScanPrintersCommand).ExecuteAsync(null);

        collector.DetectLocalAdaptersCount.Should().Be(1);
        collector.ScanLocalSubnetCount.Should().Be(1);
        vm.LastPrinterDiscovery!.ScanResults.Should().ContainSingle().Which.Should().BeSameAs(scanRow);
        vm.SelectedPrinterScanResult.Should().BeSameAs(scanRow);
        vm.SelectedPrinterTarget.Should().Be("192.168.10.50");
        vm.SelectedPrinterTargetSource.Should().Be("Printer IP scan");
        vm.IsPrinterScanRunning.Should().BeFalse();
        host.BusyTransitions.Should().ContainInOrder(true, false);
        host.Attachments.Should().Contain(vm.LastPrinterDiscovery);
    }

    [Fact]
    public async Task ScanPrinterRangeCommand_ManualRangeRunsCollectorAndSelectsFirstResult()
    {
        var first = new PrinterScanResult { Address = "10.0.0.42", Details = "Open ports: 9100" };
        var second = new PrinterScanResult { Address = "10.0.0.43", Details = "Open ports: 80" };
        var collector = new FakePrinterDiscoveryCollector(
            [],
            rangeScanFactory: request => new PrinterDiscoveryResult
            {
                Mode = "RangeScan",
                Source = request.Input,
                Verdict = "Found 2 devices with printer-related ports.",
                Severity = "OK",
                ScanResults = [first, second]
            });
        var (vm, host, _) = Build(printerDiscoveryCollector: collector);
        vm.PrinterScanRange = "10.0.0.0/24";

        await ((NetScopeDiagnosticCenter.UI.AsyncRelayCommand)vm.ScanPrinterRangeCommand).ExecuteAsync(null);

        collector.RangeRequests.Should().ContainSingle();
        collector.RangeRequests[0].Input.Should().Be("10.0.0.0/24");
        collector.RangeRequests[0].MaxHosts.Should().Be(254);
        vm.LastPrinterDiscovery!.ScanResults.Should().Equal(first, second);
        vm.SelectedPrinterScanResult.Should().BeSameAs(first);
        vm.SelectedPrinterTarget.Should().Be("10.0.0.42");
        vm.SelectedPrinterTargetSource.Should().Be("Printer IP scan");
        host.BusyTransitions.Should().ContainInOrder(true, false);
        host.Attachments.Should().Contain(vm.LastPrinterDiscovery);
    }

    [Fact]
    public async Task ScanPrinterRangeCommand_NoResultsClearsStaleScanTarget()
    {
        var oldRow = new PrinterScanResult { Address = "10.0.0.42", Details = "Open ports: 9100" };
        var collector = new FakePrinterDiscoveryCollector(
            [],
            rangeScanFactory: request => new PrinterDiscoveryResult
            {
                Mode = "RangeScan",
                Source = request.Input,
                Verdict = "No printer-related ports found in 10.0.0.0/24.",
                Severity = "Warning",
                ScanResults = []
            });
        var (vm, _, _) = Build(printerDiscoveryCollector: collector);
        vm.LastPrinterDiscovery = new PrinterDiscoveryResult { ScanResults = [oldRow] };
        vm.SelectedPrinterTarget.Should().Be("10.0.0.42");
        vm.PrinterScanRange = "10.0.0.0/24";

        await ((NetScopeDiagnosticCenter.UI.AsyncRelayCommand)vm.ScanPrinterRangeCommand).ExecuteAsync(null);

        vm.LastPrinterDiscovery!.ScanResults.Should().BeEmpty();
        vm.SelectedPrinterScanResult.Should().BeNull();
        vm.SelectedPrinterTarget.Should().BeEmpty();
        vm.SelectedPrinterTargetSource.Should().Be("Manual");
    }

    [Fact]
    public async Task SetDefaultPrinterCommand_WithSelectedPrinter_SetsRefreshesAndKeepsSelection()
    {
        var refreshed = new PrinterDiscoveryResult
        {
            Verdict = "Local printers were enumerated.",
            Severity = "OK",
            LocalPrinters =
            [
                new PrinterInfo { Name = "HP-01", IsDefault = false },
                new PrinterInfo { Name = "HP-02", IsDefault = true }
            ]
        };
        var collector = new FakeLocalPrinterCollector(refreshed)
        {
            DefaultActionResult = new PrinterActionResult
            {
                Success = true,
                Message = "'HP-02' is now the default printer."
            }
        };
        var (vm, host, _) = Build(localPrinterCollector: collector);
        var oldHp01 = new PrinterInfo { Name = "HP-01" };
        var oldHp02 = new PrinterInfo { Name = "HP-02" };
        vm.LastPrinterDiscovery = new PrinterDiscoveryResult
        {
            LocalPrinters = [oldHp01, oldHp02]
        };
        vm.SelectedLocalPrinter = oldHp02;

        await ((NetScopeDiagnosticCenter.UI.AsyncRelayCommand)vm.SetDefaultPrinterCommand).ExecuteAsync(null);

        collector.DefaultPrinterRequests.Should().ContainSingle("HP-02");
        collector.CollectCount.Should().Be(1);
        vm.LastPrinterDiscovery!.LocalPrinters.Single(printer => printer.Name == "HP-02").IsDefault.Should().BeTrue();
        vm.SelectedLocalPrinter.Should().NotBeSameAs(oldHp02);
        vm.SelectedLocalPrinter!.Name.Should().Be("HP-02");
        vm.SelectedLocalPrinter.IsDefault.Should().BeTrue();
        host.BusyTransitions.Should().ContainInOrder(true, false);
        host.Attachments.Should().Contain(vm.LastPrinterDiscovery);
        host.StatusMessages.Should().Contain(message => message.Contains("now the default printer"));
    }

    [Fact]
    public async Task SetDefaultPrinterCommand_WithoutSelection_NotifiesAndDoesNotCallCollector()
    {
        var collector = new FakeLocalPrinterCollector(new PrinterDiscoveryResult());
        var (vm, host, _) = Build(localPrinterCollector: collector);

        await ((NetScopeDiagnosticCenter.UI.AsyncRelayCommand)vm.SetDefaultPrinterCommand).ExecuteAsync(null);

        collector.DefaultPrinterRequests.Should().BeEmpty();
        collector.CollectCount.Should().Be(0);
        host.BusyTransitions.Should().BeEmpty();
        host.StatusMessages.Should().ContainSingle(message => message.Contains("Select an installed printer"));
    }

    [Fact]
    public async Task SetDefaultPrinterCommand_WhenWindowsRejects_DoesNotRefreshInventory()
    {
        var collector = new FakeLocalPrinterCollector(new PrinterDiscoveryResult())
        {
            DefaultActionResult = new PrinterActionResult
            {
                Success = false,
                Message = "Windows did not confirm the printer as default."
            }
        };
        var (vm, host, _) = Build(localPrinterCollector: collector);
        vm.SelectedLocalPrinter = new PrinterInfo { Name = "HP-02" };

        await ((NetScopeDiagnosticCenter.UI.AsyncRelayCommand)vm.SetDefaultPrinterCommand).ExecuteAsync(null);

        collector.DefaultPrinterRequests.Should().ContainSingle("HP-02");
        collector.CollectCount.Should().Be(0);
        host.BusyTransitions.Should().ContainInOrder(true, false);
        host.StatusMessages.Should().Contain(message => message.Contains("did not confirm"));
    }

    [Fact]
    public async Task IdentifySnmpAsync_ValidManualTarget_RemembersCreatesRowAndUpdatesDiscovery()
    {
        var collector = new FakeSnmpPrinterCollector(options => new SnmpDeviceInfo
        {
            Address = options.Target,
            Protocol = options.Protocol,
            Success = true,
            PrinterConfirmed = true,
            PrinterName = "HP Office",
            PrinterSerialNumber = "SN123",
            SysDescr = "HP LaserJet"
        });
        var (vm, host, _) = Build(snmpPrinterCollector: collector);
        vm.PrinterTarget = " 192.168.1.50 ";

        await ((NetScopeDiagnosticCenter.UI.AsyncRelayCommand)vm.IdentifySelectedPrinterSnmpCommand).ExecuteAsync(null);

        collector.Targets.Should().ContainSingle("192.168.1.50");
        host.RememberedTargets.Should().ContainSingle("192.168.1.50");
        vm.LastPrinterDiscovery!.SnmpProtocol.Should().Be(SnmpProtocolVersion.V2C);
        vm.LastPrinterDiscovery.SelectedTarget.Should().Be("192.168.1.50");
        vm.LastPrinterDiscovery.ScanResults.Should().ContainSingle();
        vm.LastPrinterDiscovery.ScanResults[0].SnmpStatus.Should().Be("Printer confirmed by SNMP");
        vm.LastPrinterDiscovery.ScanResults[0].SnmpInfo!.PrinterName.Should().Be("HP Office");
        vm.SelectedPrinterScanResult.Should().BeSameAs(vm.LastPrinterDiscovery.ScanResults[0]);
        host.BusyTransitions.Should().ContainInOrder(true, false);
        host.Attachments.Should().Contain(vm.LastPrinterDiscovery);
    }

    [Fact]
    public async Task IdentifySnmpAsync_InvalidTarget_DoesNotRememberOrCallCollector()
    {
        var collector = new FakeSnmpPrinterCollector(_ => new SnmpDeviceInfo { Success = true });
        var (vm, host, _) = Build(snmpPrinterCollector: collector);
        vm.PrinterTarget = "https://example.com/printer";

        await ((NetScopeDiagnosticCenter.UI.AsyncRelayCommand)vm.IdentifySelectedPrinterSnmpCommand).ExecuteAsync(null);

        collector.Targets.Should().BeEmpty();
        host.RememberedTargets.Should().BeEmpty();
        host.BusyTransitions.Should().BeEmpty();
        vm.LastPrinterDiscovery!.Mode.Should().Be("SNMP");
        vm.LastPrinterDiscovery.Verdict.Should().Contain("host name or IP address");
        vm.LastPrinterDiscovery.Evidence.Should().Contain(message => message.Contains("stopped before network"));
    }

    [Fact]
    public async Task IdentifySnmpAsync_PublicIp_DoesNotRememberOrCallCollector()
    {
        var collector = new FakeSnmpPrinterCollector(_ => new SnmpDeviceInfo { Success = true });
        var (vm, host, _) = Build(snmpPrinterCollector: collector);
        vm.PrinterTarget = "8.8.8.8";

        await ((NetScopeDiagnosticCenter.UI.AsyncRelayCommand)vm.IdentifySelectedPrinterSnmpCommand).ExecuteAsync(null);

        collector.Targets.Should().BeEmpty();
        host.RememberedTargets.Should().BeEmpty();
        host.BusyTransitions.Should().BeEmpty();
        vm.LastPrinterDiscovery!.Verdict.Should().Contain("authorized private IPv4");
    }

    [Fact]
    public async Task IdentifySnmpAsync_SnmpV3MissingCredentials_DoesNotSaveOrCallCollector()
    {
        var collector = new FakeSnmpPrinterCollector(_ => new SnmpDeviceInfo { Success = true });
        var (vm, host, _) = Build(snmpPrinterCollector: collector);
        vm.PrinterTarget = "192.168.1.50";
        vm.SelectedSnmpProtocol = SnmpProtocolVersion.V3AuthPriv;
        vm.SnmpV3UserName = "monitor";
        vm.SnmpV3AuthPassword = "auth-secret";
        vm.SnmpV3PrivacyPassword = "";
        vm.SaveSnmpCredentials = true;

        await ((NetScopeDiagnosticCenter.UI.AsyncRelayCommand)vm.IdentifySelectedPrinterSnmpCommand).ExecuteAsync(null);

        collector.Targets.Should().BeEmpty();
        host.RememberedTargets.Should().BeEmpty();
        host.BusyTransitions.Should().BeEmpty();
        vm.HasSavedSnmpCredentials.Should().BeFalse();
        vm.LastPrinterDiscovery!.Verdict.Should().Contain("SNMPv3 authPriv requires");
    }

    [Fact]
    public async Task AutoIdentifyScannedPrintersSnmpCommand_ConfirmsRealPrinterRows()
    {
        var collector = new FakeSnmpPrinterCollector(options => options.Target == "10.0.0.42"
            ? new SnmpDeviceInfo
            {
                Address = options.Target,
                Success = true,
                PrinterConfirmed = true,
                PrinterName = "HP Office",
                PrinterSerialNumber = "SN123",
                SysDescr = "HP LaserJet"
            }
            : new SnmpDeviceInfo
            {
                Address = options.Target,
                Success = true,
                PrinterConfirmed = false,
                SysName = "web-device",
                SysDescr = "embedded web server"
            });
        var (vm, host, _) = Build(snmpPrinterCollector: collector);
        var printer = new PrinterScanResult { Address = "10.0.0.42" };
        var other = new PrinterScanResult { Address = "10.0.0.43" };
        vm.LastPrinterDiscovery = new PrinterDiscoveryResult { ScanResults = [printer, other] };

        await ((NetScopeDiagnosticCenter.UI.AsyncRelayCommand)vm.AutoIdentifyScannedPrintersSnmpCommand).ExecuteAsync(null);

        collector.Targets.Should().Equal("10.0.0.42", "10.0.0.43");
        printer.SnmpStatus.Should().Be("Printer confirmed by SNMP");
        printer.Classification.Should().Be("Printer confirmed by SNMP");
        printer.SnmpInfo!.PrinterName.Should().Be("HP Office");
        other.SnmpStatus.Should().Be("SNMP responded");
        // No print-protocol port and no Printer-MIB evidence → plainly not a printer,
        // so the printers-only filter can hide it.
        other.Classification.Should().Be("Not a printer (SNMP)");
        vm.SelectedPrinterScanResult.Should().Be(printer);
        vm.IsPrinterSnmpIdentifyRunning.Should().BeFalse();
        host.BusyTransitions.Should().ContainInOrder(true, false);
        host.Attachments.Should().Contain(vm.LastPrinterDiscovery!);
        host.StatusMessages.Should().Contain(message => message.Contains("confirmed 1 printer"));
    }

    [Fact]
    public async Task AutoIdentifyScannedPrintersSnmpCommand_CapsCandidates()
    {
        var collector = new FakeSnmpPrinterCollector(options => new SnmpDeviceInfo
        {
            Address = options.Target,
            Error = "SNMP timed out on UDP 161."
        });
        var (vm, host, _) = Build(snmpPrinterCollector: collector);
        vm.LastPrinterDiscovery = new PrinterDiscoveryResult
        {
            ScanResults = Enumerable.Range(1, DiagnosticConstants.MaxPrinterAutoSnmpTargets + 3)
                .Select(index => new PrinterScanResult { Address = $"10.0.0.{index}" })
                .ToList()
        };

        await ((NetScopeDiagnosticCenter.UI.AsyncRelayCommand)vm.AutoIdentifyScannedPrintersSnmpCommand).ExecuteAsync(null);

        collector.Targets.Should().HaveCount(DiagnosticConstants.MaxPrinterAutoSnmpTargets);
        vm.LastPrinterDiscovery!.Verdict.Should().Contain("safety cap");
        host.StatusMessages.Should().Contain(message => message.Contains("safety cap"));
    }

    [Fact]
    public async Task AutoIdentifyScannedPrintersSnmpCommand_WithoutScanRows_NotifiesAndDoesNothing()
    {
        var collector = new FakeSnmpPrinterCollector(_ => new SnmpDeviceInfo());
        var (vm, host, _) = Build(snmpPrinterCollector: collector);

        await ((NetScopeDiagnosticCenter.UI.AsyncRelayCommand)vm.AutoIdentifyScannedPrintersSnmpCommand).ExecuteAsync(null);

        collector.Targets.Should().BeEmpty();
        host.BusyTransitions.Should().BeEmpty();
        host.StatusMessages.Should().ContainSingle(message => message.Contains("Run IP Scan first"));
    }

    [Fact]
    public async Task AutoIdentifyScannedPrintersSnmpCommand_SnmpV3MissingCredentials_DoesNotSaveOrScan()
    {
        var collector = new FakeSnmpPrinterCollector(_ => new SnmpDeviceInfo { Success = true });
        var (vm, host, _) = Build(snmpPrinterCollector: collector);
        vm.LastPrinterDiscovery = new PrinterDiscoveryResult
        {
            ScanResults = [new PrinterScanResult { Address = "192.168.1.50" }]
        };
        vm.SelectedSnmpProtocol = SnmpProtocolVersion.V3AuthPriv;
        vm.SnmpV3UserName = "monitor";
        vm.SnmpV3AuthPassword = "";
        vm.SnmpV3PrivacyPassword = "privacy-secret";
        vm.SaveSnmpCredentials = true;

        await ((NetScopeDiagnosticCenter.UI.AsyncRelayCommand)vm.AutoIdentifyScannedPrintersSnmpCommand).ExecuteAsync(null);

        collector.Targets.Should().BeEmpty();
        host.BusyTransitions.Should().BeEmpty();
        vm.HasSavedSnmpCredentials.Should().BeFalse();
        vm.LastPrinterDiscovery!.Verdict.Should().Contain("SNMPv3 authPriv requires");
    }

    [Fact]
    public void RemoveSelectedPrinterScanResultCommand_RemovesSelectedRowAndClearsMatchingTarget()
    {
        var (vm, host, _) = Build();
        var remove = new PrinterScanResult { Address = "10.0.0.42", SnmpStatus = "Printer confirmed by SNMP" };
        var keep = new PrinterScanResult { Address = "10.0.0.43" };
        vm.LastPrinterDiscovery = new PrinterDiscoveryResult
        {
            ScanResults = [remove, keep]
        };
        vm.SelectedPrinterScanResult = remove;
        vm.PrinterTarget = "10.0.0.42";

        vm.RemoveSelectedPrinterScanResultCommand.Execute(null);

        vm.LastPrinterDiscovery!.ScanResults.Should().ContainSingle().Which.Address.Should().Be("10.0.0.43");
        vm.SelectedPrinterScanResult.Should().Be(keep);
        vm.SelectedPrinterTarget.Should().Be("10.0.0.43");
        vm.PrinterTarget.Should().BeEmpty();
        host.Attachments.Should().Contain(vm.LastPrinterDiscovery);
        host.StatusMessages.Should().Contain(message => message.Contains("Removed 10.0.0.42"));
    }

    [Fact]
    public void RemoveSelectedPrinterScanResultCommand_WithoutSelection_Notifies()
    {
        var (vm, host, _) = Build();

        vm.RemoveSelectedPrinterScanResultCommand.Execute(null);

        host.StatusMessages.Should().ContainSingle(message => message.Contains("Select a scanned/SNMP printer row"));
    }

    [Fact]
    public void RemoveSelectedPrinterScanResultCommand_LastRow_ClearsSelectedTarget()
    {
        var (vm, _, _) = Build();
        var row = new PrinterScanResult { Address = "10.0.0.42" };
        vm.LastPrinterDiscovery = new PrinterDiscoveryResult { ScanResults = [row] };
        vm.SelectedPrinterScanResult = row;
        vm.PrinterTarget = "10.0.0.42";

        vm.RemoveSelectedPrinterScanResultCommand.Execute(null);

        vm.LastPrinterDiscovery!.ScanResults.Should().BeEmpty();
        vm.SelectedPrinterScanResult.Should().BeNull();
        vm.SelectedPrinterTarget.Should().BeEmpty();
        vm.PrinterTarget.Should().BeEmpty();
    }

    [Fact]
    public async Task DetectPrintServerCommand_DetectsServerFromInstalledSharedQueuesAndDiscovers()
    {
        var localCollector = new FakeLocalPrinterCollector(new PrinterDiscoveryResult
        {
            Verdict = "Local printers were enumerated.",
            LocalPrinters =
            [
                new PrinterInfo { Name = @"\\PRTSRV01\HP-01", ConnectionName = @"\\PRTSRV01\HP-01" },
                new PrinterInfo { Name = "HP-02", ConnectionName = @"\\PRTSRV01\HP-02" },
                new PrinterInfo { Name = "Local USB", PortName = "USB001" }
            ]
        });
        var printServerCollector = new FakePrintServerCollector(server => new PrinterDiscoveryResult
        {
            Source = server,
            Verdict = "Print server reachable and printers were enumerated.",
            Severity = "OK",
            Printers =
            [
                new PrinterInfo { Name = "HP-01", ShareName = "HP-01", ConnectionName = $@"\\{server}\HP-01", Installable = true }
            ]
        });
        var (vm, host, _) = Build(localPrinterCollector: localCollector, printServerCollector: printServerCollector);

        await ((NetScopeDiagnosticCenter.UI.AsyncRelayCommand)vm.DetectPrintServerCommand).ExecuteAsync(null);

        localCollector.CollectCount.Should().Be(1);
        printServerCollector.DiscoveredServers.Should().ContainSingle("PRTSRV01");
        vm.PrintServerName.Should().Be("PRTSRV01");
        vm.LastPrinterDiscovery!.Printers.Should().ContainSingle(printer => printer.ConnectionName == @"\\PRTSRV01\HP-01");
        vm.SelectedPrintServerPrinter.Should().NotBeNull();
        host.RememberedServers.Should().ContainSingle("PRTSRV01");
        host.StatusMessages.Should().Contain(message => message.Contains("Print server reachable"));
    }

    [Fact]
    public async Task DetectPrintServerCommand_NoSharedQueues_NotifiesManualFallback()
    {
        var localCollector = new FakeLocalPrinterCollector(new PrinterDiscoveryResult
        {
            LocalPrinters =
            [
                new PrinterInfo { Name = "Local TCP", PortName = "IP_10.0.0.50", PortHostAddress = "10.0.0.50" }
            ]
        });
        var printServerCollector = new FakePrintServerCollector(_ => new PrinterDiscoveryResult());
        var (vm, host, _) = Build(localPrinterCollector: localCollector, printServerCollector: printServerCollector);

        await ((NetScopeDiagnosticCenter.UI.AsyncRelayCommand)vm.DetectPrintServerCommand).ExecuteAsync(null);

        printServerCollector.DiscoveredServers.Should().BeEmpty();
        vm.PrintServerName.Should().BeEmpty();
        host.StatusMessages.Should().Contain(message => message.Contains("Enter the print server manually"));
    }

    [Fact]
    public async Task ScanPrinterRangeAsync_EmptyRange_NotifiesAndDoesNothing()
    {
        var (vm, host, _) = Build();
        vm.PrinterScanRange = "";

        await ((NetScopeDiagnosticCenter.UI.AsyncRelayCommand)vm.ScanPrinterRangeCommand).ExecuteAsync(null);

        host.StatusMessages.Should().Contain(s => s.Contains("Enter a scan range"));
        host.BusyTransitions.Should().BeEmpty();
    }

    [Fact]
    public async Task DiscoverPrintServerAsync_EmptyName_NotifiesAndDoesNothing()
    {
        var (vm, host, _) = Build();
        vm.PrintServerName = "";

        await ((NetScopeDiagnosticCenter.UI.AsyncRelayCommand)vm.DiscoverPrintServerCommand).ExecuteAsync(null);

        host.StatusMessages.Should().Contain(s => s.Contains("Enter a print server"));
        host.BusyTransitions.Should().BeEmpty();
    }

    [Fact]
    public async Task DiscoverPrintServerAsync_ManualServer_NormalizesDiscoversSelectsAndRemembers()
    {
        var printServerCollector = new FakePrintServerCollector(server => new PrinterDiscoveryResult
        {
            Source = server,
            Verdict = "Print server reachable and printers were enumerated.",
            Severity = "OK",
            PrintServerSpoolerStatus = "Running",
            Printers =
            [
                new PrinterInfo
                {
                    Name = "HP-Queue",
                    ShareName = "HP-Queue",
                    ConnectionName = $@"\\{server}\HP-Queue",
                    PortHostAddress = "192.168.10.42",
                    Installable = true
                }
            ]
        });
        var (vm, host, _) = Build(printServerCollector: printServerCollector);
        vm.PrintServerName = @"\\PRTSRV02";

        await ((NetScopeDiagnosticCenter.UI.AsyncRelayCommand)vm.DiscoverPrintServerCommand).ExecuteAsync(null);

        vm.PrintServerName.Should().Be("PRTSRV02");
        printServerCollector.DiscoveredServers.Should().ContainSingle("PRTSRV02");
        host.RememberedServers.Should().ContainSingle("PRTSRV02");
        vm.LastPrinterDiscovery!.PrintServerSpoolerStatus.Should().Be("Running");
        vm.LastPrinterDiscovery.Printers.Should().ContainSingle(printer => printer.ConnectionName == @"\\PRTSRV02\HP-Queue");
        vm.SelectedPrintServerPrinter.Should().BeSameAs(vm.LastPrinterDiscovery.Printers[0]);
        vm.SelectedPrinterTarget.Should().Be("192.168.10.42");
        vm.SelectedPrinterTargetSource.Should().Contain("HP-Queue");
    }

    [Fact]
    public async Task DiscoverPrintServerAsync_InvalidManualTarget_DoesNotDiscoverOrRemember()
    {
        var printServerCollector = new FakePrintServerCollector(_ => new PrinterDiscoveryResult
        {
            Verdict = "Should not be called."
        });
        var (vm, host, _) = Build(printServerCollector: printServerCollector);
        vm.PrintServerName = "print.example.com";

        await ((NetScopeDiagnosticCenter.UI.AsyncRelayCommand)vm.DiscoverPrintServerCommand).ExecuteAsync(null);

        printServerCollector.DiscoveredServers.Should().BeEmpty();
        host.RememberedServers.Should().BeEmpty();
        host.BusyTransitions.Should().BeEmpty();
        vm.LastPrinterDiscovery!.Severity.Should().Be("Critical");
        vm.LastPrinterDiscovery.Verdict.Should().Contain("single-label LAN name");
        vm.LastPrinterDiscovery.Printers.Should().BeEmpty();
    }

    [Fact]
    public async Task IdentifySnmpAsync_NoTarget_NotifiesAndDoesNothing()
    {
        var (vm, host, _) = Build();
        // Ensure no target is set
        vm.SelectedPrinterTarget = "";
        vm.PrinterTarget = "";

        await ((NetScopeDiagnosticCenter.UI.AsyncRelayCommand)vm.IdentifySelectedPrinterSnmpCommand).ExecuteAsync(null);

        host.StatusMessages.Should().Contain(s => s.Contains("Select a scanned printer"));
        host.RememberedTargets.Should().BeEmpty();
    }

    [Fact]
    public async Task InstallQueueAsync_NoSelection_NotifiesAndDoesNothing()
    {
        var (vm, host, _) = Build();
        vm.SelectedPrintServerPrinter = null;

        await ((NetScopeDiagnosticCenter.UI.AsyncRelayCommand)vm.InstallSelectedPrinterQueueCommand).ExecuteAsync(null);

        host.StatusMessages.Should().Contain(s => s.Contains("Select a shared print server queue"));
    }

    [Fact]
    public async Task InstallQueueAsync_WithoutConfirmation_NotifiesAndDoesNotCallInstaller()
    {
        var installer = new FakePrinterQueueInstaller(_ => new PrinterInstallResult { Success = true });
        var (vm, host, _) = Build(printerQueueInstaller: installer);
        vm.SelectedPrintServerPrinter = new PrinterInfo
        {
            Name = "HP-Queue",
            ConnectionName = @"\\PRTSRV01\HP-Queue",
            Installable = true
        };
        vm.ConfirmPrinterInstall = false;

        await ((NetScopeDiagnosticCenter.UI.AsyncRelayCommand)vm.InstallSelectedPrinterQueueCommand).ExecuteAsync(null);

        installer.Connections.Should().BeEmpty();
        host.BusyTransitions.Should().BeEmpty();
        host.StatusMessages.Should().Contain(message => message.Contains("Confirm printer queue installation"));
    }

    [Fact]
    public async Task InstallQueueAsync_InvalidConnection_DoesNotCallInstaller()
    {
        var installer = new FakePrinterQueueInstaller(_ => new PrinterInstallResult { Success = true });
        var (vm, host, _) = Build(printerQueueInstaller: installer);
        vm.SelectedPrintServerPrinter = new PrinterInfo
        {
            Name = "HP-Queue",
            ConnectionName = @"\\print.example.com\HP-Queue",
            Installable = true
        };
        vm.ConfirmPrinterInstall = true;

        await ((NetScopeDiagnosticCenter.UI.AsyncRelayCommand)vm.InstallSelectedPrinterQueueCommand).ExecuteAsync(null);

        installer.Connections.Should().BeEmpty();
        host.BusyTransitions.Should().BeEmpty();
        host.StatusMessages.Should().Contain(message => message.Contains("connection is invalid"));
        vm.ConfirmPrinterInstall.Should().BeTrue();
    }

    [Fact]
    public async Task InstallQueueAsync_SuccessRefreshesLocalAndKeepsSelectedQueue()
    {
        var firstQueue = new PrinterInfo
        {
            Name = "HP-Queue",
            ConnectionName = @"\\PRTSRV01\HP-Queue",
            Installable = true
        };
        var selectedQueue = new PrinterInfo
        {
            Name = "Canon-Queue",
            ConnectionName = @"\\PRTSRV01\Canon-Queue",
            Installable = true
        };
        var installer = new FakePrinterQueueInstaller(connection => new PrinterInstallResult
        {
            Success = true,
            Severity = "OK",
            Verdict = "Printer queue installed successfully.",
            Category = "Installed",
            ConnectionName = connection,
            Evidence = [$"Installed {connection}."]
        });
        var localCollector = new FakeLocalPrinterCollector(new PrinterDiscoveryResult
        {
            Verdict = "Local printers were enumerated.",
            Severity = "OK",
            LocalPrinters = [new PrinterInfo { Name = "Canon-Queue" }]
        });
        var (vm, host, _) = Build(localPrinterCollector: localCollector, printerQueueInstaller: installer);
        vm.LastPrinterDiscovery = new PrinterDiscoveryResult
        {
            Printers = [firstQueue, selectedQueue]
        };
        vm.SelectedPrintServerPrinter = selectedQueue;
        vm.ConfirmPrinterInstall = true;

        await ((NetScopeDiagnosticCenter.UI.AsyncRelayCommand)vm.InstallSelectedPrinterQueueCommand).ExecuteAsync(null);

        installer.Connections.Should().ContainSingle(@"\\PRTSRV01\Canon-Queue");
        localCollector.CollectCount.Should().Be(1);
        selectedQueue.InstallStatus.Should().Be("Installed");
        selectedQueue.InstallError.Should().BeEmpty();
        vm.SelectedPrintServerPrinter.Should().BeSameAs(selectedQueue);
        vm.LastPrinterDiscovery!.LastInstall.Should().NotBeNull();
        vm.LastPrinterDiscovery.LastInstall!.ConnectionName.Should().Be(@"\\PRTSRV01\Canon-Queue");
        vm.LastPrinterDiscovery.LocalPrinters.Should().ContainSingle(printer => printer.Name == "Canon-Queue");
        vm.ConfirmPrinterInstall.Should().BeFalse();
        host.BusyTransitions.Should().ContainInOrder(true, false);
        host.Attachments.Should().Contain(vm.LastPrinterDiscovery);
    }

    [Fact]
    public async Task InstallQueueAsync_FailureStoresErrorAndDoesNotRefreshLocalPrinters()
    {
        var selectedQueue = new PrinterInfo
        {
            Name = "HP-Queue",
            ConnectionName = @"\\PRTSRV01\HP-Queue",
            Installable = true
        };
        var installer = new FakePrinterQueueInstaller(connection => new PrinterInstallResult
        {
            Success = false,
            Severity = "Warning",
            Verdict = "Printer queue install failed.",
            Category = "AccessDenied",
            ConnectionName = connection,
            Error = "Access denied.",
            Evidence = ["Add-Printer failed: Access denied."]
        });
        var localCollector = new FakeLocalPrinterCollector(new PrinterDiscoveryResult());
        var (vm, host, _) = Build(localPrinterCollector: localCollector, printerQueueInstaller: installer);
        vm.LastPrinterDiscovery = new PrinterDiscoveryResult
        {
            Printers = [selectedQueue]
        };
        vm.SelectedPrintServerPrinter = selectedQueue;
        vm.ConfirmPrinterInstall = true;

        await ((NetScopeDiagnosticCenter.UI.AsyncRelayCommand)vm.InstallSelectedPrinterQueueCommand).ExecuteAsync(null);

        installer.Connections.Should().ContainSingle(@"\\PRTSRV01\HP-Queue");
        localCollector.CollectCount.Should().Be(0);
        selectedQueue.InstallStatus.Should().Be("AccessDenied");
        selectedQueue.InstallError.Should().Be("Access denied.");
        vm.LastPrinterDiscovery!.LastInstall.Should().NotBeNull();
        vm.LastPrinterDiscovery.LastInstall!.Category.Should().Be("AccessDenied");
        vm.ConfirmPrinterInstall.Should().BeFalse();
        host.StatusMessages.Should().Contain(message => message.Contains("Access denied"));
    }

    [Fact]
    public async Task DetectPrintServer_NoLocalConnections_FallsBackToActiveDirectory()
    {
        var localCollector = new FakeLocalPrinterCollector(new PrinterDiscoveryResult
        {
            Verdict = "Local printers were enumerated.",
            Severity = "OK",
            LocalPrinters = [new PrinterInfo { Name = "Microsoft Print to PDF" }]
        });
        var printServerCollector = new FakePrintServerCollector(server => new PrinterDiscoveryResult
        {
            Source = server,
            Verdict = "Print server reachable and printers were enumerated.",
            Severity = "OK"
        });
        var (vm, host, _) = Build(
            localPrinterCollector: localCollector,
            printServerCollector: printServerCollector,
            adPrintServerLocator: () => [new AdPrintServerInfo("PRINT01", 12), new AdPrintServerInfo("PRINT02", 3)]);

        await ((NetScopeDiagnosticCenter.UI.AsyncRelayCommand)vm.DetectPrintServerCommand).ExecuteAsync(null);

        vm.PrintServerName.Should().Be("PRINT01", "the busiest AD-published server wins");
        printServerCollector.DiscoveredServers.Should().ContainSingle("PRINT01");
        host.RememberedServers.Should().ContainSingle("PRINT01");
        host.StatusMessages.Should().Contain(message => message.Contains("Active Directory"));
        host.StatusMessages.Should().Contain(message => message.Contains("PRINT02"), "all published servers are reported");
    }

    [Fact]
    public async Task DetectPrintServer_NothingLocallyAndNothingInAd_AsksForManualEntry()
    {
        var localCollector = new FakeLocalPrinterCollector(new PrinterDiscoveryResult
        {
            Verdict = "Local printers were enumerated.",
            Severity = "OK",
            LocalPrinters = [new PrinterInfo { Name = "Microsoft Print to PDF" }]
        });
        var printServerCollector = new FakePrintServerCollector(_ => new PrinterDiscoveryResult { Verdict = "Should not be called." });
        var (vm, host, _) = Build(
            localPrinterCollector: localCollector,
            printServerCollector: printServerCollector,
            adPrintServerLocator: () => []);

        await ((NetScopeDiagnosticCenter.UI.AsyncRelayCommand)vm.DetectPrintServerCommand).ExecuteAsync(null);

        printServerCollector.DiscoveredServers.Should().BeEmpty();
        host.StatusMessages.Should().Contain(message => message.Contains("Enter the print server manually"));
    }

    [Fact]
    public void FilteredScanResults_HidesSnmpConfirmedNonPrinters_ByDefault()
    {
        var (vm, _, _) = Build();
        vm.LastPrinterDiscovery = new PrinterDiscoveryResult
        {
            ScanResults =
            [
                new PrinterScanResult { Address = "192.168.1.10", Classification = "Printer confirmed by SNMP" },
                new PrinterScanResult { Address = "192.168.1.11", Classification = "Not a printer (SNMP)" },
                new PrinterScanResult { Address = "192.168.1.12", Classification = "Likely printer" }
            ]
        };

        vm.ShowOnlyPrinters.Should().BeTrue("printers-only is the production default");
        vm.FilteredScanResults.Should().HaveCount(2);
        vm.FilteredScanResults.Should().NotContain(row => row.Address == "192.168.1.11");
        vm.HasHiddenScanResults.Should().BeTrue();
        vm.HiddenScanResultCountText.Should().Contain("1 non-printer");

        vm.ShowOnlyPrinters = false;

        vm.FilteredScanResults.Should().HaveCount(3);
        vm.HasHiddenScanResults.Should().BeFalse();
    }

    [Fact]
    public async Task IdentifySnmp_ManualTargetWithoutPrintPorts_MarksRowAsNonPrinter()
    {
        var snmp = new FakeSnmpPrinterCollector(_ => new SnmpDeviceInfo
        {
            Address = "192.168.1.50",
            Success = true,
            PrinterConfirmed = false,
            SysDescr = "Cisco SG350 managed switch"
        });
        var (vm, _, _) = Build(snmpPrinterCollector: snmp);
        vm.PrinterTarget = "192.168.1.50";

        await ((NetScopeDiagnosticCenter.UI.AsyncRelayCommand)vm.IdentifySelectedPrinterSnmpCommand).ExecuteAsync(null);

        var row = vm.LastPrinterDiscovery!.ScanResults.Single(item => item.Address == "192.168.1.50");
        row.Classification.Should().Be("Not a printer (SNMP)");
        vm.FilteredScanResults.Should().NotContain(item => item.Address == "192.168.1.50");
    }

    [Fact]
    public async Task IdentifySnmp_PrintPortHostWithoutPrinterMib_StaysLikelyPrinter()
    {
        var snmp = new FakeSnmpPrinterCollector(_ => new SnmpDeviceInfo
        {
            Address = "192.168.1.60",
            Success = true,
            PrinterConfirmed = false,
            SysDescr = "Cheap label printer with minimal MIB"
        });
        var (vm, _, _) = Build(snmpPrinterCollector: snmp);
        vm.LastPrinterDiscovery = new PrinterDiscoveryResult
        {
            ScanResults =
            [
                new PrinterScanResult { Address = "192.168.1.60", OpenPorts = [9100], Classification = "Likely printer" }
            ]
        };
        vm.SelectedPrinterScanResult = vm.LastPrinterDiscovery.ScanResults[0];

        await ((NetScopeDiagnosticCenter.UI.AsyncRelayCommand)vm.IdentifySelectedPrinterSnmpCommand).ExecuteAsync(null);

        var row = vm.LastPrinterDiscovery.ScanResults[0];
        row.Classification.Should().Be("Likely printer (SNMP identity unconfirmed)");
        vm.FilteredScanResults.Should().Contain(row);
    }

    [Fact]
    public async Task InstallQueue_WithSetDefaultTicked_MakesInstalledQueueTheDefault()
    {
        var localCollector = new FakeLocalPrinterCollector(new PrinterDiscoveryResult
        {
            Verdict = "Local printers were enumerated.",
            Severity = "OK"
        });
        var installer = new FakePrinterQueueInstaller(connection => new PrinterInstallResult
        {
            Success = true,
            Verdict = "Printer queue installed.",
            Severity = "OK",
            Category = "Installed"
        });
        var (vm, host, _) = Build(localPrinterCollector: localCollector, printerQueueInstaller: installer);
        vm.SelectedPrintServerPrinter = new PrinterInfo
        {
            Name = "HP-Queue",
            ConnectionName = @"\\PRTSRV01\HP-Queue",
            Installable = true
        };
        vm.ConfirmPrinterInstall = true;
        vm.SetDefaultAfterInstall = true;

        await ((NetScopeDiagnosticCenter.UI.AsyncRelayCommand)vm.InstallSelectedPrinterQueueCommand).ExecuteAsync(null);

        installer.Connections.Should().ContainSingle(@"\\PRTSRV01\HP-Queue");
        localCollector.DefaultPrinterRequests.Should().ContainSingle(@"\\PRTSRV01\HP-Queue");
    }

    [Fact]
    public async Task InstallQueue_WithSetDefaultUnticked_DoesNotTouchDefaultPrinter()
    {
        var localCollector = new FakeLocalPrinterCollector(new PrinterDiscoveryResult { Verdict = "OK", Severity = "OK" });
        var installer = new FakePrinterQueueInstaller(_ => new PrinterInstallResult
        {
            Success = true,
            Verdict = "Printer queue installed.",
            Severity = "OK",
            Category = "Installed"
        });
        var (vm, _, _) = Build(localPrinterCollector: localCollector, printerQueueInstaller: installer);
        vm.SelectedPrintServerPrinter = new PrinterInfo
        {
            Name = "HP-Queue",
            ConnectionName = @"\\PRTSRV01\HP-Queue",
            Installable = true
        };
        vm.ConfirmPrinterInstall = true;
        vm.SetDefaultAfterInstall = false;

        await ((NetScopeDiagnosticCenter.UI.AsyncRelayCommand)vm.InstallSelectedPrinterQueueCommand).ExecuteAsync(null);

        localCollector.DefaultPrinterRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task AdPrintServerLocator_OnThisMachine_NeverThrows()
    {
        // Integration guard: on a workgroup machine this returns empty; on a domain
        // machine it returns published servers. Either way it must not throw.
        var result = await Task.Run(() => AdPrintServerLocator.FindPublishedPrintServers());

        result.Should().NotBeNull();
    }
}
