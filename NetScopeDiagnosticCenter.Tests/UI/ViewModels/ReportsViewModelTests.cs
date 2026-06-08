using System.IO;
using NetScopeDiagnosticCenter.Collectors;
using NetScopeDiagnosticCenter.Core.Models;
using NetScopeDiagnosticCenter.Infrastructure;
using NetScopeDiagnosticCenter.Reports;
using NetScopeDiagnosticCenter.UI.ViewModels;

namespace NetScopeDiagnosticCenter.Tests.UI.ViewModels;

/// <summary>
/// Behavior tests for ReportsViewModel using a fake host. Verifies that
/// commands route through the host callbacks (status, busy, paths-changed)
/// and that the summary builder mirrors the legacy logic.
/// </summary>
public class ReportsViewModelTests : IDisposable
{
    private sealed class FakeHost : ReportsViewModel.IHost
    {
        public NetworkDiagnosisResult? LastDiagnosis { get; set; }
        public LinkQualityResult? LastLinkQualityResult { get; set; }
        public NetworkProfile NetworkProfile { get; set; } = new();

        public List<string> StatusMessages { get; } = [];
        public List<bool> BusyTransitions { get; } = [];
        public int PathsChangedCount { get; private set; }

        public void NotifyStatus(string message) => StatusMessages.Add(message);
        public void NotifyBusy(bool busy) => BusyTransitions.Add(busy);
        public void NotifyReportPathsChanged() => PathsChangedCount++;
    }

    private sealed class NullLogger : ILoggingService
    {
        public void Debug(string message) { }
        public void Info(string message) { }
        public void Warning(string message) { }
        public void Error(string message, Exception? exception = null) { }
    }

    private readonly string _isolatedAppData;

    public ReportsViewModelTests()
    {
        _isolatedAppData = Path.Combine(Path.GetTempPath(), "NetScopeTests-Reports-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_isolatedAppData);
    }

    public void Dispose()
    {
        try { Directory.Delete(_isolatedAppData, recursive: true); } catch { /* best-effort */ }
    }

    private (ReportsViewModel vm, FakeHost host) Build(NetworkDiagnosisResult? diagnosis = null)
    {
        var appStorage = new AppStorageService(_isolatedAppData);
        appStorage.EnsureFolders();
        var storage = new ReportStorageService(appStorage, new HtmlReportBuilder(), new TextSummaryBuilder());
        var psRunner = new PowerShellRunner(new NullLogger());
        var wifi = new WifiCollector(psRunner);
        var host = new FakeHost { LastDiagnosis = diagnosis };
        var vm = new ReportsViewModel(host, storage, wifi, new NullLogger());
        return (vm, host);
    }

    [Fact]
    public void SummaryText_NoDiagnosis_ReturnsPlaceholder()
    {
        var (vm, _) = Build(diagnosis: null);
        vm.SummaryText.Should().Contain("No diagnosis has been run yet");
    }

    [Fact]
    public void SummaryText_WithDiagnosis_IncludesIdentity()
    {
        var diagnosis = new NetworkDiagnosisResult
        {
            ComputerName = "TEST-PC",
            UserName = "tester",
            Verdict = new DiagnosisVerdict { Title = "All good", Severity = "OK", Confidence = "High" }
        };
        var (vm, _) = Build(diagnosis);

        vm.SummaryText.Should().Contain("TEST-PC");
        vm.SummaryText.Should().Contain("tester");
        vm.SummaryText.Should().Contain("All good");
    }

    [Fact]
    public async Task GenerateReportAsync_NoDiagnosis_NotifiesStatusAndDoesNothing()
    {
        var (vm, host) = Build(diagnosis: null);

        await vm.GenerateReportAsync();

        host.StatusMessages.Should().Contain(s => s.Contains("Run a diagnosis before generating"));
        vm.LastReportPath.Should().BeEmpty();
        host.PathsChangedCount.Should().Be(0);
    }

    [Fact]
    public async Task GenerateReportAsync_WithDiagnosis_PopulatesPathsAndNotifies()
    {
        var diagnosis = new NetworkDiagnosisResult
        {
            ComputerName = "TEST-PC",
            UserName = "tester",
            Verdict = new DiagnosisVerdict { Title = "Issue", Severity = "Warning" }
        };
        var (vm, host) = Build(diagnosis);

        await vm.GenerateReportAsync();

        vm.LastReportPath.Should().NotBeEmpty();
        vm.LastTextSummaryPath.Should().NotBeEmpty();
        vm.LastRawJsonPath.Should().NotBeEmpty();
        host.PathsChangedCount.Should().BeGreaterThan(0);
        host.BusyTransitions.Should().ContainInOrder(true, false);
        host.StatusMessages.Should().Contain(s => s.StartsWith("Report saved:"));
    }

    [Fact]
    public async Task GenerateReportAsync_AppliesProfileToDiagnosis()
    {
        var diagnosis = new NetworkDiagnosisResult();
        var profile = new NetworkProfile { ProfileName = "Custom" };
        var (vm, host) = Build(diagnosis);
        host.NetworkProfile = profile;

        await vm.GenerateReportAsync();

        diagnosis.Profile.Should().BeSameAs(profile);
    }

    [Fact]
    public async Task GenerateReportAsync_ReportableLinkQuality_AttachedToDiagnosis()
    {
        var diagnosis = new NetworkDiagnosisResult();
        var linkQuality = new LinkQualityResult
        {
            Source = "Manual ping",
            Severity = "OK",
            PingResults = [new LinkQualityPingResult { Target = "1.1.1.1" }]
        };
        var (vm, host) = Build(diagnosis);
        host.LastLinkQualityResult = linkQuality;

        await vm.GenerateReportAsync();

        diagnosis.LastLinkQuality.Should().BeSameAs(linkQuality);
    }

    [Fact]
    public async Task GenerateReportAsync_NonReportableLinkQuality_NotAttached()
    {
        var diagnosis = new NetworkDiagnosisResult();
        var linkQuality = new LinkQualityResult
        {
            Source = "No Quick Diagnosis result",
            Severity = "OK",
            // empty ping/dns results — fails the IsReportable predicate
        };
        var (vm, host) = Build(diagnosis);
        host.LastLinkQualityResult = linkQuality;

        await vm.GenerateReportAsync();

        diagnosis.LastLinkQuality.Should().BeNull();
    }

    [Fact]
    public void IsReportableLinkQuality_RequiresMeasuredEvidence()
    {
        var emptyManual = new LinkQualityResult { Source = "Manual ping" };
        ReportsViewModel.IsReportableLinkQuality(emptyManual).Should().BeFalse();

        var emptyNoData = new LinkQualityResult { Source = "No data" };
        ReportsViewModel.IsReportableLinkQuality(emptyNoData).Should().BeFalse();

        var emptyDefault = new LinkQualityResult { Source = "No Quick Diagnosis result" };
        ReportsViewModel.IsReportableLinkQuality(emptyDefault).Should().BeFalse();

        var defaultWithPings = new LinkQualityResult
        {
            Source = "No Quick Diagnosis result",
            PingResults = [new LinkQualityPingResult { Target = "x" }]
        };
        ReportsViewModel.IsReportableLinkQuality(defaultWithPings).Should().BeTrue();

        var noDataWithEvidence = new LinkQualityResult
        {
            Source = "No data",
            Evidence = ["Standalone ping completed."]
        };
        ReportsViewModel.IsReportableLinkQuality(noDataWithEvidence).Should().BeTrue();
    }

    [Fact]
    public void OpenPath_EmptyPath_NotifiesStatusOnly()
    {
        var (vm, host) = Build();

        vm.OpenPath(string.Empty);

        host.StatusMessages.Should().Contain(s => s.Contains("No file path"));
    }

    [Fact]
    public void NotifyDiagnosisChanged_FiresPropertyChangedForSummary()
    {
        var (vm, _) = Build();
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        vm.NotifyDiagnosisChanged();

        changed.Should().Contain(nameof(ReportsViewModel.SummaryText));
    }

    [Fact]
    public void Commands_AreNotNull()
    {
        var (vm, _) = Build();
        vm.GenerateReportCommand.Should().NotBeNull();
        vm.GenerateWlanReportCommand.Should().NotBeNull();
        vm.CopySummaryCommand.Should().NotBeNull();
        vm.OpenReportsFolderCommand.Should().NotBeNull();
    }

    [Fact]
    public void HasDiagnosis_ReflectsHostState()
    {
        Build(diagnosis: null).vm.HasDiagnosis.Should().BeFalse();
        Build(new NetworkDiagnosisResult()).vm.HasDiagnosis.Should().BeTrue();
    }

    [Fact]
    public void GenerateAndCopyCommands_DisabledWithoutDiagnosis()
    {
        var (vm, _) = Build(diagnosis: null);
        vm.GenerateReportCommand.CanExecute(null).Should().BeFalse();
        vm.CopySummaryCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public void GenerateAndCopyCommands_EnabledWithDiagnosis()
    {
        var (vm, _) = Build(new NetworkDiagnosisResult());
        vm.GenerateReportCommand.CanExecute(null).Should().BeTrue();
        vm.CopySummaryCommand.CanExecute(null).Should().BeTrue();
    }

    [Fact]
    public void VerdictSurface_ReflectsDiagnosis()
    {
        var diagnosis = new NetworkDiagnosisResult
        {
            Verdict = new DiagnosisVerdict { Title = "DNS failure", Severity = "Critical", Confidence = "High", AffectedLayer = "DNS" },
            HealthScore = new HealthScoreResult { Score = 42, Status = "Poor" },
        };
        var (vm, _) = Build(diagnosis);

        vm.VerdictTitle.Should().Be("DNS failure");
        vm.VerdictSeverity.Should().Be("Critical");
        vm.VerdictConfidence.Should().Be("High");
        vm.VerdictLayer.Should().Be("DNS");
        vm.HealthScoreText.Should().Be("42/100");
        vm.HealthStatus.Should().Be("Poor");
    }

    [Fact]
    public void VerdictSurface_NoDiagnosis_ShowsEmptyState()
    {
        var (vm, _) = Build(diagnosis: null);
        vm.VerdictTitle.Should().Contain("No diagnosis");
        vm.VerdictSeverity.Should().Be("Unknown");
        vm.HealthScoreText.Should().Be("—");
    }

    [Fact]
    public void GeneratedFiles_EmptyBeforeExport()
    {
        var (vm, _) = Build(diagnosis: null);
        vm.HasHtmlReport.Should().BeFalse();
        vm.HasAnyReport.Should().BeFalse();
        vm.HasWlanReport.Should().BeFalse();
        vm.HtmlReportPathText.Should().Be("Not generated yet");
        vm.TextReportPathText.Should().Be("Not generated yet");
        vm.JsonReportPathText.Should().Be("Not generated yet");
        vm.WlanReportPathText.Should().Be("Not generated yet");
    }

    [Fact]
    public async Task GenerateReportAsync_PopulatesFileFlagsAndExportSummary()
    {
        var (vm, _) = Build(new NetworkDiagnosisResult());

        await vm.GenerateReportAsync();

        vm.HasHtmlReport.Should().BeTrue();
        vm.HasTextReport.Should().BeTrue();
        vm.HasJsonReport.Should().BeTrue();
        vm.HasAnyReport.Should().BeTrue();
        vm.HtmlReportPathText.Should().EndWith(".html");
        vm.LastExportSummary.Should().NotBeEmpty();
    }

    [Fact]
    public async Task GenerateReportAsync_ConsecutiveExports_UseDistinctFileNames()
    {
        var (vm, _) = Build(new NetworkDiagnosisResult());

        await vm.GenerateReportAsync();
        var firstHtml = vm.LastReportPath;
        var firstText = vm.LastTextSummaryPath;
        var firstJson = vm.LastRawJsonPath;

        await vm.GenerateReportAsync();

        vm.LastReportPath.Should().NotBe(firstHtml);
        vm.LastTextSummaryPath.Should().NotBe(firstText);
        vm.LastRawJsonPath.Should().NotBe(firstJson);
        File.Exists(firstHtml).Should().BeTrue();
        File.Exists(firstText).Should().BeTrue();
        File.Exists(firstJson).Should().BeTrue();
    }

    [Fact]
    public void OpenPath_RejectsUnsupportedFileType()
    {
        var (vm, host) = Build(diagnosis: null);
        var path = Path.Combine(_isolatedAppData, "not-a-report.ps1");
        File.WriteAllText(path, "Write-Host blocked");

        vm.OpenPath(path);

        host.StatusMessages.Should().Contain(s => s.Contains("Unsupported file type"));
    }

    [Fact]
    public void OpenPath_RejectsHtmlOutsideKnownReportLocations()
    {
        var (vm, host) = Build(diagnosis: null);
        var path = Path.Combine(_isolatedAppData, "external.html");
        File.WriteAllText(path, "<html></html>");

        vm.OpenPath(path);

        host.StatusMessages.Should().Contain(s => s.Contains("only opens generated reports"));
    }

    [Fact]
    public void OpenPath_MissingFile_NotifiesStatus()
    {
        var (vm, host) = Build(diagnosis: null);
        var path = Path.Combine(_isolatedAppData, "missing.html");

        vm.OpenPath(path);

        host.StatusMessages.Should().Contain(s => s.Contains("File does not exist"));
    }

    [Fact]
    public void NotifyDiagnosisChanged_AfterDiagnosisSet_EnablesCommandsAndRaisesEvents()
    {
        var (vm, host) = Build(diagnosis: null);
        vm.GenerateReportCommand.CanExecute(null).Should().BeFalse();

        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        host.LastDiagnosis = new NetworkDiagnosisResult
        {
            Verdict = new DiagnosisVerdict { Title = "Healthy" },
        };
        vm.NotifyDiagnosisChanged();

        vm.HasDiagnosis.Should().BeTrue();
        vm.VerdictTitle.Should().Be("Healthy");
        vm.GenerateReportCommand.CanExecute(null).Should().BeTrue();
        changed.Should().Contain(nameof(ReportsViewModel.HasDiagnosis));
        changed.Should().Contain(nameof(ReportsViewModel.VerdictTitle));
    }

    [Fact]
    public void Constructor_NullDependencies_ThrowArgumentNullException()
    {
        var appStorage = new AppStorageService(_isolatedAppData);
        appStorage.EnsureFolders();
        var storage = new ReportStorageService(appStorage, new HtmlReportBuilder(), new TextSummaryBuilder());
        var wifi = new WifiCollector(new PowerShellRunner(new NullLogger()));
        var host = new FakeHost();
        var logger = new NullLogger();

        FluentActions.Invoking(() => new ReportsViewModel(null!, storage, wifi, logger))
            .Should().Throw<ArgumentNullException>().WithParameterName("host");
        FluentActions.Invoking(() => new ReportsViewModel(host, null!, wifi, logger))
            .Should().Throw<ArgumentNullException>().WithParameterName("reportStorage");
        FluentActions.Invoking(() => new ReportsViewModel(host, storage, null!, logger))
            .Should().Throw<ArgumentNullException>().WithParameterName("wifiCollector");
        FluentActions.Invoking(() => new ReportsViewModel(host, storage, wifi, null!))
            .Should().Throw<ArgumentNullException>().WithParameterName("logger");
    }

    // Note: CopySummary is not unit-tested because Clipboard.SetText requires STA
    // and SummaryText always returns a placeholder (never empty), so the early-return
    // branch is unreachable. Behavior is verified via manual run.
}
