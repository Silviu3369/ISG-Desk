using System.IO;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using NetScopeDiagnosticCenter.Collectors;
using NetScopeDiagnosticCenter.Core;
using NetScopeDiagnosticCenter.Core.Models;
using NetScopeDiagnosticCenter.Core.Monitoring;
using NetScopeDiagnosticCenter.Infrastructure;
using NetScopeDiagnosticCenter.Reports;
using NetScopeDiagnosticCenter.UI;
using NetScopeDiagnosticCenter.UI.ViewModels;

namespace NetScopeDiagnosticCenter.Tests;

/// <summary>
/// Smoke tests for the DI composition root (App.ConfigureServices).
/// These catch missing registrations at build time rather than at runtime.
/// </summary>
public class AppCompositionTests : IDisposable
{
    private readonly string _isolatedAppData;

    public AppCompositionTests()
    {
        _isolatedAppData = Path.Combine(Path.GetTempPath(), "NetScopeTests-DI-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_isolatedAppData);
    }

    public void Dispose()
    {
        try { Directory.Delete(_isolatedAppData, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>
    /// Builds the same service collection that App.OnStartup would build, by reflection
    /// against the private static ConfigureServices method. We do not instantiate App
    /// itself (that would require a running WPF dispatcher).
    /// </summary>
    private IServiceProvider BuildProvider(Action<IServiceCollection>? overrideServices = null)
    {
        var services = new ServiceCollection();
        var configureMethod = typeof(App).GetMethod(
            "ConfigureServices",
            BindingFlags.NonPublic | BindingFlags.Static);
        configureMethod.Should().NotBeNull("App.ConfigureServices is the composition root and must remain accessible");
        configureMethod!.Invoke(null, [services]);
        services.AddSingleton(new AppStorageService(_isolatedAppData));
        overrideServices?.Invoke(services);
        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Counts collector invocations without spawning PowerShell. Used only by the
    /// construction-side-effect regression test below; every other test resolves
    /// the unmodified production graph.
    /// </summary>
    private sealed class CountingOverviewCollector : ISystemOverviewCollector
    {
        public int Calls { get; private set; }

        public Task<SystemOverview> GetAsync(CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new SystemOverview());
        }
    }

    [Fact]
    public void Container_ResolvesAllInfrastructure()
    {
        using var provider = (ServiceProvider)BuildProvider();

        provider.GetRequiredService<AppStorageService>().Should().NotBeNull();
        provider.GetRequiredService<ILoggingService>().Should().NotBeNull();
        provider.GetRequiredService<LoggingService>().Should().NotBeNull();
        provider.GetRequiredService<SecureCredentialService>().Should().NotBeNull();
        provider.GetRequiredService<SnmpCredentialStore>().Should().NotBeNull();
        provider.GetRequiredService<IPowerShellRunner>().Should().NotBeNull();
        provider.GetRequiredService<PowerShellRunner>().Should().NotBeNull();
        provider.GetRequiredService<SnmpClientService>().Should().NotBeNull();
        provider.GetRequiredService<ActivityFeedService>().Should().NotBeNull();
    }

    [Fact]
    public void Container_ResolvesAllCollectors()
    {
        using var provider = (ServiceProvider)BuildProvider();

        provider.GetRequiredService<NetworkAdapterCollector>().Should().NotBeNull();
        provider.GetRequiredService<IpConfigurationCollector>().Should().NotBeNull();
        provider.GetRequiredService<DnsCollector>().Should().NotBeNull();
        provider.GetRequiredService<WifiCollector>().Should().NotBeNull();
        provider.GetRequiredService<PortTestCollector>().Should().NotBeNull();
        provider.GetRequiredService<TargetConnectivityCollector>().Should().NotBeNull();
        provider.GetRequiredService<TargetShareDiscoveryCollector>().Should().NotBeNull();
        provider.GetRequiredService<TargetServiceDiscoveryCollector>().Should().NotBeNull();
        provider.GetRequiredService<DomainControllerDiscoveryCollector>().Should().NotBeNull();
        provider.GetRequiredService<DomainCollector>().Should().NotBeNull();
        provider.GetRequiredService<DhcpCollector>().Should().NotBeNull();
        provider.GetRequiredService<PcContextCollector>().Should().NotBeNull();
        provider.GetRequiredService<LocalPrinterCollector>().Should().NotBeNull();
        provider.GetRequiredService<PrintServerCollector>().Should().NotBeNull();
        provider.GetRequiredService<PrinterDiscoveryCollector>().Should().NotBeNull();
        provider.GetRequiredService<SnmpPrinterCollector>().Should().NotBeNull();
        provider.GetRequiredService<PrinterQueueInstaller>().Should().NotBeNull();
        provider.GetRequiredService<NetworkDeviceCollector>().Should().NotBeNull();
        provider.GetRequiredService<ISystemOverviewCollector>().Should().NotBeNull();
    }

    [Fact]
    public void Container_ResolvesCoreEngines()
    {
        using var provider = (ServiceProvider)BuildProvider();

        provider.GetRequiredService<RuleEngine>().Should().NotBeNull();
        provider.GetRequiredService<HealthScoreCalculator>().Should().NotBeNull();
        provider.GetRequiredService<ScenarioEngine>().Should().NotBeNull();
        provider.GetRequiredService<DiagnosticEngine>().Should().NotBeNull();
    }

    [Fact]
    public void Container_ResolvesReports()
    {
        using var provider = (ServiceProvider)BuildProvider();

        provider.GetRequiredService<HtmlReportBuilder>().Should().NotBeNull();
        provider.GetRequiredService<TextSummaryBuilder>().Should().NotBeNull();
        provider.GetRequiredService<ReportStorageService>().Should().NotBeNull();
    }

    [Fact]
    public void Container_ResolvesViewModels()
    {
        using var provider = (ServiceProvider)BuildProvider();

        provider.GetRequiredService<MainViewModel>().Should().NotBeNull();
    }

    [Fact]
    public async Task MainViewModel_Construction_DoesNotRunOverviewCollector()
    {
        // Regression: the ctor used to fire-and-forget TechnicianHome.EnsureLoadedAsync(),
        // so every test resolving MainViewModel spawned a real powershell.exe (and could
        // orphan isgdesk-*.ps1 temp scripts in %TEMP% when xunit exited first). The
        // kick-off is now an explicit hook App.OnStartup calls after creating the window.
        var collector = new CountingOverviewCollector();
        using var provider = (ServiceProvider)BuildProvider(
            services => services.AddSingleton<ISystemOverviewCollector>(collector));
        var mainVm = provider.GetRequiredService<MainViewModel>();

        collector.Calls.Should().Be(0, "constructing MainViewModel must not start the overview collection");

        await mainVm.StartInitialLoadAsync();
        collector.Calls.Should().Be(1, "App.OnStartup's explicit hook starts the load");

        await mainVm.StartInitialLoadAsync();
        collector.Calls.Should().Be(1, "EnsureLoadedAsync is single-load guarded");
    }

    [Fact]
    public void Container_ResolvesMonitoring()
    {
        using var provider = (ServiceProvider)BuildProvider();

        provider.GetRequiredService<IPingProbe>().Should().NotBeNull();
        provider.GetRequiredService<IMonitoringSessionFactory>().Should().NotBeNull();
    }

    [Fact]
    public void MonitoringFactory_ProducesIndependentSessions()
    {
        using var provider = (ServiceProvider)BuildProvider();
        var factory = provider.GetRequiredService<IMonitoringSessionFactory>();

        var s1 = factory.Create(intervalMs: 1000, maxSamples: 10);
        using (s1)
        {
            using var s2 = factory.Create(intervalMs: 1000, maxSamples: 10);
            s2.Should().NotBeSameAs(s1);
        }
    }

    [Fact]
    public void MainViewModel_ForwardsSubVmPropertyChanges_SnmpV3Switch()
    {
        // Regression: switching to SNMPv3 didn't reveal credential inputs because
        // IsSnmpV3Selected PropertyChanged fires on PrintersViewModel, not on the
        // MainViewModel bound by XAML. App.xaml.cs ctor wires forwarding subscribers.
        using var provider = (ServiceProvider)BuildProvider();
        var mainVm = provider.GetRequiredService<MainViewModel>();

        var changed = new List<string?>();
        mainVm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        mainVm.SelectedSnmpProtocol = NetScopeDiagnosticCenter.Core.Models.SnmpProtocolVersion.V3AuthPriv;

        changed.Should().Contain(nameof(MainViewModel.SelectedSnmpProtocol));
        changed.Should().Contain(nameof(MainViewModel.IsSnmpV3Selected));
        changed.Should().Contain(nameof(MainViewModel.IsSnmpV2Selected));
        mainVm.IsSnmpV3Selected.Should().BeTrue();
    }

    [Fact]
    public void MainViewModel_ExposesNavigationSeverityBindings()
    {
        using var provider = (ServiceProvider)BuildProvider();
        var mainVm = provider.GetRequiredService<MainViewModel>();

        mainVm.NavigationDiagnosisSeverity.Should().Be("Unknown");
        mainVm.NavigationTargetedTestsSeverity.Should().Be("Unknown");

        mainVm.LastDiagnosis = new NetworkDiagnosisResult
        {
            Verdict = new DiagnosisVerdict { Severity = "Critical" }
        };
        mainVm.LastTargetedScenario = new ScenarioDiagnosisResult { Severity = "Warning" };
        mainVm.LastPrinterDiscovery = new PrinterDiscoveryResult { Severity = "OK" };
        mainVm.LastNetworkDevice = new NetworkDeviceResult { Severity = "Warning" };
        mainVm.LastNetworkDeviceScan = new NetworkDeviceScanResult { Severity = "Critical" };

        mainVm.NavigationDiagnosisSeverity.Should().Be("Critical");
        mainVm.NavigationTargetedTestsSeverity.Should().Be("Warning");
        mainVm.NavigationPrintersSeverity.Should().Be("OK");
        mainVm.NavigationNetworkDevicesSeverity.Should().Be("Critical");
        mainVm.NavigationWifiSeverity.Should().Be("Unknown");
        mainVm.NavigationReportsSeverity.Should().Be("Unknown");
    }

    [Fact]
    public void PrinterDiscoveryAttachment_DoesNotCreatePlaceholderDiagnosis()
    {
        using var provider = (ServiceProvider)BuildProvider();
        var mainVm = provider.GetRequiredService<MainViewModel>();
        var discovery = new PrinterDiscoveryResult
        {
            Severity = "OK",
            Verdict = "Local printers were enumerated."
        };
        mainVm.LastPrinterDiscovery = discovery;

        ((PrintersViewModel.IHost)mainVm).AttachPrinterDiscovery(discovery);

        mainVm.LastDiagnosis.Should().BeNull();
        mainVm.LastPrinterDiscovery.Should().BeSameAs(discovery);
        mainVm.NavigationPrintersSeverity.Should().Be("OK");
        mainVm.NavigationDiagnosisSeverity.Should().Be("Unknown");
    }

    [Fact]
    public void PrinterDiscoveryAttachment_EnrichesExistingDiagnosisOnly()
    {
        using var provider = (ServiceProvider)BuildProvider();
        var mainVm = provider.GetRequiredService<MainViewModel>();
        var diagnosis = new NetworkDiagnosisResult
        {
            Verdict = new DiagnosisVerdict { Severity = "Warning" }
        };
        var discovery = new PrinterDiscoveryResult
        {
            Severity = "OK",
            Verdict = "Printer confirmed by SNMP."
        };
        mainVm.LastDiagnosis = diagnosis;
        mainVm.LastPrinterDiscovery = discovery;

        ((PrintersViewModel.IHost)mainVm).AttachPrinterDiscovery(discovery);

        mainVm.LastDiagnosis.Should().BeSameAs(diagnosis);
        mainVm.LastDiagnosis!.LastPrinterDiscovery.Should().BeSameAs(discovery);
        mainVm.NavigationDiagnosisSeverity.Should().Be("Warning");
        mainVm.NavigationPrintersSeverity.Should().Be("OK");
    }

    [Fact]
    public void MainViewModel_ExposesInstalledPrinterSelectionBinding()
    {
        using var provider = (ServiceProvider)BuildProvider();
        var mainVm = provider.GetRequiredService<MainViewModel>();
        var printer = new PrinterInfo { Name = "HP-Office" };
        var replacement = new PrinterInfo { Name = "Canon-Desk" };

        mainVm.SelectedLocalPrinter = printer;

        mainVm.Printers.SelectedLocalPrinter.Should().BeSameAs(printer);

        mainVm.Printers.SelectedLocalPrinter = replacement;
        mainVm.SelectedLocalPrinter.Should().BeSameAs(replacement);
    }

    [Fact]
    public void MainViewModel_ExposesPrintServerBindings()
    {
        using var provider = (ServiceProvider)BuildProvider();
        var mainVm = provider.GetRequiredService<MainViewModel>();
        var printer = new PrinterInfo { Name = "HP-Server", ConnectionName = @"\\PRTSRV01\HP-Server" };
        var replacement = new PrinterInfo { Name = "Canon-Server", ConnectionName = @"\\PRTSRV01\Canon-Server" };

        mainVm.PrintServerName = "PRTSRV01";
        mainVm.SelectedPrintServerPrinter = printer;

        mainVm.Printers.PrintServerName.Should().Be("PRTSRV01");
        mainVm.Printers.SelectedPrintServerPrinter.Should().BeSameAs(printer);

        mainVm.Printers.PrintServerName = "PRTSRV02";
        mainVm.Printers.SelectedPrintServerPrinter = replacement;

        mainVm.PrintServerName.Should().Be("PRTSRV02");
        mainVm.SelectedPrintServerPrinter.Should().BeSameAs(replacement);
    }

    [Fact]
    public void MainViewModel_ExposesPrinterIpScanBindings()
    {
        using var provider = (ServiceProvider)BuildProvider();
        var mainVm = provider.GetRequiredService<MainViewModel>();
        var row = new PrinterScanResult { Address = "10.0.0.42" };
        var replacement = new PrinterScanResult { Address = "10.0.0.43" };

        mainVm.PrinterScanRange = "10.0.0.0/24";
        mainVm.SelectedPrinterScanResult = row;

        mainVm.Printers.PrinterScanRange.Should().Be("10.0.0.0/24");
        mainVm.Printers.SelectedPrinterScanResult.Should().BeSameAs(row);

        mainVm.Printers.PrinterScanRange = "10.0.1.0/24";
        mainVm.Printers.SelectedPrinterScanResult = replacement;

        mainVm.PrinterScanRange.Should().Be("10.0.1.0/24");
        mainVm.SelectedPrinterScanResult.Should().BeSameAs(replacement);
    }

    [Fact]
    public void NetworkDeviceAttachment_DoesNotCreatePlaceholderDiagnosis()
    {
        using var provider = (ServiceProvider)BuildProvider();
        var mainVm = provider.GetRequiredService<MainViewModel>();
        var device = new NetworkDeviceResult
        {
            Severity = "Warning",
            Verdict = "Switch identity read completed with warnings."
        };
        mainVm.LastNetworkDevice = device;

        ((NetworkDevicesViewModel.IHost)mainVm).AttachNetworkDevice(device);

        mainVm.LastDiagnosis.Should().BeNull();
        mainVm.LastNetworkDevice.Should().BeSameAs(device);
        mainVm.NavigationNetworkDevicesSeverity.Should().Be("Warning");
        mainVm.NavigationDiagnosisSeverity.Should().Be("Unknown");
    }

    [Fact]
    public void NetworkDeviceScanAttachment_DoesNotCreatePlaceholderDiagnosis()
    {
        using var provider = (ServiceProvider)BuildProvider();
        var mainVm = provider.GetRequiredService<MainViewModel>();
        var scan = new NetworkDeviceScanResult
        {
            Severity = "Critical",
            Verdict = "SNMP scan found devices needing attention."
        };
        mainVm.LastNetworkDeviceScan = scan;

        ((NetworkDevicesViewModel.IHost)mainVm).AttachNetworkDeviceScan(scan);

        mainVm.LastDiagnosis.Should().BeNull();
        mainVm.LastNetworkDeviceScan.Should().BeSameAs(scan);
        mainVm.NavigationNetworkDevicesSeverity.Should().Be("Critical");
        mainVm.NavigationDiagnosisSeverity.Should().Be("Unknown");
    }

    [Fact]
    public void NetworkDeviceAttachments_EnrichExistingDiagnosisOnly()
    {
        using var provider = (ServiceProvider)BuildProvider();
        var mainVm = provider.GetRequiredService<MainViewModel>();
        var diagnosis = new NetworkDiagnosisResult
        {
            Verdict = new DiagnosisVerdict { Severity = "OK" }
        };
        var device = new NetworkDeviceResult
        {
            Severity = "Warning",
            Verdict = "Router identity read completed."
        };
        var scan = new NetworkDeviceScanResult
        {
            Severity = "Critical",
            Verdict = "LAN scan found one critical device."
        };
        mainVm.LastDiagnosis = diagnosis;
        mainVm.LastNetworkDevice = device;
        mainVm.LastNetworkDeviceScan = scan;

        ((NetworkDevicesViewModel.IHost)mainVm).AttachNetworkDevice(device);
        ((NetworkDevicesViewModel.IHost)mainVm).AttachNetworkDeviceScan(scan);

        mainVm.LastDiagnosis.Should().BeSameAs(diagnosis);
        mainVm.LastDiagnosis!.LastNetworkDevice.Should().BeSameAs(device);
        mainVm.LastDiagnosis.LastNetworkDeviceScan.Should().BeSameAs(scan);
        mainVm.NavigationDiagnosisSeverity.Should().Be("OK");
        mainVm.NavigationNetworkDevicesSeverity.Should().Be("Critical");
    }

    [Fact]
    public void MainViewModel_NormalizesNavigationKeys()
    {
        using var provider = (ServiceProvider)BuildProvider();
        var mainVm = provider.GetRequiredService<MainViewModel>();

        mainVm.CurrentPage = "reports";
        mainVm.CurrentPage.Should().Be("Reports");
        mainVm.CurrentPageDisplayName.Should().Be("Report Center");
        mainVm.CurrentPageIcon.Should().Be("\uE8A5");
        mainVm.CurrentPageAccentStart.Should().Be("#E11D48");
        mainVm.CurrentPageAccentEnd.Should().Be("#BE123C");

        mainVm.CurrentPage = "  Wifi  ";
        mainVm.CurrentPage.Should().Be("Wifi");
        mainVm.CurrentPageDisplayName.Should().Be("Wi-Fi Analyzer");
        mainVm.CurrentPageIcon.Should().Be("\uE701");
        mainVm.CurrentPageAccentStart.Should().Be("#0EA5E9");
        mainVm.CurrentPageAccentEnd.Should().Be("#0369A1");
    }

    [Fact]
    public void MainViewModel_InvalidNavigationKey_FallsBackToTechnicianHome()
    {
        using var provider = (ServiceProvider)BuildProvider();
        var mainVm = provider.GetRequiredService<MainViewModel>();

        mainVm.CurrentPage = "MissingModule";

        mainVm.CurrentPage.Should().Be("TechnicianHome");
        mainVm.CurrentPageDisplayName.Should().Be("Technician Home");
        mainVm.CurrentPageSubtitle.Should().Be("Local system overview - read-only");
    }

    [Fact]
    public void MainViewModel_SharesSnmpCredentialsBetweenPrintersAndNetworkDevices()
    {
        using var provider = (ServiceProvider)BuildProvider();
        var mainVm = provider.GetRequiredService<MainViewModel>();

        mainVm.SnmpCommunity = "shared-readonly";
        mainVm.SelectedSnmpProtocol = SnmpProtocolVersion.V3AuthPriv;
        mainVm.SnmpV3UserName = "monitor";
        mainVm.NetworkDeviceSnmpV3AuthPassword = "auth-secret";

        mainVm.NetworkDeviceCommunity.Should().Be("shared-readonly");
        mainVm.NetworkDeviceSelectedSnmpProtocol.Should().Be(SnmpProtocolVersion.V3AuthPriv);
        mainVm.NetworkDeviceSnmpV3UserName.Should().Be("monitor");
        mainVm.SnmpV3AuthPassword.Should().Be("auth-secret");
    }

    [Fact]
    public void MainViewModel_ModuleStatusPublishesActivityEntry()
    {
        using var provider = (ServiceProvider)BuildProvider();
        var mainVm = provider.GetRequiredService<MainViewModel>();

        ((DiagnosisViewModel.IHost)mainVm).NotifyStatus("Running smoke status...");

        mainVm.StatusMessage.Should().Be("Running smoke status...");
        mainVm.Activity.LastEntry.Should().NotBeNull();
        mainVm.Activity.LastEntry!.SourceModule.Should().Be(ActivitySourceModule.QuickDiagnosis);
        mainVm.Activity.LastEntry.Message.Should().Be("Running smoke status...");
    }

    [Fact]
    public void MainViewModel_ExposesDiagnosisActionStateLabels()
    {
        using var provider = (ServiceProvider)BuildProvider();
        var mainVm = provider.GetRequiredService<MainViewModel>();

        mainVm.DiagnoseButtonText.Should().Be("Diagnose This PC");
        mainVm.DiagnoseButtonIcon.Should().Be("\uE9D9");
        mainVm.PortTestButtonText.Should().Be("Run Port Test");
        mainVm.PortTestButtonIcon.Should().Be("\uE8C8");
        mainVm.LinkQualityPingButtonText.Should().Be("Run Ping Test");
        mainVm.LinkQualityPathButtonText.Should().Be("Run Gateway / DNS / Internet");
    }

    [Fact]
    public void MainViewModel_ExposesSmartPortTestPresets()
    {
        using var provider = (ServiceProvider)BuildProvider();
        var mainVm = provider.GetRequiredService<MainViewModel>();

        mainVm.PortTestPortOptions.Should().Contain(item => item.Port == 445 && item.ServiceName == "SMB");
        mainVm.PortTestPortOptions.Should().Contain(item => item.Port == 3389 && item.ServiceName == "RDP");
        mainVm.SelectedPortDescription.Should().Contain("Windows file share");

        mainVm.LastDiagnosis = new NetworkDiagnosisResult
        {
            IpConfiguration = new IpConfigurationInfo
            {
                Gateway = "192.168.1.1",
                DnsServers = ["192.168.1.10", "1.1.1.1"]
            },
            Domain = new DomainInfo { LogonServer = "\\\\dc01" },
            LastPortTest = new PortTestResult { Target = "fileserver01", Port = 445 },
            Profile = new NetworkProfile
            {
                ImportantServers =
                [
                    new DiagnosticTarget { Name = "ERP", Host = "erp01", Purpose = "Business app" }
                ]
            }
        };

        mainVm.PortTestTargetOptions.Should().Contain(item => item.Label == "Gateway" && item.Target == "192.168.1.1");
        mainVm.PortTestTargetOptions.Should().Contain(item => item.Label == "DNS 1" && item.Target == "192.168.1.10");
        mainVm.PortTestTargetOptions.Should().Contain(item => item.Label == "Logon server" && item.Target == "dc01");
        mainVm.PortTestTargetOptions.Should().Contain(item => item.Label == "Last target" && item.Target == "fileserver01");
        mainVm.PortTestTargetOptions.Should().Contain(item => item.Label == "ERP" && item.Target == "erp01");

        var gateway = mainVm.PortTestTargetOptions.Single(item => item.Label == "Gateway");
        mainVm.ApplyPortTestTargetPresetCommand.Execute(gateway);

        mainVm.TargetHost.Should().Be("192.168.1.1");
        mainVm.StatusMessage.Should().Be("Port test target set to 192.168.1.1.");
    }

    [Fact]
    public void MainViewModel_ExplainsPortTestOutcomes()
    {
        using var provider = (ServiceProvider)BuildProvider();
        var mainVm = provider.GetRequiredService<MainViewModel>();

        mainVm.LastDiagnosis = new NetworkDiagnosisResult
        {
            LastPortTest = new PortTestResult
            {
                Target = "fileserver01",
                Port = 445,
                PingSucceeded = false,
                TcpSucceeded = true
            }
        };

        mainVm.LastPortTestInterpretation.Should().Contain("TCP works");
        mainVm.LastPortTestInterpretation.Should().Contain("ICMP may be filtered");
    }

    [Fact]
    public void MainViewModel_ExposesLinkQualitySurfaceCounts()
    {
        using var provider = (ServiceProvider)BuildProvider();
        var mainVm = provider.GetRequiredService<MainViewModel>();

        mainVm.HasNoLinkQualityPingResults.Should().BeTrue();
        mainVm.HasNoLinkQualityDnsResults.Should().BeTrue();
        mainVm.HasNoLinkQualityEvidence.Should().BeTrue();
        mainVm.HasNoLinkQualityRecommendations.Should().BeTrue();
        mainVm.HasNoLinkQualityLimitations.Should().BeTrue();
        mainVm.LastLinkQualityResult.Source.Should().Be("No data");
        mainVm.LastLinkQualityResult.Summary.Should().Be("No Link Quality test has been run yet.");
        mainVm.LastDiagnosis.Should().BeNull();
        mainVm.LinkQualityPingResultCountText.Should().Be("0 ping results");
        mainVm.LinkQualityDnsResultCountText.Should().Be("0 DNS results");

        mainVm.LastDiagnosis = new NetworkDiagnosisResult
        {
            Adapter = new AdapterInfo
            {
                Name = "Ethernet 1",
                InterfaceDescription = "Intel Ethernet",
                ConnectionType = "Ethernet",
                LinkSpeed = "1 Gbps",
                LinkSpeedMbps = 1000,
                SpeedDuplex = "Full",
                SamplingStatus = "Sampled over 1 second."
            },
            Profile = new NetworkProfile { ExpectedMinimumEthernetMbps = 1000 }
        };

        mainVm.HasLinkQualityEvidence.Should().BeTrue();
        mainVm.HasLinkQualityRecommendations.Should().BeTrue();
        mainVm.HasLinkQualityLimitations.Should().BeTrue();
        mainVm.LinkQualityEvidenceCountText.Should().Be("5 evidence items");
        mainVm.LinkQualitySnapshotTimeText.Should().StartWith("Snapshot:");
    }

    [Fact]
    public void MainViewModel_ExposesLinkQualityTargetAndSamplePresets()
    {
        using var provider = (ServiceProvider)BuildProvider();
        var mainVm = provider.GetRequiredService<MainViewModel>();

        mainVm.LinkQualityTargetOptions.Should().Contain(item => item.Label == "Internet" && item.Target == "1.1.1.1");
        mainVm.LinkQualityTargetOptions.Should().Contain(item => item.Label == "Custom" && item.IsCustom);
        mainVm.LinkQualitySampleOptions.Select(item => item.Samples).Should().Equal(5, 10, 20, 50);

        mainVm.LastDiagnosis = new NetworkDiagnosisResult
        {
            IpConfiguration = new IpConfigurationInfo
            {
                Gateway = "192.168.1.1",
                DnsServers = ["192.168.1.10"]
            }
        };

        mainVm.LinkQualityTargetOptions.Should().Contain(item => item.Label == "Gateway" && item.Target == "192.168.1.1");
        mainVm.LinkQualityTargetOptions.Should().Contain(item => item.Label == "DNS" && item.Target == "192.168.1.10");

        var dns = mainVm.LinkQualityTargetOptions.Single(item => item.Label == "DNS");
        mainVm.ApplyLinkQualityTargetPresetCommand.Execute(dns);
        mainVm.LinkQualityPingTarget.Should().Be("192.168.1.10");
        mainVm.StatusMessage.Should().Be("Link Quality target set to 192.168.1.10.");

        var sample20 = mainVm.LinkQualitySampleOptions.Single(item => item.Samples == 20);
        mainVm.ApplyLinkQualitySamplePresetCommand.Execute(sample20);
        mainVm.LinkQualityPingSamples.Should().Be(20);
        mainVm.StatusMessage.Should().Be("Link Quality samples set to 20.");

        var custom = mainVm.LinkQualityTargetOptions.Single(item => item.Label == "Custom");
        mainVm.ApplyLinkQualityTargetPresetCommand.Execute(custom);
        mainVm.LinkQualityPingTarget.Should().BeEmpty();
        mainVm.StatusMessage.Should().Be("Link Quality target cleared for custom input.");
    }

    [Fact]
    public void MainViewModel_ExposesLinkQualityTicketSummary()
    {
        using var provider = (ServiceProvider)BuildProvider();
        var mainVm = provider.GetRequiredService<MainViewModel>();

        mainVm.CopyLinkQualitySummaryCommand.CanExecute(null).Should().BeFalse();
        mainVm.LinkQualityTicketSummaryText.Should().Contain("ISG Desk - Link Quality Summary");

        mainVm.LastDiagnosis = new NetworkDiagnosisResult
        {
            Adapter = new AdapterInfo
            {
                Name = "Ethernet 1",
                InterfaceDescription = "Intel Ethernet",
                ConnectionType = "Ethernet",
                LinkSpeed = "1 Gbps",
                LinkSpeedMbps = 1000,
                SpeedDuplex = "Full Duplex",
                Errors = 2,
                Discards = 1,
                SamplingStatus = "Sampled 5 seconds."
            }
        };

        mainVm.CopyLinkQualitySummaryCommand.CanExecute(null).Should().BeTrue();
        mainVm.LinkQualityTicketSummaryText.Should().Contain("Status        : Warning");
        mainVm.LinkQualityTicketSummaryText.Should().Contain("Adapter       : Ethernet 1 (Ethernet)");
        mainVm.LinkQualityTicketSummaryText.Should().Contain("EVIDENCE");
        mainVm.LinkQualityTicketSummaryText.Should().Contain("RECOMMENDATIONS");
    }

    [Fact]
    public async Task DiagnosisViewModel_RejectsInvalidTcpPortBeforeCollectorRuns()
    {
        using var provider = (ServiceProvider)BuildProvider();
        var mainVm = provider.GetRequiredService<MainViewModel>();
        mainVm.TargetHost = "localhost";
        mainVm.TargetPort = 0;

        await mainVm.Diagnosis.RunPortTestAsync();

        mainVm.StatusMessage.Should().Be("Enter a TCP port between 1 and 65535.");
        mainVm.IsPortTestRunning.Should().BeFalse();
        mainVm.LastDiagnosis.Should().BeNull();
    }

    [Fact]
    public void DiagnosisViewModel_PortTestValidationState_TracksTargetAndPort()
    {
        using var provider = (ServiceProvider)BuildProvider();
        var mainVm = provider.GetRequiredService<MainViewModel>();

        mainVm.CanRunPortTest.Should().BeTrue();
        mainVm.PortTestInputSeverity.Should().Be("OK");
        mainVm.PortTestInputStatusText.Should().Contain("localhost:445");
        mainVm.RunPortTestCommand.CanExecute(null).Should().BeTrue();

        mainVm.TargetPort = 0;
        mainVm.CanRunPortTest.Should().BeFalse();
        mainVm.PortTestInputSeverity.Should().Be("Warning");
        mainVm.PortTestInputStatusText.Should().Be("Enter a TCP port between 1 and 65535.");
        mainVm.RunPortTestCommand.CanExecute(null).Should().BeFalse();

        mainVm.TargetPort = 443;
        mainVm.TargetHost = "https://example.com/path";
        mainVm.CanRunPortTest.Should().BeFalse();
        mainVm.PortTestInputStatusText.Should().Contain("without protocol");

        mainVm.TargetHost = "example.com";
        mainVm.CanRunPortTest.Should().BeTrue();
        mainVm.PortTestInputSeverity.Should().Be("OK");
        mainVm.PortTestInputStatusText.Should().Contain("example.com:443");
        mainVm.RunPortTestCommand.CanExecute(null).Should().BeTrue();
    }

    [Fact]
    public void MainViewModel_ExposesDiagnosisSurfaceEmptyAndPopulatedState()
    {
        using var provider = (ServiceProvider)BuildProvider();
        var mainVm = provider.GetRequiredService<MainViewModel>();

        mainVm.HasNoDiagnosis.Should().BeTrue();
        mainVm.HasNoDiagnosticSteps.Should().BeTrue();
        mainVm.HasNoPortTest.Should().BeTrue();
        mainVm.DiagnosisNextActionTitle.Should().Be("Ready to collect baseline");
        mainVm.DiagnosisProblemText.Should().Be("No Quick Diagnosis result yet.");
        mainVm.DiagnosisRecommendedActionText.Should().Contain("Run Diagnose This PC");

        mainVm.LastDiagnosis = new NetworkDiagnosisResult
        {
            LastPortTest = new PortTestResult { Target = "server01", Port = 445 },
            Steps =
            [
                new DiagnosisStepResult
                {
                    Name = "Gateway",
                    Status = "OK",
                    Evidence = ["Gateway reachable."]
                }
            ],
            Verdict = new DiagnosisVerdict
            {
                Title = "DNS server latency warning",
                Severity = "Warning",
                Evidence = ["Gateway reachable."],
                Limitations = ["Traceroute skipped."],
                Recommendations = ["Continue with app-specific tests."]
            },
            CollectorWarnings = ["Wi-Fi collector unavailable."],
            HealthScore = new HealthScoreResult
            {
                Penalties = ["DNS latency warning."]
            }
        };

        mainVm.HasDiagnosis.Should().BeTrue();
        mainVm.HasDiagnosticSteps.Should().BeTrue();
        mainVm.HasPortTest.Should().BeTrue();
        mainVm.HasVerdictEvidence.Should().BeTrue();
        mainVm.HasVerdictLimitations.Should().BeTrue();
        mainVm.HasVerdictRecommendations.Should().BeTrue();
        mainVm.HasCollectorWarnings.Should().BeTrue();
        mainVm.HasHealthPenalties.Should().BeTrue();
        mainVm.DiagnosisNextActionTitle.Should().Be("Recommended next check");
        mainVm.DiagnosisProblemText.Should().Be("DNS server latency warning");
        mainVm.DiagnosisEvidenceText.Should().Be("Gateway reachable.");
        mainVm.DiagnosisRecommendedActionText.Should().Be("Continue with app-specific tests.");
    }

    [Fact]
    public void MainViewModel_InPlaceDiagnosisMutationRefreshesSurfaceState()
    {
        using var provider = (ServiceProvider)BuildProvider();
        var mainVm = provider.GetRequiredService<MainViewModel>();
        mainVm.LastDiagnosis = new NetworkDiagnosisResult();
        var changed = new List<string?>();
        mainVm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        mainVm.LastDiagnosis.LastPortTest = new PortTestResult { Target = "server01", Port = 445 };
        ((DiagnosisViewModel.IHost)mainVm).NotifyDiagnosisChanged();

        mainVm.HasPortTest.Should().BeTrue();
        mainVm.HasNoPortTest.Should().BeFalse();
        changed.Should().Contain(nameof(MainViewModel.HasPortTest));
        changed.Should().Contain(nameof(MainViewModel.HasNoPortTest));
    }

    [Fact]
    public void MainViewModel_BusyStateTracksOverlappingModuleOperations()
    {
        using var provider = (ServiceProvider)BuildProvider();
        var mainVm = provider.GetRequiredService<MainViewModel>();

        ((DiagnosisViewModel.IHost)mainVm).NotifyBusy(true);
        ((PrintersViewModel.IHost)mainVm).NotifyBusy(true);

        mainVm.IsBusy.Should().BeTrue();

        ((DiagnosisViewModel.IHost)mainVm).NotifyBusy(false);
        mainVm.IsBusy.Should().BeTrue();

        ((PrintersViewModel.IHost)mainVm).NotifyBusy(false);
        mainVm.IsBusy.Should().BeFalse();

        ((PrintersViewModel.IHost)mainVm).NotifyBusy(false);
        mainVm.IsBusy.Should().BeFalse();
    }

    [Fact]
    public void Singleton_ILoggingService_AndConcreteLoggingService_AreSameInstance()
    {
        using var provider = (ServiceProvider)BuildProvider();

        var iface = provider.GetRequiredService<ILoggingService>();
        var concrete = provider.GetRequiredService<LoggingService>();
        concrete.Should().BeSameAs(iface);
    }

    [Fact]
    public void Singleton_IPowerShellRunner_AndConcrete_AreSameInstance()
    {
        using var provider = (ServiceProvider)BuildProvider();

        var iface = provider.GetRequiredService<IPowerShellRunner>();
        var concrete = provider.GetRequiredService<PowerShellRunner>();
        concrete.Should().BeSameAs(iface);
    }

    [Fact]
    public void MainViewModel_ResolvedTwice_ReturnsSameInstance_BecauseSingleton()
    {
        using var provider = (ServiceProvider)BuildProvider();

        var first = provider.GetRequiredService<MainViewModel>();
        var second = provider.GetRequiredService<MainViewModel>();
        second.Should().BeSameAs(first);
    }
}
