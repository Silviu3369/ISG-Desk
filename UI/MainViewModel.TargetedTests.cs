using System.Windows.Input;
using NetScopeDiagnosticCenter.Core.Models;
using NetScopeDiagnosticCenter.UI.ViewModels;

namespace NetScopeDiagnosticCenter.UI;

/// <summary>
/// Phase 2d migration shim: real targeted-test logic lives in <see cref="TargetedTestsViewModel"/>.
/// MainWindow.xaml binds to MainViewModel; every member here delegates to <c>TargetedTests.X</c>.
/// </summary>
public sealed partial class MainViewModel : TargetedTestsViewModel.IHost
{
    /// <summary>Dedicated targeted-tests VM. Constructed in MainViewModel.cs ctor.</summary>
    public TargetedTestsViewModel TargetedTests { get; private set; } = null!;

    // Recent-target lists are owned by MainViewModel (cross-VM via _recentTargets);
    // RecentPrinterTargets is consumed by Printers UI as well.
    public IReadOnlyList<string> RecentServerTargets => _recentTargets.ServerTargets;
    public IReadOnlyList<string> RecentServiceTargets => _recentTargets.ServiceTargets;
    public IReadOnlyList<string> RecentPrinterTargets => _recentTargets.PrinterTargets;

    // === Targeted-test state delegations ===
    public IReadOnlyList<ScenarioDefinition> TestDefinitions => TargetedTests.TestDefinitions;
    public string SelectedTestKey { get => TargetedTests.SelectedTestKey; set => TargetedTests.SelectedTestKey = value; }
    public ScenarioDefinition SelectedTestDefinition => TargetedTests.SelectedTestDefinition;
    public string SelectedTestName => TargetedTests.SelectedTestName;
    public string SelectedTestDescription => TargetedTests.SelectedTestDescription;
    public bool IsShareTestSelected => TargetedTests.IsShareTestSelected;
    public bool IsDomainTestSelected => TargetedTests.IsDomainTestSelected;
    public bool IsServiceTestSelected => TargetedTests.IsServiceTestSelected;
    public string SelectedTestInputNote => TargetedTests.SelectedTestInputNote;
    public bool CanRunSelectedTest => TargetedTests.CanRunSelectedTest;
    public string SelectedTestInputStatusText => TargetedTests.SelectedTestInputStatusText;
    public string SelectedTestInputSeverity => TargetedTests.SelectedTestInputSeverity;
    public bool CanScanServiceTargets => TargetedTests.CanScanServiceTargets;
    public string ServicePortInputStatusText => TargetedTests.ServicePortInputStatusText;
    public string ServicePortInputSeverity => TargetedTests.ServicePortInputSeverity;
    public string SelectedServicePortCountText => TargetedTests.SelectedServicePortCountText;
    public string ServicePortLimitText => TargetedTests.ServicePortLimitText;
    public string ServiceTargetCountText => TargetedTests.ServiceTargetCountText;
    public IReadOnlyList<TargetedTestsViewModel.ServicePortGroup> ServicePortGroups => TargetedTests.ServicePortGroups;
    public ShareTargetDiscoveryResult? LastShareDiscoveryResult => TargetedTests.LastShareDiscoveryResult;
    public bool HasShareDiscoveryResult => TargetedTests.HasShareDiscoveryResult;
    public bool HasNoShareDiscoveryResult => TargetedTests.HasNoShareDiscoveryResult;
    public bool HasShareDiscoveryCandidates => TargetedTests.HasShareDiscoveryCandidates;
    public bool HasNoShareDiscoveryCandidates => TargetedTests.HasNoShareDiscoveryCandidates;
    public string ShareDiscoverySeverity => TargetedTests.ShareDiscoverySeverity;
    public string ShareDiscoveryStatusText => TargetedTests.ShareDiscoveryStatusText;
    public string ShareDiscoverySourceText => TargetedTests.ShareDiscoverySourceText;
    public string ShareDiscoveryScanScopeText => TargetedTests.ShareDiscoveryScanScopeText;
    public string ShareTargetCountText => TargetedTests.ShareTargetCountText;
    public string ShareInputStatusText => TargetedTests.ShareInputStatusText;
    public string ShareInputSeverity => TargetedTests.ShareInputSeverity;
    public DomainControllerDiscoveryResult? LastDomainDiscoveryResult => TargetedTests.LastDomainDiscoveryResult;
    public bool HasDomainDiscoveryResult => TargetedTests.HasDomainDiscoveryResult;
    public bool HasNoDomainDiscoveryResult => TargetedTests.HasNoDomainDiscoveryResult;
    public bool HasDomainDiscoveryCandidates => TargetedTests.HasDomainDiscoveryCandidates;
    public bool HasNoDomainDiscoveryCandidates => TargetedTests.HasNoDomainDiscoveryCandidates;
    public string DomainDiscoverySeverity => TargetedTests.DomainDiscoverySeverity;
    public string DomainDiscoveryStatusText => TargetedTests.DomainDiscoveryStatusText;
    public string DomainDiscoveryDomainsText => TargetedTests.DomainDiscoveryDomainsText;
    public string DomainControllerCountText => TargetedTests.DomainControllerCountText;
    public string DomainInputStatusText => TargetedTests.DomainInputStatusText;
    public string DomainInputSeverity => TargetedTests.DomainInputSeverity;
    public ServiceTargetDiscoveryResult? LastServiceDiscoveryResult => TargetedTests.LastServiceDiscoveryResult;
    public bool HasServiceDiscoveryResult => TargetedTests.HasServiceDiscoveryResult;
    public bool HasNoServiceDiscoveryResult => TargetedTests.HasNoServiceDiscoveryResult;
    public bool HasServiceDiscoveryCandidates => TargetedTests.HasServiceDiscoveryCandidates;
    public bool HasNoServiceDiscoveryCandidates => TargetedTests.HasNoServiceDiscoveryCandidates;
    public string ServiceDiscoverySeverity => TargetedTests.ServiceDiscoverySeverity;
    public string ServiceDiscoveryStatusText => TargetedTests.ServiceDiscoveryStatusText;
    public string ServiceDiscoverySourceText => TargetedTests.ServiceDiscoverySourceText;
    public string ServiceDiscoveryScanScopeText => TargetedTests.ServiceDiscoveryScanScopeText;

    public bool IsTestRunning { get => TargetedTests.IsTestRunning; set => TargetedTests.IsTestRunning = value; }
    public bool IsShareDiscoveryRunning { get => TargetedTests.IsShareDiscoveryRunning; set => TargetedTests.IsShareDiscoveryRunning = value; }
    public bool IsDomainDiscoveryRunning { get => TargetedTests.IsDomainDiscoveryRunning; set => TargetedTests.IsDomainDiscoveryRunning = value; }
    public bool IsServiceDiscoveryRunning { get => TargetedTests.IsServiceDiscoveryRunning; set => TargetedTests.IsServiceDiscoveryRunning = value; }
    public string ShareTestTarget { get => TargetedTests.ShareTestTarget; set => TargetedTests.ShareTestTarget = value; }
    public string DomainControllerOverride { get => TargetedTests.DomainControllerOverride; set => TargetedTests.DomainControllerOverride = value; }
    public string ServiceTestTarget { get => TargetedTests.ServiceTestTarget; set => TargetedTests.ServiceTestTarget = value; }
    public string ServiceTestPorts { get => TargetedTests.ServiceTestPorts; set => TargetedTests.ServiceTestPorts = value; }

    // === Targeted-test result derived properties ===
    public bool HasTestResult => TargetedTests.HasTestResult;
    public bool HasNoTestResult => TargetedTests.HasNoTestResult;
    public string TestResultTitle => TargetedTests.TestResultTitle;
    public string TestResultName => TargetedTests.TestResultName;
    public string TestResultSeverity => TargetedTests.TestResultSeverity;
    public string TestResultOwner => TargetedTests.TestResultOwner;
    public string TestResultLayer => TargetedTests.TestResultLayer;
    public string TestResultConfidence => TargetedTests.TestResultConfidence;
    public string TestResultInput => TargetedTests.TestResultInput;
    public string TestResultBaseline => TargetedTests.TestResultBaseline;
    public string TestResultEmptyText => TargetedTests.TestResultEmptyText;
    public IReadOnlyList<DiagnosisStepResult> TestSteps => TargetedTests.TestSteps;
    public IReadOnlyList<string> TestEvidence => TargetedTests.TestEvidence;
    public IReadOnlyList<string> TestNextChecks => TargetedTests.TestNextChecks;
    public IReadOnlyList<string> TestLimitations => TargetedTests.TestLimitations;
    public IReadOnlyList<TargetProbeResult> TestTargetResults => TargetedTests.TestTargetResults;
    public bool HasTestSteps => TargetedTests.HasTestSteps;
    public bool HasNoTestSteps => TargetedTests.HasNoTestSteps;
    public bool HasTestEvidence => TargetedTests.HasTestEvidence;
    public bool HasNoTestEvidence => TargetedTests.HasNoTestEvidence;
    public bool HasTestNextChecks => TargetedTests.HasTestNextChecks;
    public bool HasNoTestNextChecks => TargetedTests.HasNoTestNextChecks;
    public bool HasTestLimitations => TargetedTests.HasTestLimitations;
    public bool HasNoTestLimitations => TargetedTests.HasNoTestLimitations;
    public bool HasTestTargetResults => TargetedTests.HasTestTargetResults;
    public bool HasNoTestTargetResults => TargetedTests.HasNoTestTargetResults;
    public string TestTargetResultsEmptyText => TargetedTests.TestTargetResultsEmptyText;

    // === Commands ===
    public ICommand SelectTestCommand => TargetedTests.SelectTestCommand;
    public ICommand RunTestCommand => TargetedTests.RunTestCommand;
    public ICommand RunSelectedTestCommand => TargetedTests.RunSelectedTestCommand;
    public ICommand CancelTestCommand => TargetedTests.CancelTestCommand;
    public ICommand DetectShareTargetCommand => TargetedTests.DetectShareTargetCommand;
    public ICommand ScanShareTargetsCommand => TargetedTests.ScanShareTargetsCommand;
    public ICommand CancelShareDiscoveryCommand => TargetedTests.CancelShareDiscoveryCommand;
    public ICommand DetectDomainTargetCommand => TargetedTests.DetectDomainTargetCommand;
    public ICommand DiscoverDomainControllersCommand => TargetedTests.DiscoverDomainControllersCommand;
    public ICommand CancelDomainDiscoveryCommand => TargetedTests.CancelDomainDiscoveryCommand;
    public ICommand DetectServiceTargetCommand => TargetedTests.DetectServiceTargetCommand;
    public ICommand ScanServiceTargetsCommand => TargetedTests.ScanServiceTargetsCommand;
    public ICommand CancelServiceDiscoveryCommand => TargetedTests.CancelServiceDiscoveryCommand;
    public ICommand SelectDefaultServicePortsCommand => TargetedTests.SelectDefaultServicePortsCommand;
    public ICommand SelectCoreServicePortsCommand => TargetedTests.SelectCoreServicePortsCommand;
    public ICommand ClearServicePortsCommand => TargetedTests.ClearServicePortsCommand;
    public ICommand CopyTargetedTestSummaryCommand => TargetedTests.CopyTargetedTestSummaryCommand;

    /// <summary>
    /// Cross-VM "Forget recent targets" — touches Targeted Tests + Printers + in-session history.
    /// Stays on MainViewModel because it spans multiple VMs.
    /// </summary>
    private void ForgetRecentTargets()
    {
        _recentTargets = new RecentTargets();
        TargetedTests.ResetInputs();
        // PrintersVM input fields. Going through the shim setters delegates to PrintersViewModel.
        PrinterTarget = string.Empty;
        PrintServerName = string.Empty;
        PrinterScanRange = string.Empty;
        _appStorage.SaveRecentTargets(_recentTargets);
        OnPropertyChanged(nameof(RecentServerTargets));
        OnPropertyChanged(nameof(RecentServiceTargets));
        OnPropertyChanged(nameof(RecentPrinterTargets));
        StatusMessage = "Recent targets cleared.";
    }

    /// <summary>Re-fires targeted-test-derived bindings after the independent targeted result changes.</summary>
    private void RefreshTargetedTestsResultStatus() => TargetedTests?.NotifyDiagnosisChanged();

    // === TargetedTestsViewModel.IHost ===
    void TargetedTestsViewModel.IHost.NotifyStatus(string message) => PublishStatus(ActivitySourceModule.TargetedTests, message);
    void TargetedTestsViewModel.IHost.NotifyBusy(bool busy) => SetBusyState(busy);

    ScenarioDiagnosisResult? TargetedTestsViewModel.IHost.LastTargetedScenario
    {
        get => LastTargetedScenario;
        set => LastTargetedScenario = value;
    }

    NetworkProfile TargetedTestsViewModel.IHost.NetworkProfile => NetworkProfile;

    string TargetedTestsViewModel.IHost.CurrentPage
    {
        get => CurrentPage;
        set => CurrentPage = value;
    }

    void TargetedTestsViewModel.IHost.RememberShareTarget(string target)
    {
        RememberValue(_recentTargets.ServerTargets, target);
        _appStorage.SaveRecentTargets(_recentTargets);
        OnPropertyChanged(nameof(RecentServerTargets));
    }

    void TargetedTestsViewModel.IHost.RememberServiceTarget(string target)
    {
        RememberValue(_recentTargets.ServiceTargets, target);
        _appStorage.SaveRecentTargets(_recentTargets);
        OnPropertyChanged(nameof(RecentServiceTargets));
    }

    void TargetedTestsViewModel.IHost.SetDomainControllerOverride(string value)
    {
        _recentTargets.DomainControllerOverride = value;
        _appStorage.SaveRecentTargets(_recentTargets);
    }
}
