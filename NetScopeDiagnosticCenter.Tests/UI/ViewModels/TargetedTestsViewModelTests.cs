using NetScopeDiagnosticCenter.Collectors;
using NetScopeDiagnosticCenter.Core;
using NetScopeDiagnosticCenter.Core.Models;
using NetScopeDiagnosticCenter.Infrastructure;
using NetScopeDiagnosticCenter.UI;
using NetScopeDiagnosticCenter.UI.ViewModels;

namespace NetScopeDiagnosticCenter.Tests.UI.ViewModels;

public sealed class TargetedTestsViewModelTests
{
    private sealed class FakeHost : TargetedTestsViewModel.IHost
    {
        public ScenarioDiagnosisResult? LastTargetedScenario { get; set; }
        public NetworkProfile NetworkProfile { get; set; } = new();
        public string CurrentPage { get; set; } = string.Empty;
        public List<string> StatusMessages { get; } = [];
        public List<bool> BusyTransitions { get; } = [];

        public void NotifyStatus(string message) => StatusMessages.Add(message);
        public void NotifyBusy(bool busy) => BusyTransitions.Add(busy);
        public void RememberShareTarget(string target) { }
        public void RememberServiceTarget(string target) { }
        public void SetDomainControllerOverride(string value) { }
    }

    private sealed class NullLogger : ILoggingService
    {
        public void Debug(string message) { }
        public void Info(string message) { }
        public void Warning(string message) { }
        public void Error(string message, Exception? exception = null) { }
    }

    private sealed class FakeShareDiscoveryCollector : TargetShareDiscoveryCollector
    {
        private readonly ShareTargetDiscoveryResult _result;

        public FakeShareDiscoveryCollector(ShareTargetDiscoveryResult result)
        {
            _result = result;
        }

        public int ScanCount { get; private set; }

        public override Task<ShareTargetDiscoveryResult> ScanLocalSubnetAsync(CancellationToken cancellationToken = default)
        {
            ScanCount++;
            return Task.FromResult(_result);
        }
    }

    private sealed class FakeDomainDiscoveryCollector : DomainControllerDiscoveryCollector
    {
        private readonly DomainControllerDiscoveryResult _result;

        public FakeDomainDiscoveryCollector(DomainControllerDiscoveryResult result)
            : base(new PowerShellRunner(new NullLogger()))
        {
            _result = result;
        }

        public IReadOnlyList<string> LastDomainHints { get; private set; } = [];
        public int DiscoverCount { get; private set; }

        public override Task<DomainControllerDiscoveryResult> DiscoverAsync(
            IEnumerable<string> domainNames,
            CancellationToken cancellationToken = default)
        {
            DiscoverCount++;
            LastDomainHints = domainNames.ToList();
            return Task.FromResult(_result);
        }
    }

    private sealed class FakeServiceDiscoveryCollector : TargetServiceDiscoveryCollector
    {
        private readonly ServiceTargetDiscoveryResult _result;

        public FakeServiceDiscoveryCollector(ServiceTargetDiscoveryResult result)
        {
            _result = result;
        }

        public IReadOnlyList<int> LastPorts { get; private set; } = [];
        public int ScanCount { get; private set; }

        public override Task<ServiceTargetDiscoveryResult> ScanLocalSubnetAsync(
            IReadOnlyList<int> ports,
            CancellationToken cancellationToken = default)
        {
            ScanCount++;
            LastPorts = ports.ToList();
            return Task.FromResult(_result);
        }
    }

    [Fact]
    public async Task RunTestAsync_ServiceAccessWithMixedInvalidPorts_ShowsValidationWithoutRunningBaseline()
    {
        var (vm, host) = Build();
        vm.ServiceTestTarget = "server-01";
        vm.ServiceTestPorts = "443, 70000";

        await vm.RunTestAsync(ScenarioWorkflow.ServiceAccess);

        host.BusyTransitions.Should().BeEmpty();
        host.CurrentPage.Should().Be("TargetedTests");
        host.LastTargetedScenario.Should().NotBeNull();
        host.LastTargetedScenario!.Title.Should().Contain("Invalid TCP port '70000'");
        host.StatusMessages.Should().ContainSingle(message => message.Contains("Invalid TCP port '70000'"));
    }

    [Fact]
    public async Task RunTestAsync_ServiceAccessWithTooManyPorts_ShowsValidationWithoutRunningBaseline()
    {
        var (vm, host) = Build();
        vm.ServiceTestTarget = "server-01";
        vm.ServiceTestPorts = string.Join(",", Enumerable.Range(1, DiagnosticConstants.MaxTargetedTestPorts + 1));

        await vm.RunTestAsync(ScenarioWorkflow.ServiceAccess);

        host.BusyTransitions.Should().BeEmpty();
        host.LastTargetedScenario?.Title.Should().Contain($"{DiagnosticConstants.MaxTargetedTestPorts} or fewer TCP ports");
    }

    [Fact]
    public async Task RunTestAsync_ServiceAccessWithDelimiterOnlyInput_ShowsValidationWithoutRunningBaseline()
    {
        var (vm, host) = Build();
        vm.ServiceTestTarget = ",;;";
        vm.ServiceTestPorts = "443";

        await vm.RunTestAsync(ScenarioWorkflow.ServiceAccess);

        host.BusyTransitions.Should().BeEmpty();
        host.LastTargetedScenario?.Title.Should().Be("Enter at least one target.");
    }

    [Fact]
    public async Task RunTestAsync_ServiceAccessWithTooManyTargets_ShowsValidationWithoutRunningBaseline()
    {
        var (vm, host) = Build();
        vm.ServiceTestTarget = string.Join(",", Enumerable.Range(1, DiagnosticConstants.MaxTargetedTestTargets + 1)
            .Select(index => $"app-{index:00}"));
        vm.ServiceTestPorts = "443";

        await vm.RunTestAsync(ScenarioWorkflow.ServiceAccess);

        host.BusyTransitions.Should().BeEmpty();
        host.LastTargetedScenario?.Title.Should().Contain($"{DiagnosticConstants.MaxTargetedTestTargets} or fewer targets");
    }

    [Fact]
    public async Task RunTestAsync_InternalShareWithDelimiterOnlyInput_ShowsValidationWithoutRunningBaseline()
    {
        var (vm, host) = Build();
        vm.ShareTestTarget = ",;;";

        await vm.RunTestAsync(ScenarioWorkflow.InternalShare);

        host.BusyTransitions.Should().BeEmpty();
        host.LastTargetedScenario?.Title.Should().Be("Enter at least one target.");
    }

    [Fact]
    public async Task RunTestAsync_InternalShareWithTooManyTargets_ShowsValidationWithoutRunningBaseline()
    {
        var (vm, host) = Build();
        vm.ShareTestTarget = string.Join(",", Enumerable.Range(1, DiagnosticConstants.MaxTargetedTestTargets + 1)
            .Select(index => $"server-{index:00}"));

        await vm.RunTestAsync(ScenarioWorkflow.InternalShare);

        host.BusyTransitions.Should().BeEmpty();
        host.LastTargetedScenario?.Title.Should().Contain($"{DiagnosticConstants.MaxTargetedTestTargets} or fewer targets");
    }

    [Fact]
    public void DetectShareTargetCommand_UsesProfileFileServersWithoutNetworkScan()
    {
        var host = new FakeHost
        {
            NetworkProfile = new NetworkProfile
            {
                FileServers =
                [
                    new DiagnosticTarget
                    {
                        Host = "filesrv01",
                        SharePath = @"\\filesrv01\support",
                        Purpose = "File server"
                    }
                ]
            }
        };
        var (vm, _) = Build(host: host);

        vm.DetectShareTargetCommand.Execute(null);

        vm.ShareTestTarget.Should().Be(@"\\filesrv01\support");
        host.StatusMessages.Should().ContainSingle(message => message.Contains("No network scan was run"));
    }

    [Fact]
    public async Task ScanShareTargetsCommand_FillsSmbScanCandidates()
    {
        var collector = new FakeShareDiscoveryCollector(new ShareTargetDiscoveryResult
        {
            Source = "192.168.1.0/24",
            Verdict = "Found 2 host(s) with SMB TCP 445 open.",
            Severity = "OK",
            Candidates =
            [
                new ShareTargetCandidate { Target = "filesrv01", Source = "SMB scan", Confidence = "High" },
                new ShareTargetCandidate { Target = "192.168.1.22", Source = "SMB scan", Confidence = "Medium" }
            ]
        });
        var (vm, host) = Build(collector);

        await ((AsyncRelayCommand)vm.ScanShareTargetsCommand).ExecuteAsync(null);

        collector.ScanCount.Should().Be(1);
        vm.ShareTestTarget.Should().Be("filesrv01, 192.168.1.22");
        vm.LastShareDiscoveryResult.Should().NotBeNull();
        vm.HasShareDiscoveryResult.Should().BeTrue();
        vm.HasShareDiscoveryCandidates.Should().BeTrue();
        vm.ShareDiscoveryStatusText.Should().Contain("Found 2");
        vm.ShareTargetCountText.Should().Be("2 target(s) queued.");
        vm.IsShareDiscoveryRunning.Should().BeFalse();
        host.BusyTransitions.Should().ContainInOrder(true, false);
        host.StatusMessages.Should().Contain(message => message.Contains("SMB discovery complete"));
    }

    [Fact]
    public void ShareInputValidationState_TracksManualTargets()
    {
        var (vm, _) = Build();
        vm.SelectedTestKey = ScenarioWorkflow.InternalShare;

        vm.ShareInputSeverity.Should().Be("Warning");
        vm.ShareInputStatusText.Should().Contain("Enter a server or share target");
        vm.ShareTargetCountText.Should().Be("No manual target entered.");

        vm.ShareTestTarget = @"\\filesrv01\support, 192.168.1.22";

        vm.ShareInputSeverity.Should().Be("OK");
        vm.ShareInputStatusText.Should().Contain("valid");
        vm.ShareTargetCountText.Should().Be("2 target(s) queued.");
    }

    [Fact]
    public void DetectDomainTargetCommand_UsesProfileDomainControllersWithoutDnsSrv()
    {
        var host = new FakeHost
        {
            NetworkProfile = new NetworkProfile
            {
                DomainName = "contoso.local",
                DomainControllers =
                [
                    new DiagnosticTarget { Host = "dc01.contoso.local", Purpose = "Domain controller" }
                ]
            }
        };
        var (vm, _) = Build(host: host);

        vm.DetectDomainTargetCommand.Execute(null);

        vm.DomainControllerOverride.Should().Be("dc01.contoso.local");
        host.StatusMessages.Should().ContainSingle(message => message.Contains("No DNS SRV discovery was run"));
    }

    [Fact]
    public async Task DiscoverDomainControllersCommand_FillsDnsSrvCandidates()
    {
        var collector = new FakeDomainDiscoveryCollector(new DomainControllerDiscoveryResult
        {
            Verdict = "Found 2 domain controller candidate(s) from DNS SRV.",
            Severity = "OK",
            Candidates =
            [
                new DomainControllerCandidate { Host = "dc01.contoso.local", DomainName = "contoso.local", Source = "DNS SRV", Confidence = "High" },
                new DomainControllerCandidate { Host = "dc02.contoso.local", DomainName = "contoso.local", Source = "DNS SRV", Confidence = "High" }
            ]
        });
        var host = new FakeHost
        {
            NetworkProfile = new NetworkProfile { DomainName = "contoso.local" }
        };
        var (vm, _) = Build(domainDiscoveryCollector: collector, host: host);

        await ((AsyncRelayCommand)vm.DiscoverDomainControllersCommand).ExecuteAsync(null);

        collector.DiscoverCount.Should().Be(1);
        collector.LastDomainHints.Should().ContainSingle("contoso.local");
        vm.DomainControllerOverride.Should().Be("dc01.contoso.local, dc02.contoso.local");
        vm.LastDomainDiscoveryResult.Should().NotBeNull();
        vm.HasDomainDiscoveryResult.Should().BeTrue();
        vm.HasDomainDiscoveryCandidates.Should().BeTrue();
        vm.DomainDiscoveryStatusText.Should().Contain("Found 2");
        vm.DomainControllerCountText.Should().Be("2 domain controller target(s) queued.");
        vm.IsDomainDiscoveryRunning.Should().BeFalse();
        host.BusyTransitions.Should().ContainInOrder(true, false);
        host.StatusMessages.Should().Contain(message => message.Contains("DC discovery complete"));
    }

    [Fact]
    public void DomainInputValidationState_TracksOverrideTargets()
    {
        var (vm, _) = Build();
        vm.SelectedTestKey = ScenarioWorkflow.DnsDomain;

        vm.DomainInputSeverity.Should().Be("OK");
        vm.DomainInputStatusText.Should().Contain("detected/profile");
        vm.DomainControllerCountText.Should().Be("Using detected/profile DC targets.");

        vm.DomainControllerOverride = "https://dc01.contoso.local/path";

        vm.DomainInputSeverity.Should().Be("Warning");
        vm.DomainInputStatusText.Should().Contain("without protocol");
        vm.RunSelectedTestCommand.CanExecute(null).Should().BeFalse();

        vm.DomainControllerOverride = "dc01.contoso.local, dc02.contoso.local";

        vm.DomainInputSeverity.Should().Be("OK");
        vm.DomainInputStatusText.Should().Contain("valid");
        vm.DomainControllerCountText.Should().Be("2 domain controller target(s) queued.");
        vm.RunSelectedTestCommand.CanExecute(null).Should().BeTrue();
    }

    [Fact]
    public void DetectServiceTargetCommand_UsesProfileImportantServersWithoutPortScan()
    {
        var host = new FakeHost
        {
            NetworkProfile = new NetworkProfile
            {
                ImportantServers =
                [
                    new DiagnosticTarget { Host = "erp01", Purpose = "Business app" },
                    new DiagnosticTarget { Host = "api01", Purpose = "API" }
                ]
            }
        };
        var (vm, _) = Build(host: host);

        vm.DetectServiceTargetCommand.Execute(null);

        vm.ServiceTestTarget.Should().StartWith("erp01, api01");
        host.StatusMessages.Should().ContainSingle(message => message.Contains("No port scan was run"));
    }

    [Fact]
    public async Task ScanServiceTargetsCommand_FillsPortScanCandidates()
    {
        var collector = new FakeServiceDiscoveryCollector(new ServiceTargetDiscoveryResult
        {
            Source = "192.168.1.0/24",
            Verdict = "Found 2 host(s) with requested service ports open.",
            Severity = "OK",
            ScannedHosts = 24,
            Ports = [443, 8443],
            Candidates =
            [
                new ServiceTargetCandidate { Host = "erp01", Source = "Service scan", Confidence = "High", OpenPorts = [443] },
                new ServiceTargetCandidate { Host = "192.168.1.44", Source = "Service scan", Confidence = "Medium", OpenPorts = [443, 8443] }
            ]
        });
        var (vm, host) = Build(serviceDiscoveryCollector: collector);
        vm.ServiceTestPorts = "443, 8443";

        await ((AsyncRelayCommand)vm.ScanServiceTargetsCommand).ExecuteAsync(null);

        collector.ScanCount.Should().Be(1);
        collector.LastPorts.Should().Equal(443, 8443);
        vm.ServiceTestTarget.Should().Contain("erp01");
        vm.ServiceTestTarget.Should().Contain("192.168.1.44");
        vm.LastServiceDiscoveryResult.Should().NotBeNull();
        vm.HasServiceDiscoveryResult.Should().BeTrue();
        vm.HasServiceDiscoveryCandidates.Should().BeTrue();
        vm.ServiceDiscoveryStatusText.Should().Contain("Found 2");
        vm.ServiceDiscoveryScanScopeText.Should().Contain("443");
        vm.LastServiceDiscoveryResult!.Candidates[1].OpenPortsText.Should().Be("443, 8443");
        vm.IsServiceDiscoveryRunning.Should().BeFalse();
        host.BusyTransitions.Should().ContainInOrder(true, false);
        host.StatusMessages.Should().Contain(message => message.Contains("Service discovery complete"));
    }

    [Fact]
    public async Task ScanServiceTargetsCommand_InvalidPorts_DoesNotScan()
    {
        var collector = new FakeServiceDiscoveryCollector(new ServiceTargetDiscoveryResult());
        var (vm, host) = Build(serviceDiscoveryCollector: collector);
        vm.ServiceTestPorts = "443, 70000";

        await ((AsyncRelayCommand)vm.ScanServiceTargetsCommand).ExecuteAsync(null);

        collector.ScanCount.Should().Be(0);
        host.BusyTransitions.Should().BeEmpty();
        host.StatusMessages.Should().BeEmpty();
        vm.ScanServiceTargetsCommand.CanExecute(null).Should().BeFalse();
        vm.ServicePortInputStatusText.Should().Contain("Invalid TCP port '70000'");
    }

    [Fact]
    public void InputValidationState_ServiceAccessTracksTargetAndPorts()
    {
        var (vm, _) = Build();
        vm.SelectedTestKey = ScenarioWorkflow.ServiceAccess;

        vm.CanRunSelectedTest.Should().BeFalse();
        vm.SelectedTestInputStatusText.Should().Contain("Enter a service target");

        vm.ServiceTestTarget = "app-01";
        vm.ServiceTestPorts = "443, 70000";

        vm.CanRunSelectedTest.Should().BeFalse();
        vm.CanScanServiceTargets.Should().BeFalse();
        vm.ServicePortInputStatusText.Should().Contain("70000");
        vm.ScanServiceTargetsCommand.CanExecute(null).Should().BeFalse();

        vm.ServiceTestPorts = "443, 8443";

        vm.CanRunSelectedTest.Should().BeTrue();
        vm.CanScanServiceTargets.Should().BeTrue();
        vm.SelectedTestInputSeverity.Should().Be("OK");
        vm.SelectedTestInputStatusText.Should().Contain("443");
        vm.ScanServiceTargetsCommand.CanExecute(null).Should().BeTrue();
    }

    [Fact]
    public void ServicePortSelector_DefaultCoreAndManualStayInSync()
    {
        var (vm, host) = Build();
        var options = vm.ServicePortGroups.SelectMany(group => group.Ports).ToList();
        var https = options.Single(option => option.Port == 443);
        var smb = options.Single(option => option.Port == 445);

        https.IsSelected.Should().BeTrue();
        smb.IsSelected.Should().BeTrue();
        vm.SelectedServicePortCountText.Should().Be("2/12 TCP port(s) selected.");

        options.Single(option => option.Port == 3389).IsSelected = true;

        vm.ServiceTestPorts.Should().Contain("3389");
        vm.SelectedServicePortCountText.Should().Be("3/12 TCP port(s) selected.");

        vm.ClearServicePortsCommand.Execute(null);

        vm.ServiceTestPorts.Should().BeEmpty();
        options.Should().OnlyContain(option => !option.IsSelected);
        vm.CanScanServiceTargets.Should().BeFalse();

        vm.SelectCoreServicePortsCommand.Execute(null);

        vm.ServiceTestPorts
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Should()
            .HaveCount(DiagnosticConstants.MaxTargetedTestPorts);
        options.Where(option => option.IsCore).Should().OnlyContain(option => option.IsSelected);
        vm.CanScanServiceTargets.Should().BeTrue();

        var nonCore = options.First(option => !option.IsCore);
        nonCore.IsSelected = true;

        nonCore.IsSelected.Should().BeFalse();
        host.StatusMessages.Should().Contain(message => message.Contains($"{DiagnosticConstants.MaxTargetedTestPorts} or fewer TCP ports"));
    }

    [Fact]
    public void BuildTargetedTestTicketSummary_IncludesScenarioAndTargetRows()
    {
        var (vm, host) = Build();
        host.LastTargetedScenario = new ScenarioDiagnosisResult
        {
            WorkflowName = "Service Access",
            Title = "Service port reachable.",
            Severity = "OK",
            AffectedLayer = "Application",
            OwnerSuggestion = "Application Team",
            Confidence = "High",
            InputSummary = "Service target: app-01; ports: 443",
            BaselineSummary = "Gateway and DNS OK.",
            Evidence = ["TCP 443 open."],
            NextChecks = ["Validate application login."],
            Limitations = ["No authentication was attempted."],
            TargetResults =
            [
                new TargetProbeResult
                {
                    Target = new DiagnosticTarget { Host = "app-01" },
                    DnsStatus = "OK",
                    PingStatus = "OK",
                    AverageLatencyMs = 12.4,
                    Ports = [new PortProbeResult { Port = 443, TcpSucceeded = true }],
                    Verdict = "Reachable",
                    OwnerSuggestion = "Application Team"
                }
            ]
        };
        vm.NotifyDiagnosisChanged();

        vm.CopyTargetedTestSummaryCommand.CanExecute(null).Should().BeTrue();
        var summary = vm.BuildTargetedTestTicketSummary(new DateTimeOffset(2026, 6, 8, 13, 30, 0, TimeSpan.Zero));

        summary.Should().Contain("Targeted Test Summary");
        summary.Should().Contain("Service Access");
        summary.Should().Contain("app-01");
        summary.Should().Contain("TCP 443 open");
    }

    private static (TargetedTestsViewModel Vm, FakeHost Host) Build(
        TargetShareDiscoveryCollector? shareDiscoveryCollector = null,
        TargetServiceDiscoveryCollector? serviceDiscoveryCollector = null,
        DomainControllerDiscoveryCollector? domainDiscoveryCollector = null,
        FakeHost? host = null)
    {
        host ??= new FakeHost();
        var vm = new TargetedTestsViewModel(
            new ScenarioEngine(null!, null!),
            shareDiscoveryCollector ?? new TargetShareDiscoveryCollector(),
            serviceDiscoveryCollector ?? new TargetServiceDiscoveryCollector(),
            domainDiscoveryCollector ?? new DomainControllerDiscoveryCollector(new PowerShellRunner(new NullLogger())),
            new NullLogger(),
            host);
        return (vm, host);
    }
}
