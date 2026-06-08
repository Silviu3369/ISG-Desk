using NetScopeDiagnosticCenter.Collectors;
using NetScopeDiagnosticCenter.Core.Models;
using NetScopeDiagnosticCenter.Infrastructure;
using NetScopeDiagnosticCenter.UI;
using NetScopeDiagnosticCenter.UI.ViewModels;

namespace NetScopeDiagnosticCenter.Tests.UI.ViewModels;

public class TechnicianHomeViewModelTests
{
    private sealed class FakeHost : TechnicianHomeViewModel.IHost
    {
        public NetworkDiagnosisResult? LastDiagnosis { get; set; }
        public string LastReportPath { get; set; } = string.Empty;
    }

    private sealed class FakeCollector : ISystemOverviewCollector
    {
        private readonly Func<CancellationToken, Task<SystemOverview>> _getAsync;

        public FakeCollector(Func<CancellationToken, Task<SystemOverview>> getAsync)
        {
            _getAsync = getAsync;
        }

        public Task<SystemOverview> GetAsync(CancellationToken cancellationToken = default) =>
            _getAsync(cancellationToken);
    }

    [Fact]
    public async Task RefreshState_ChangesWhileSnapshotIsLoading_AndPublishesActivity()
    {
        var gate = new TaskCompletionSource<SystemOverview>(TaskCreationOptions.RunContinuationsAsynchronously);
        var feed = new ActivityFeedService();
        var vm = Build(
            new FakeHost(),
            new FakeCollector(_ => gate.Task),
            feed);

        var loadTask = vm.EnsureLoadedAsync();
        await WaitUntilAsync(() => vm.IsLoading);

        vm.RefreshButtonText.Should().Be("Refreshing...");
        vm.RefreshButtonIcon.Should().Be("\uE895");
        vm.RefreshCommand.CanExecute(null).Should().BeFalse();
        vm.CopySystemSummaryCommand.CanExecute(null).Should().BeFalse();
        feed.Entries.Last().SourceModule.Should().Be(ActivitySourceModule.TechnicianHome);
        feed.Entries.Last().Status.Should().Be(ActivityStatus.Running);

        gate.SetResult(MakeHealthyOverview());
        await loadTask;

        vm.IsLoading.Should().BeFalse();
        vm.RefreshButtonText.Should().Be("Refresh");
        vm.RefreshCommand.CanExecute(null).Should().BeTrue();
        vm.CopySystemSummaryCommand.CanExecute(null).Should().BeTrue();
        feed.Entries.Last().Status.Should().Be(ActivityStatus.Success);
        feed.Entries.Last().Message.Should().Contain("System overview refreshed");
    }

    [Fact]
    public async Task SummaryProperties_ProjectLoadedOverviewIntoStatusTiles()
    {
        var vm = Build(
            new FakeHost(),
            new FakeCollector(_ => Task.FromResult(MakeHealthyOverview())),
            new ActivityFeedService());

        await vm.EnsureLoadedAsync();

        vm.PrivilegeSeverity.Should().Be("OK");
        vm.PrivilegeSummary.Should().Contain("Administrator");
        vm.NetworkPostureSeverity.Should().Be("OK");
        vm.NetworkSummary.Should().Contain("Ethernet");
        vm.NetworkSummary.Should().Contain("192.168.1.25");
        vm.SecurityPostureSeverity.Should().Be("OK");
        vm.SecuritySummary.Should().Be("Tracked controls look healthy");
    }

    [Fact]
    public void DiagnosisSummary_UpdatesWhenHostDiagnosisChanges()
    {
        var host = new FakeHost();
        var vm = Build(
            host,
            new FakeCollector(_ => Task.FromResult(MakeHealthyOverview())),
            new ActivityFeedService());

        vm.DiagnosisSummary.Should().Be("No baseline yet");

        host.LastDiagnosis = new NetworkDiagnosisResult
        {
            Verdict = new DiagnosisVerdict
            {
                Title = "DNS failure",
                Severity = "Warning",
                AffectedLayer = "DNS",
                Confidence = "High"
            },
            HealthScore = new HealthScoreResult
            {
                Score = 74,
                Status = "Needs attention"
            }
        };

        vm.NotifyDiagnosisChanged();

        vm.DiagnosisSummary.Should().Be("74/100 - DNS failure");
        vm.DiagnosisSeverity.Should().Be("Warning");
        vm.RecommendedAction.Should().Contain("Investigate");
    }

    [Fact]
    public void CopySummaryCommand_PublishesActivityEntry()
    {
        var feed = new ActivityFeedService();
        var vm = Build(
            new FakeHost(),
            new FakeCollector(_ => Task.FromResult(MakeHealthyOverview())),
            feed);

        vm.CopySystemSummaryCommand.Execute(null);

        feed.Entries.Last().SourceModule.Should().Be(ActivitySourceModule.TechnicianHome);
        feed.Entries.Last().StepName.Should().Be("Technician Home");
        feed.Entries.Last().Message.Should().MatchRegex("System summary copied|Copy failed");
    }

    [Fact]
    public void WindowsToolCommands_AreAvailable()
    {
        var vm = Build(
            new FakeHost(),
            new FakeCollector(_ => Task.FromResult(MakeHealthyOverview())),
            new ActivityFeedService());

        vm.OpenDeviceManagerCommand.Should().NotBeNull();
        vm.OpenNetworkConnectionsCommand.Should().NotBeNull();
        vm.OpenEventViewerCommand.Should().NotBeNull();
        vm.OpenWindowsSecurityCommand.Should().NotBeNull();
        vm.OpenSystemAboutCommand.Should().NotBeNull();
    }

    private static TechnicianHomeViewModel Build(
        FakeHost host,
        ISystemOverviewCollector collector,
        ActivityFeedService feed) =>
        new(host, collector, feed);

    private static SystemOverview MakeHealthyOverview() => new()
    {
        CapturedAt = new DateTimeOffset(2026, 5, 25, 10, 0, 0, TimeSpan.Zero),
        Device = new SystemOverviewDevice
        {
            DeviceName = "TECH-PC",
            Manufacturer = "Contoso",
            Model = "DeskPro",
            SerialNumber = "ABC123",
            WindowsEdition = "Windows 11 Pro",
            WindowsVersion = "23H2",
            OsBuild = "22631",
            Architecture = "X64",
            Uptime = "1d 2h 3m",
            LastBootTime = "2026-05-24 08:00:00"
        },
        Hardware = new SystemOverviewHardware
        {
            Manufacturer = "Contoso",
            Model = "DeskPro",
            Chassis = "Desktop",
            Processor = "Intel Core",
            Cores = "8 cores / 16 threads",
            InstalledRam = "32 GB",
            MemoryUsage = "8 / 32 GB used (25%)",
            Graphics = "Integrated",
            Storage = "NVMe (512 GB)",
            StorageFree = "300 GB free of 512 GB (59%)",
            BiosVersion = "1.2.3",
            Battery = "Not present"
        },
        User = new SystemOverviewUser
        {
            CurrentUser = @"DOMAIN\tech",
            ProfilePath = @"C:\Users\tech",
            AdminRights = "Administrator",
            Elevated = "Yes (elevated)"
        },
        Network = new SystemOverviewNetwork
        {
            AdapterName = "Ethernet",
            ConnectionType = "Ethernet",
            Ipv4Address = "192.168.1.25",
            SubnetPrefix = "/24 (255.255.255.0)",
            DefaultGateway = "192.168.1.1",
            DnsServers = "1.1.1.1, 8.8.8.8",
            DhcpEnabled = "Yes",
            DhcpServer = "192.168.1.1",
            MacAddress = "AA:BB:CC:DD:EE:FF",
            LinkSpeed = "1000 Mbps",
            WifiSsid = "-"
        },
        Organization = new SystemOverviewOrg
        {
            WorkgroupOrDomain = "DOMAIN",
            DomainJoined = "Yes",
            EntraJoined = "YES",
            HybridJoined = "Yes",
            MdmEnrollment = "Enrolled",
            LogonServer = @"\\DC01"
        },
        Security = new SystemOverviewSecurity
        {
            DefenderStatus = "AV on, real-time on",
            FirewallStatus = "On (all profiles)",
            BitLockerStatus = "FullyEncrypted (On)",
            UacStatus = "Enabled",
            PendingReboot = "No",
            SecureBoot = "Enabled",
            TpmStatus = "Present, ready",
            WindowsActivation = "Activated"
        }
    };

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!condition())
        {
            cts.Token.ThrowIfCancellationRequested();
            await Task.Delay(20, cts.Token);
        }
    }
}
