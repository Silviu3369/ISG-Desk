using System.Text;
using System.Windows.Input;
using NetScopeDiagnosticCenter.Collectors;
using NetScopeDiagnosticCenter.Core;
using NetScopeDiagnosticCenter.Core.Models;
using NetScopeDiagnosticCenter.Infrastructure;

namespace NetScopeDiagnosticCenter.UI.ViewModels;

/// <summary>
/// Targeted Tests ViewModel: three scenario runners for targets Quick Diagnosis cannot
/// fully answer by itself: internal shares, DNS/domain, and service ports. Each run
/// produces a fresh Quick Diagnosis baseline followed by scenario-specific checks.
/// </summary>
/// <remarks>
/// Phase 2d migration. The old zero-input scenarios were removed from the product
/// surface because they duplicated Quick Diagnosis or Wi-Fi Analyzer.
/// </remarks>
public sealed class TargetedTestsViewModel : ObservableObject
{
    public sealed class ServicePortOption : ObservableObject
    {
        private readonly Action<ServicePortOption>? _selectionChanged;
        private bool _isSelected;

        public ServicePortOption(int port, string name, string category, string description, bool isCore, Action<ServicePortOption>? selectionChanged)
        {
            Port = port;
            Name = name;
            Category = category;
            Description = description;
            IsCore = isCore;
            _selectionChanged = selectionChanged;
        }

        public int Port { get; }
        public string Name { get; }
        public string Category { get; }
        public string Description { get; }
        public bool IsCore { get; }
        public string DisplayName => $"{Name} ({Port})";

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (SetProperty(ref _isSelected, value))
                {
                    _selectionChanged?.Invoke(this);
                }
            }
        }
    }

    public sealed class ServicePortGroup
    {
        public ServicePortGroup(string name, IReadOnlyList<ServicePortOption> ports)
        {
            Name = name;
            Ports = ports;
        }

        public string Name { get; }
        public IReadOnlyList<ServicePortOption> Ports { get; }
    }

    public interface IHost
    {
        void NotifyStatus(string message);
        void NotifyBusy(bool busy);

        ScenarioDiagnosisResult? LastTargetedScenario { get; set; }
        NetworkProfile NetworkProfile { get; }
        string CurrentPage { get; set; }

        /// <summary>Remember a server/share target for the current session (raises RecentServerTargets).</summary>
        void RememberShareTarget(string target);
        /// <summary>Remember a service host target for the current session (raises RecentServiceTargets).</summary>
        void RememberServiceTarget(string target);
        /// <summary>Remember the domain controller override for the current session.</summary>
        void SetDomainControllerOverride(string value);
    }

    private readonly ScenarioEngine _scenarioEngine;
    private readonly TargetShareDiscoveryCollector _shareDiscoveryCollector;
    private readonly TargetServiceDiscoveryCollector _serviceDiscoveryCollector;
    private readonly DomainControllerDiscoveryCollector _domainDiscoveryCollector;
    private readonly ILoggingService _logger;
    private readonly IHost _host;
    private readonly List<ServicePortOption> _servicePortOptions;

    private string _selectedTestKey = ScenarioWorkflow.InternalShare;
    private string _shareTestTarget = string.Empty;
    private string _domainControllerOverride = string.Empty;
    private string _serviceTestTarget = string.Empty;
    private string _serviceTestPorts = "443, 445";
    private CancellationTokenSource? _testCancellation;
    private CancellationTokenSource? _shareDiscoveryCancellation;
    private CancellationTokenSource? _domainDiscoveryCancellation;
    private CancellationTokenSource? _serviceDiscoveryCancellation;
    private ShareTargetDiscoveryResult? _lastShareDiscoveryResult;
    private DomainControllerDiscoveryResult? _lastDomainDiscoveryResult;
    private ServiceTargetDiscoveryResult? _lastServiceDiscoveryResult;
    private bool _syncingServicePortOptions;
    private bool _isTestRunning;
    private bool _isShareDiscoveryRunning;
    private bool _isDomainDiscoveryRunning;
    private bool _isServiceDiscoveryRunning;

    public TargetedTestsViewModel(
        ScenarioEngine scenarioEngine,
        TargetShareDiscoveryCollector shareDiscoveryCollector,
        TargetServiceDiscoveryCollector serviceDiscoveryCollector,
        DomainControllerDiscoveryCollector domainDiscoveryCollector,
        ILoggingService logger,
        IHost host,
        string initialShareTarget = "",
        string initialServiceTarget = "",
        string initialDomainControllerOverride = "")
    {
        _scenarioEngine = scenarioEngine ?? throw new ArgumentNullException(nameof(scenarioEngine));
        _shareDiscoveryCollector = shareDiscoveryCollector ?? throw new ArgumentNullException(nameof(shareDiscoveryCollector));
        _serviceDiscoveryCollector = serviceDiscoveryCollector ?? throw new ArgumentNullException(nameof(serviceDiscoveryCollector));
        _domainDiscoveryCollector = domainDiscoveryCollector ?? throw new ArgumentNullException(nameof(domainDiscoveryCollector));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _host = host ?? throw new ArgumentNullException(nameof(host));

        _shareTestTarget = initialShareTarget;
        _serviceTestTarget = initialServiceTarget;
        _domainControllerOverride = initialDomainControllerOverride;
        _servicePortOptions = BuildServicePortOptions(OnServicePortOptionSelectionChanged);
        ServicePortGroups = BuildServicePortGroups(_servicePortOptions);

        SelectTestCommand = new RelayCommand(parameter => SelectedTestKey = parameter?.ToString() ?? ScenarioWorkflow.InternalShare);
        RunTestCommand = new AsyncRelayCommand(
            parameter => RunTestAsync(parameter?.ToString() ?? ScenarioWorkflow.InternalShare),
            parameter => !IsTestRunning && TryValidateTestInput(parameter?.ToString() ?? ScenarioWorkflow.InternalShare, out _));
        RunSelectedTestCommand = new AsyncRelayCommand(
            _ => RunTestAsync(SelectedTestKey),
            _ => !IsTestRunning && CanRunSelectedTest);
        CancelTestCommand = new RelayCommand(_ => CancelTest());
        DetectShareTargetCommand = new RelayCommand(_ => DetectShareTarget());
        ScanShareTargetsCommand = new AsyncRelayCommand(_ => ScanShareTargetsAsync(), _ => !IsTestRunning && !IsShareDiscoveryRunning);
        CancelShareDiscoveryCommand = new RelayCommand(_ => CancelShareDiscovery());
        DetectDomainTargetCommand = new RelayCommand(_ => DetectDomainTarget());
        DiscoverDomainControllersCommand = new AsyncRelayCommand(_ => DiscoverDomainControllersAsync(), _ => !IsTestRunning && !IsDomainDiscoveryRunning);
        CancelDomainDiscoveryCommand = new RelayCommand(_ => CancelDomainDiscovery());
        DetectServiceTargetCommand = new RelayCommand(_ => DetectServiceTarget());
        ScanServiceTargetsCommand = new AsyncRelayCommand(_ => ScanServiceTargetsAsync(), _ => !IsTestRunning && !IsServiceDiscoveryRunning && CanScanServiceTargets);
        CancelServiceDiscoveryCommand = new RelayCommand(_ => CancelServiceDiscovery());
        SelectDefaultServicePortsCommand = new RelayCommand(_ => SelectServicePorts([443, 445]));
        SelectCoreServicePortsCommand = new RelayCommand(_ => SelectServicePorts(_servicePortOptions.Where(option => option.IsCore).Select(option => option.Port)));
        ClearServicePortsCommand = new RelayCommand(_ => ServiceTestPorts = string.Empty);
        CopyTargetedTestSummaryCommand = new RelayCommand(_ => CopyTargetedTestSummary(), _ => HasTestResult);

        SyncServicePortOptionsFromText();
    }

    public IReadOnlyList<ScenarioDefinition> TestDefinitions => ScenarioWorkflow.All;

    public ICommand SelectTestCommand { get; }
    public ICommand RunTestCommand { get; }
    public ICommand RunSelectedTestCommand { get; }
    public ICommand CancelTestCommand { get; }
    public ICommand DetectShareTargetCommand { get; }
    public ICommand ScanShareTargetsCommand { get; }
    public ICommand CancelShareDiscoveryCommand { get; }
    public ICommand DetectDomainTargetCommand { get; }
    public ICommand DiscoverDomainControllersCommand { get; }
    public ICommand CancelDomainDiscoveryCommand { get; }
    public ICommand DetectServiceTargetCommand { get; }
    public ICommand ScanServiceTargetsCommand { get; }
    public ICommand CancelServiceDiscoveryCommand { get; }
    public ICommand SelectDefaultServicePortsCommand { get; }
    public ICommand SelectCoreServicePortsCommand { get; }
    public ICommand ClearServicePortsCommand { get; }
    public ICommand CopyTargetedTestSummaryCommand { get; }
    public IReadOnlyList<ServicePortGroup> ServicePortGroups { get; }

    public string SelectedTestKey
    {
        get => _selectedTestKey;
        set
        {
            if (SetProperty(ref _selectedTestKey, value))
            {
                OnPropertyChanged(nameof(SelectedTestDefinition));
                OnPropertyChanged(nameof(SelectedTestName));
                OnPropertyChanged(nameof(SelectedTestDescription));
                OnPropertyChanged(nameof(IsShareTestSelected));
                OnPropertyChanged(nameof(IsDomainTestSelected));
                OnPropertyChanged(nameof(IsServiceTestSelected));
                OnPropertyChanged(nameof(SelectedTestInputNote));
                RefreshInputState();
            }
        }
    }

    public ScenarioDefinition SelectedTestDefinition =>
        TestDefinitions.FirstOrDefault(definition => definition.Key == SelectedTestKey)
        ?? TestDefinitions.First();

    public string SelectedTestName => SelectedTestDefinition.Name;
    public string SelectedTestDescription => SelectedTestDefinition.Description;
    public bool IsShareTestSelected => SelectedTestKey == ScenarioWorkflow.InternalShare;
    public bool IsDomainTestSelected => SelectedTestKey == ScenarioWorkflow.DnsDomain;
    public bool IsServiceTestSelected => SelectedTestKey == ScenarioWorkflow.ServiceAccess;

    public string SelectedTestInputNote => SelectedTestKey switch
    {
        ScenarioWorkflow.InternalShare => "Enter servers or UNC shares, use local auto-detect, or run a safe SMB scan on the local private subnet.",
        ScenarioWorkflow.DnsDomain => "Use local/profile DCs, discover AD domain controllers through DNS SRV, or enter an override.",
        ScenarioWorkflow.ServiceAccess => "Enter one or more hosts, use local/profile auto-detect, or scan the local private subnet for the selected ports.",
        _ => "Select a targeted test."
    };

    public bool IsTestRunning
    {
        get => _isTestRunning;
        set
        {
            if (SetProperty(ref _isTestRunning, value))
            {
                RefreshInputState();
                RefreshDiscoveryCommandState();
            }
        }
    }

    public bool IsShareDiscoveryRunning
    {
        get => _isShareDiscoveryRunning;
        set
        {
            if (SetProperty(ref _isShareDiscoveryRunning, value))
            {
                RefreshDiscoveryCommandState();
            }
        }
    }

    public bool IsDomainDiscoveryRunning
    {
        get => _isDomainDiscoveryRunning;
        set
        {
            if (SetProperty(ref _isDomainDiscoveryRunning, value))
            {
                RefreshDiscoveryCommandState();
            }
        }
    }

    public bool IsServiceDiscoveryRunning
    {
        get => _isServiceDiscoveryRunning;
        set
        {
            if (SetProperty(ref _isServiceDiscoveryRunning, value))
            {
                RefreshDiscoveryCommandState();
            }
        }
    }

    public string ShareTestTarget
    {
        get => _shareTestTarget;
        set
        {
            if (SetProperty(ref _shareTestTarget, value))
            {
                RefreshInputState();
            }
        }
    }

    public string DomainControllerOverride
    {
        get => _domainControllerOverride;
        set
        {
            if (SetProperty(ref _domainControllerOverride, value))
            {
                RefreshInputState();
            }
        }
    }

    public string ServiceTestTarget
    {
        get => _serviceTestTarget;
        set
        {
            if (SetProperty(ref _serviceTestTarget, value))
            {
                RefreshInputState();
            }
        }
    }

    public string ServiceTestPorts
    {
        get => _serviceTestPorts;
        set
        {
            if (SetProperty(ref _serviceTestPorts, value))
            {
                SyncServicePortOptionsFromText();
                RefreshInputState();
            }
        }
    }

    public bool CanRunSelectedTest => TryValidateTestInput(SelectedTestKey, out _);

    public string SelectedTestInputStatusText
    {
        get
        {
            if (!TryValidateTestInput(SelectedTestKey, out var message))
            {
                return message;
            }

            return SelectedTestKey switch
            {
                ScenarioWorkflow.InternalShare when string.IsNullOrWhiteSpace(ShareTestTarget) =>
                    "Ready: this run will use detected/profile file server targets.",
                ScenarioWorkflow.InternalShare =>
                    "Ready: server/share target format is valid.",
                ScenarioWorkflow.DnsDomain when string.IsNullOrWhiteSpace(DomainControllerOverride) =>
                    "Ready: this run will use detected/profile domain controller targets.",
                ScenarioWorkflow.DnsDomain =>
                    "Ready: domain controller override format is valid.",
                ScenarioWorkflow.ServiceAccess =>
                    $"Ready: service target and TCP ports are valid ({string.Join(", ", ParseValidatedPorts(ServiceTestPorts))}).",
                _ => "Ready to run selected targeted test."
            };
        }
    }

    public string SelectedTestInputSeverity => CanRunSelectedTest ? "OK" : "Warning";
    public bool CanScanServiceTargets => TryValidatePorts(ServiceTestPorts, out _);
    public string ServicePortInputStatusText => CanScanServiceTargets
        ? $"Service scan will check TCP {string.Join(", ", ParseValidatedPorts(ServiceTestPorts))}."
        : GetPortValidationMessage();
    public string ServicePortInputSeverity => CanScanServiceTargets ? "OK" : "Warning";
    public string SelectedServicePortCountText
    {
        get
        {
            var selected = SplitPortList(ServiceTestPorts).Count();
            return selected == 0
                ? $"No TCP ports selected. Limit: {DiagnosticConstants.MaxTargetedTestPorts} per run."
                : $"{selected}/{DiagnosticConstants.MaxTargetedTestPorts} TCP port(s) selected.";
        }
    }
    public string ServicePortLimitText => $"Production safety limit: select up to {DiagnosticConstants.MaxTargetedTestPorts} TCP ports per run. For wider checks, run ports in batches.";
    public string ServiceTargetCountText
    {
        get
        {
            var count = SplitTargetList(ServiceTestTarget).Count();
            return count == 0 ? "No service target entered." : $"{count} target(s) queued.";
        }
    }
    public ShareTargetDiscoveryResult? LastShareDiscoveryResult
    {
        get => _lastShareDiscoveryResult;
        private set
        {
            if (SetProperty(ref _lastShareDiscoveryResult, value))
            {
                RefreshShareDiscoveryState();
            }
        }
    }

    public bool HasShareDiscoveryResult => LastShareDiscoveryResult is not null;
    public bool HasNoShareDiscoveryResult => !HasShareDiscoveryResult;
    public bool HasShareDiscoveryCandidates => LastShareDiscoveryResult?.Candidates.Count > 0;
    public bool HasNoShareDiscoveryCandidates => !HasShareDiscoveryCandidates;
    public string ShareDiscoverySeverity => LastShareDiscoveryResult?.Severity ?? "Unknown";
    public string ShareDiscoveryStatusText => LastShareDiscoveryResult is null
        ? "No SMB discovery run in this session."
        : string.IsNullOrWhiteSpace(LastShareDiscoveryResult.SkippedReason)
            ? LastShareDiscoveryResult.Verdict
            : LastShareDiscoveryResult.SkippedReason;
    public string ShareDiscoverySourceText => LastShareDiscoveryResult is null
        ? "Source: none"
        : string.IsNullOrWhiteSpace(LastShareDiscoveryResult.Source)
            ? "Source: local adapter detection"
            : $"Source: {LastShareDiscoveryResult.Source}";
    public string ShareDiscoveryScanScopeText => LastShareDiscoveryResult is null
        ? $"Scope: private IPv4, max {DiagnosticConstants.MaxSmbDiscoveryHosts} hosts"
        : $"Scanned {LastShareDiscoveryResult.ScannedHosts} of max {LastShareDiscoveryResult.MaxHosts} hosts";
    public string ShareTargetCountText
    {
        get
        {
            var count = SplitTargetList(ShareTestTarget).Count();
            return count == 0 ? "No manual target entered." : $"{count} target(s) queued.";
        }
    }
    public string ShareInputStatusText
    {
        get
        {
            return TryValidateTestInput(ScenarioWorkflow.InternalShare, out var message)
                ? string.IsNullOrWhiteSpace(ShareTestTarget)
                    ? "Ready: this run will use detected/profile file server targets."
                    : "Ready: server/share target format is valid."
                : message;
        }
    }
    public string ShareInputSeverity => TryValidateTestInput(ScenarioWorkflow.InternalShare, out _) ? "OK" : "Warning";
    public DomainControllerDiscoveryResult? LastDomainDiscoveryResult
    {
        get => _lastDomainDiscoveryResult;
        private set
        {
            if (SetProperty(ref _lastDomainDiscoveryResult, value))
            {
                RefreshDomainDiscoveryState();
            }
        }
    }

    public bool HasDomainDiscoveryResult => LastDomainDiscoveryResult is not null;
    public bool HasNoDomainDiscoveryResult => !HasDomainDiscoveryResult;
    public bool HasDomainDiscoveryCandidates => LastDomainDiscoveryResult?.Candidates.Count > 0;
    public bool HasNoDomainDiscoveryCandidates => !HasDomainDiscoveryCandidates;
    public string DomainDiscoverySeverity => LastDomainDiscoveryResult?.Severity ?? "Unknown";
    public string DomainDiscoveryStatusText => LastDomainDiscoveryResult is null
        ? "No DNS SRV discovery run in this session."
        : string.IsNullOrWhiteSpace(LastDomainDiscoveryResult.SkippedReason)
            ? LastDomainDiscoveryResult.Verdict
            : LastDomainDiscoveryResult.SkippedReason;
    public string DomainDiscoveryDomainsText => LastDomainDiscoveryResult is { DomainNames.Count: > 0 }
        ? $"Domain hints queried: {string.Join(", ", LastDomainDiscoveryResult.DomainNames)}"
        : "Domain hints queried: none";
    public string DomainControllerCountText
    {
        get
        {
            var count = SplitTargetList(DomainControllerOverride).Count();
            return count == 0 ? "Using detected/profile DC targets." : $"{count} domain controller target(s) queued.";
        }
    }
    public string DomainInputStatusText
    {
        get
        {
            return TryValidateTestInput(ScenarioWorkflow.DnsDomain, out var message)
                ? string.IsNullOrWhiteSpace(DomainControllerOverride)
                    ? "Ready: the run will use detected/profile domain controller targets."
                    : "Ready: domain controller override format is valid."
                : message;
        }
    }
    public string DomainInputSeverity => TryValidateTestInput(ScenarioWorkflow.DnsDomain, out _) ? "OK" : "Warning";
    public ServiceTargetDiscoveryResult? LastServiceDiscoveryResult
    {
        get => _lastServiceDiscoveryResult;
        private set
        {
            if (SetProperty(ref _lastServiceDiscoveryResult, value))
            {
                RefreshServiceDiscoveryState();
            }
        }
    }

    public bool HasServiceDiscoveryResult => LastServiceDiscoveryResult is not null;
    public bool HasNoServiceDiscoveryResult => !HasServiceDiscoveryResult;
    public bool HasServiceDiscoveryCandidates => LastServiceDiscoveryResult?.Candidates.Count > 0;
    public bool HasNoServiceDiscoveryCandidates => !HasServiceDiscoveryCandidates;
    public string ServiceDiscoverySeverity => LastServiceDiscoveryResult?.Severity ?? "Unknown";
    public string ServiceDiscoveryStatusText => LastServiceDiscoveryResult is null
        ? "No service discovery run in this session."
        : string.IsNullOrWhiteSpace(LastServiceDiscoveryResult.SkippedReason)
            ? LastServiceDiscoveryResult.Verdict
            : LastServiceDiscoveryResult.SkippedReason;
    public string ServiceDiscoverySourceText => LastServiceDiscoveryResult is null
        ? "Source: none"
        : string.IsNullOrWhiteSpace(LastServiceDiscoveryResult.Source)
            ? "Source: local adapter detection"
            : $"Source: {LastServiceDiscoveryResult.Source}";
    public string ServiceDiscoveryScanScopeText => LastServiceDiscoveryResult is null
        ? $"Scope: private IPv4, max {DiagnosticConstants.MaxServiceDiscoveryHosts} hosts"
        : $"Scanned {LastServiceDiscoveryResult.ScannedHosts} of max {LastServiceDiscoveryResult.MaxHosts} hosts; TCP {string.Join(", ", LastServiceDiscoveryResult.Ports)}";

    private ScenarioDiagnosisResult? TestScenario => _host.LastTargetedScenario;

    public bool HasTestResult => TestScenario is not null;
    public bool HasNoTestResult => !HasTestResult;
    public string TestResultTitle => TestScenario?.Title ?? string.Empty;
    public string TestResultName => TestScenario?.WorkflowName ?? string.Empty;
    public string TestResultSeverity => TestScenario?.Severity ?? "Unknown";
    public string TestResultOwner => TestScenario?.OwnerSuggestion ?? string.Empty;
    public string TestResultLayer => TestScenario?.AffectedLayer ?? string.Empty;
    public string TestResultConfidence => TestScenario?.Confidence ?? string.Empty;
    public string TestResultInput => TestScenario?.InputSummary ?? string.Empty;
    public string TestResultBaseline => TestScenario?.BaselineSummary ?? string.Empty;
    public string TestResultEmptyText => string.Empty;

    public IReadOnlyList<DiagnosisStepResult> TestSteps =>
        TestScenario?.Steps is { } steps ? steps : Array.Empty<DiagnosisStepResult>();
    public IReadOnlyList<string> TestEvidence =>
        TestScenario?.Evidence is { } evidence ? evidence : Array.Empty<string>();
    public IReadOnlyList<string> TestNextChecks =>
        TestScenario?.NextChecks is { } nextChecks ? nextChecks : Array.Empty<string>();
    public IReadOnlyList<string> TestLimitations =>
        TestScenario?.Limitations is { } limitations ? limitations : Array.Empty<string>();
    public IReadOnlyList<TargetProbeResult> TestTargetResults =>
        TestScenario?.TargetResults is { } targetResults ? targetResults : Array.Empty<TargetProbeResult>();

    public bool HasTestSteps => TestSteps.Count > 0;
    public bool HasNoTestSteps => !HasTestSteps;
    public bool HasTestEvidence => TestEvidence.Count > 0;
    public bool HasNoTestEvidence => !HasTestEvidence;
    public bool HasTestNextChecks => TestNextChecks.Count > 0;
    public bool HasNoTestNextChecks => !HasTestNextChecks;
    public bool HasTestLimitations => TestLimitations.Count > 0;
    public bool HasNoTestLimitations => !HasTestLimitations;
    public bool HasTestTargetResults => TestTargetResults.Count > 0;
    public bool HasNoTestTargetResults => !HasTestTargetResults;
    public string TestTargetResultsEmptyText => HasTestResult
        ? "This targeted test did not produce target rows. The verdict, steps and evidence still describe the result."
        : string.Empty;

    /// <summary>Re-fires every targeted-test-derived property change after the scenario result changes.</summary>
    public void NotifyDiagnosisChanged()
    {
        OnPropertyChanged(nameof(HasTestResult));
        OnPropertyChanged(nameof(HasNoTestResult));
        OnPropertyChanged(nameof(TestResultTitle));
        OnPropertyChanged(nameof(TestResultName));
        OnPropertyChanged(nameof(TestResultSeverity));
        OnPropertyChanged(nameof(TestResultOwner));
        OnPropertyChanged(nameof(TestResultLayer));
        OnPropertyChanged(nameof(TestResultConfidence));
        OnPropertyChanged(nameof(TestResultInput));
        OnPropertyChanged(nameof(TestResultBaseline));
        OnPropertyChanged(nameof(TestResultEmptyText));
        OnPropertyChanged(nameof(TestSteps));
        OnPropertyChanged(nameof(TestEvidence));
        OnPropertyChanged(nameof(TestNextChecks));
        OnPropertyChanged(nameof(TestLimitations));
        OnPropertyChanged(nameof(TestTargetResults));
        OnPropertyChanged(nameof(HasTestSteps));
        OnPropertyChanged(nameof(HasNoTestSteps));
        OnPropertyChanged(nameof(HasTestEvidence));
        OnPropertyChanged(nameof(HasNoTestEvidence));
        OnPropertyChanged(nameof(HasTestNextChecks));
        OnPropertyChanged(nameof(HasNoTestNextChecks));
        OnPropertyChanged(nameof(HasTestLimitations));
        OnPropertyChanged(nameof(HasNoTestLimitations));
        OnPropertyChanged(nameof(HasTestTargetResults));
        OnPropertyChanged(nameof(HasNoTestTargetResults));
        OnPropertyChanged(nameof(TestTargetResultsEmptyText));
        (CopyTargetedTestSummaryCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    /// <summary>Resets targeted-test-owned input fields. Used by the cross-VM "Forget recent targets" flow.</summary>
    public void ResetInputs()
    {
        ShareTestTarget = string.Empty;
        ServiceTestTarget = string.Empty;
        DomainControllerOverride = string.Empty;
    }

    private void RefreshInputState()
    {
        OnPropertyChanged(nameof(CanRunSelectedTest));
        OnPropertyChanged(nameof(SelectedTestInputStatusText));
        OnPropertyChanged(nameof(SelectedTestInputSeverity));
        OnPropertyChanged(nameof(CanScanServiceTargets));
        OnPropertyChanged(nameof(ServicePortInputStatusText));
        OnPropertyChanged(nameof(ServicePortInputSeverity));
        OnPropertyChanged(nameof(SelectedServicePortCountText));
        OnPropertyChanged(nameof(ServicePortLimitText));
        OnPropertyChanged(nameof(ServiceTargetCountText));
        OnPropertyChanged(nameof(ShareTargetCountText));
        OnPropertyChanged(nameof(ShareInputStatusText));
        OnPropertyChanged(nameof(ShareInputSeverity));
        OnPropertyChanged(nameof(DomainControllerCountText));
        OnPropertyChanged(nameof(DomainInputStatusText));
        OnPropertyChanged(nameof(DomainInputSeverity));
        (RunTestCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (RunSelectedTestCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (ScanServiceTargetsCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
    }

    private void RefreshShareDiscoveryState()
    {
        OnPropertyChanged(nameof(HasShareDiscoveryResult));
        OnPropertyChanged(nameof(HasNoShareDiscoveryResult));
        OnPropertyChanged(nameof(HasShareDiscoveryCandidates));
        OnPropertyChanged(nameof(HasNoShareDiscoveryCandidates));
        OnPropertyChanged(nameof(ShareDiscoverySeverity));
        OnPropertyChanged(nameof(ShareDiscoveryStatusText));
        OnPropertyChanged(nameof(ShareDiscoverySourceText));
        OnPropertyChanged(nameof(ShareDiscoveryScanScopeText));
    }

    private void RefreshDomainDiscoveryState()
    {
        OnPropertyChanged(nameof(HasDomainDiscoveryResult));
        OnPropertyChanged(nameof(HasNoDomainDiscoveryResult));
        OnPropertyChanged(nameof(HasDomainDiscoveryCandidates));
        OnPropertyChanged(nameof(HasNoDomainDiscoveryCandidates));
        OnPropertyChanged(nameof(DomainDiscoverySeverity));
        OnPropertyChanged(nameof(DomainDiscoveryStatusText));
        OnPropertyChanged(nameof(DomainDiscoveryDomainsText));
    }

    private void RefreshServiceDiscoveryState()
    {
        OnPropertyChanged(nameof(HasServiceDiscoveryResult));
        OnPropertyChanged(nameof(HasNoServiceDiscoveryResult));
        OnPropertyChanged(nameof(HasServiceDiscoveryCandidates));
        OnPropertyChanged(nameof(HasNoServiceDiscoveryCandidates));
        OnPropertyChanged(nameof(ServiceDiscoverySeverity));
        OnPropertyChanged(nameof(ServiceDiscoveryStatusText));
        OnPropertyChanged(nameof(ServiceDiscoverySourceText));
        OnPropertyChanged(nameof(ServiceDiscoveryScanScopeText));
    }

    private void OnServicePortOptionSelectionChanged(ServicePortOption changedOption)
    {
        if (_syncingServicePortOptions)
        {
            return;
        }

        var selectedOptions = _servicePortOptions
            .Where(option => option.IsSelected)
            .ToList();

        if (selectedOptions.Count > DiagnosticConstants.MaxTargetedTestPorts && changedOption.IsSelected)
        {
            _syncingServicePortOptions = true;
            try
            {
                changedOption.IsSelected = false;
            }
            finally
            {
                _syncingServicePortOptions = false;
            }

            selectedOptions.Remove(changedOption);
            _host.NotifyStatus($"Select {DiagnosticConstants.MaxTargetedTestPorts} or fewer TCP ports in one run.");
        }

        ServiceTestPorts = FormatPortList(selectedOptions.Select(option => option.Port));
    }

    private void SelectServicePorts(IEnumerable<int> ports)
    {
        ServiceTestPorts = FormatPortList(ports
            .Where(port => port is > 0 and <= 65535)
            .Distinct()
            .Take(DiagnosticConstants.MaxTargetedTestPorts));
    }

    private void SyncServicePortOptionsFromText()
    {
        var selectedPorts = SplitPortList(ServiceTestPorts)
            .Select(portText => int.TryParse(portText, out var port) ? port : 0)
            .Where(port => port is > 0 and <= 65535)
            .ToHashSet();

        _syncingServicePortOptions = true;
        try
        {
            foreach (var option in _servicePortOptions)
            {
                option.IsSelected = selectedPorts.Contains(option.Port);
            }
        }
        finally
        {
            _syncingServicePortOptions = false;
        }
    }

    private static string FormatPortList(IEnumerable<int> ports) =>
        string.Join(", ", ports.Distinct().OrderBy(port => port));

    private static List<ServicePortOption> BuildServicePortOptions(Action<ServicePortOption> selectionChanged) =>
    [
        new(80, "HTTP", "Web", "Unencrypted web / proxy service.", true, selectionChanged),
        new(443, "HTTPS", "Web", "Encrypted web / application service.", true, selectionChanged),
        new(8080, "HTTP alt", "Web", "Alternative web or proxy service.", true, selectionChanged),
        new(8443, "HTTPS alt", "Web", "Alternative encrypted web service.", true, selectionChanged),
        new(445, "SMB", "Windows / File", "Windows file share and SMB service.", true, selectionChanged),
        new(3389, "RDP", "Windows / File", "Remote Desktop service.", true, selectionChanged),
        new(5985, "WinRM", "Windows / File", "Windows Remote Management over HTTP.", true, selectionChanged),
        new(5986, "WinRM TLS", "Windows / File", "Windows Remote Management over HTTPS.", true, selectionChanged),
        new(53, "DNS", "Domain / Directory", "DNS lookup service.", true, selectionChanged),
        new(88, "Kerberos", "Domain / Directory", "Active Directory Kerberos authentication.", true, selectionChanged),
        new(389, "LDAP", "Domain / Directory", "Active Directory LDAP.", true, selectionChanged),
        new(636, "LDAPS", "Domain / Directory", "Active Directory LDAP over TLS.", true, selectionChanged),
        new(1433, "SQL Server", "Apps / Database", "Microsoft SQL Server.", false, selectionChanged),
        new(3306, "MySQL", "Apps / Database", "MySQL or MariaDB database.", false, selectionChanged),
        new(5432, "PostgreSQL", "Apps / Database", "PostgreSQL database.", false, selectionChanged),
        new(1521, "Oracle", "Apps / Database", "Oracle database listener.", false, selectionChanged),
        new(25, "SMTP", "Mail / Messaging", "SMTP mail relay.", false, selectionChanged),
        new(587, "SMTP submit", "Mail / Messaging", "Authenticated SMTP submission.", false, selectionChanged),
        new(993, "IMAPS", "Mail / Messaging", "IMAP over TLS.", false, selectionChanged),
        new(9100, "RAW print", "Print / Device", "JetDirect / raw printer service.", false, selectionChanged),
        new(631, "IPP", "Print / Device", "Internet Printing Protocol.", false, selectionChanged),
        new(161, "SNMP", "Print / Device", "SNMP agent query port.", false, selectionChanged),
        new(22, "SSH", "Admin / Network", "SSH remote administration.", false, selectionChanged),
        new(23, "Telnet", "Admin / Network", "Legacy Telnet remote administration.", false, selectionChanged)
    ];

    private static IReadOnlyList<ServicePortGroup> BuildServicePortGroups(IReadOnlyList<ServicePortOption> options) =>
        options
            .GroupBy(option => option.Category)
            .Select(group => new ServicePortGroup(group.Key, group.ToList()))
            .ToList();

    private void RefreshDiscoveryCommandState()
    {
        (ScanShareTargetsCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (DiscoverDomainControllersCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (ScanServiceTargetsCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
    }

    public async Task RunTestAsync(string testKey)
    {
        SelectedTestKey = testKey;
        if (!TryValidateTestInput(testKey, out var validationMessage))
        {
            ShowTestValidationResult(testKey, validationMessage);
            return;
        }

        CancelQuietly(_testCancellation);
        var testCancellation = new CancellationTokenSource();
        _testCancellation = testCancellation;
        var token = testCancellation.Token;

        _host.NotifyBusy(true);
        IsTestRunning = true;
        _host.NotifyStatus($"Running independent targeted checks: {ScenarioWorkflow.GetName(testKey)}...");
        try
        {
            RememberTestTargets(testKey);
            var scenario = await _scenarioEngine.RunIndependentAsync(testKey, _host.NetworkProfile, BuildTestInput(), token);
            token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(_testCancellation, testCancellation))
            {
                return;
            }

            _host.LastTargetedScenario = scenario;
            _host.NotifyStatus(scenario.Title);
            _host.CurrentPage = "TargetedTests";
        }
        catch (OperationCanceledException)
        {
            if (ReferenceEquals(_testCancellation, testCancellation))
            {
                _host.NotifyStatus("Targeted test cancelled.");
            }
        }
        catch (Exception ex)
        {
            _logger.Error("Targeted test command failed.", ex);
            if (ReferenceEquals(_testCancellation, testCancellation))
            {
                _host.NotifyStatus($"Targeted test failed: {ex.Message}");
            }
        }
        finally
        {
            testCancellation.Dispose();
            if (ReferenceEquals(_testCancellation, testCancellation))
            {
                _testCancellation = null;
                IsTestRunning = false;
            }

            _host.NotifyBusy(false);
        }
    }

    public void CancelTest()
    {
        CancelQuietly(_testCancellation);
        _host.NotifyStatus("Stopping targeted test...");
    }

    private static void CancelQuietly(CancellationTokenSource? cancellation)
    {
        try { cancellation?.Cancel(); } catch (ObjectDisposedException) { }
    }

    // ---- Auto-detect (local read-only; the manual textbox stays the primary input) ----

    private void DetectShareTarget()
    {
        var candidates = TargetAutoDetect.LocalShareTargets(_host.NetworkProfile);
        if (ApplyShareCandidates(candidates, out var filledCount))
        {
            _host.NotifyStatus($"Auto-detected {filledCount} local/profile share target(s). No network scan was run.");
        }
        else
        {
            _host.NotifyStatus("No mapped network drives found - enter the server or share manually.");
        }
    }

    private async Task ScanShareTargetsAsync()
    {
        CancelQuietly(_shareDiscoveryCancellation);
        var shareDiscoveryCancellation = new CancellationTokenSource();
        _shareDiscoveryCancellation = shareDiscoveryCancellation;
        var token = shareDiscoveryCancellation.Token;

        _host.NotifyBusy(true);
        IsShareDiscoveryRunning = true;
        _host.NotifyStatus("Scanning local private subnet for SMB hosts on TCP 445...");

        try
        {
            var passiveCandidates = TargetAutoDetect.LocalShareTargets(_host.NetworkProfile);
            var result = await _shareDiscoveryCollector.ScanLocalSubnetAsync(token);
            token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(_shareDiscoveryCancellation, shareDiscoveryCancellation))
            {
                return;
            }

            LastShareDiscoveryResult = result;
            var combinedCandidates = passiveCandidates
                .Concat(result.Candidates)
                .ToList();

            if (ApplyShareCandidates(combinedCandidates, out var filledCount))
            {
                var suffix = combinedCandidates.Count > filledCount
                    ? $" Filled first {filledCount} safe target(s)."
                    : $" Filled {filledCount} target(s).";
                _host.NotifyStatus($"SMB discovery complete. {result.Verdict} Passive candidates: {passiveCandidates.Count}.{suffix}");
            }
            else
            {
                _host.NotifyStatus(string.IsNullOrWhiteSpace(result.SkippedReason)
                    ? result.Verdict
                    : result.SkippedReason);
            }
        }
        catch (OperationCanceledException)
        {
            if (ReferenceEquals(_shareDiscoveryCancellation, shareDiscoveryCancellation))
            {
                _host.NotifyStatus("SMB discovery cancelled.");
            }
        }
        catch (Exception ex)
        {
            _logger.Error("SMB discovery failed.", ex);
            if (ReferenceEquals(_shareDiscoveryCancellation, shareDiscoveryCancellation))
            {
                _host.NotifyStatus($"SMB discovery failed: {ex.Message}");
            }
        }
        finally
        {
            shareDiscoveryCancellation.Dispose();
            if (ReferenceEquals(_shareDiscoveryCancellation, shareDiscoveryCancellation))
            {
                _shareDiscoveryCancellation = null;
                IsShareDiscoveryRunning = false;
            }

            _host.NotifyBusy(false);
        }
    }

    private void CancelShareDiscovery()
    {
        CancelQuietly(_shareDiscoveryCancellation);
        _host.NotifyStatus("Stopping SMB discovery...");
    }

    private void DetectDomainTarget()
    {
        var candidates = TargetAutoDetect.LocalDomainControllerTargets(_host.NetworkProfile);
        if (ApplyDomainControllerCandidates(candidates, out var filledCount))
        {
            _host.NotifyStatus($"Auto-detected {filledCount} local/profile domain controller target(s). No DNS SRV discovery was run.");
        }
        else
        {
            var domain = TargetAutoDetect.DomainName();
            _host.NotifyStatus(domain is null
                ? "This PC is not domain-joined - leave empty or enter a DC manually."
                : $"Domain '{domain}' detected but no logon server in environment - enter a DC manually.");
        }
    }

    private async Task DiscoverDomainControllersAsync()
    {
        CancelQuietly(_domainDiscoveryCancellation);
        var domainDiscoveryCancellation = new CancellationTokenSource();
        _domainDiscoveryCancellation = domainDiscoveryCancellation;
        var token = domainDiscoveryCancellation.Token;

        _host.NotifyBusy(true);
        IsDomainDiscoveryRunning = true;
        _host.NotifyStatus("Discovering domain controllers through AD DNS SRV records...");

        try
        {
            var passiveCandidates = TargetAutoDetect.LocalDomainControllerTargets(_host.NetworkProfile);
            var domainHints = TargetAutoDetect.DomainNameHints(_host.NetworkProfile);
            var result = await _domainDiscoveryCollector.DiscoverAsync(domainHints, token);
            token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(_domainDiscoveryCancellation, domainDiscoveryCancellation))
            {
                return;
            }

            LastDomainDiscoveryResult = result;
            var combinedCandidates = passiveCandidates
                .Concat(result.Candidates)
                .ToList();

            if (ApplyDomainControllerCandidates(combinedCandidates, out var filledCount))
            {
                var suffix = combinedCandidates.Count > filledCount
                    ? $" Filled first {filledCount} safe target(s)."
                    : $" Filled {filledCount} target(s).";
                _host.NotifyStatus($"DC discovery complete. {result.Verdict} Passive candidates: {passiveCandidates.Count}.{suffix}");
            }
            else
            {
                _host.NotifyStatus(string.IsNullOrWhiteSpace(result.SkippedReason)
                    ? result.Verdict
                    : result.SkippedReason);
            }
        }
        catch (OperationCanceledException)
        {
            if (ReferenceEquals(_domainDiscoveryCancellation, domainDiscoveryCancellation))
            {
                _host.NotifyStatus("DC discovery cancelled.");
            }
        }
        catch (Exception ex)
        {
            _logger.Error("DC discovery failed.", ex);
            if (ReferenceEquals(_domainDiscoveryCancellation, domainDiscoveryCancellation))
            {
                _host.NotifyStatus($"DC discovery failed: {ex.Message}");
            }
        }
        finally
        {
            domainDiscoveryCancellation.Dispose();
            if (ReferenceEquals(_domainDiscoveryCancellation, domainDiscoveryCancellation))
            {
                _domainDiscoveryCancellation = null;
                IsDomainDiscoveryRunning = false;
            }

            _host.NotifyBusy(false);
        }
    }

    private void CancelDomainDiscovery()
    {
        CancelQuietly(_domainDiscoveryCancellation);
        _host.NotifyStatus("Stopping DC discovery...");
    }

    private void DetectServiceTarget()
    {
        var candidates = TargetAutoDetect.LocalServiceTargets(_host.NetworkProfile);
        if (ApplyServiceCandidates(candidates, out var filledCount))
        {
            _host.NotifyStatus($"Auto-detected {filledCount} local/profile service target(s). No port scan was run.");
        }
        else
        {
            _host.NotifyStatus("Could not auto-detect a local host - enter the service target manually.");
        }
    }

    private async Task ScanServiceTargetsAsync()
    {
        if (!TryValidatePorts(ServiceTestPorts, out var validationMessage))
        {
            _host.NotifyStatus(validationMessage);
            return;
        }

        CancelQuietly(_serviceDiscoveryCancellation);
        var serviceDiscoveryCancellation = new CancellationTokenSource();
        _serviceDiscoveryCancellation = serviceDiscoveryCancellation;
        var token = serviceDiscoveryCancellation.Token;
        var ports = ParseValidatedPorts(ServiceTestPorts);

        _host.NotifyBusy(true);
        IsServiceDiscoveryRunning = true;
        _host.NotifyStatus($"Scanning local private subnet for TCP service ports: {string.Join(", ", ports)}...");

        try
        {
            var passiveCandidates = TargetAutoDetect.LocalServiceTargets(_host.NetworkProfile);
            var result = await _serviceDiscoveryCollector.ScanLocalSubnetAsync(ports, token);
            token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(_serviceDiscoveryCancellation, serviceDiscoveryCancellation))
            {
                return;
            }

            var combinedCandidates = passiveCandidates
                .Concat(result.Candidates)
                .ToList();
            LastServiceDiscoveryResult = result;

            if (ApplyServiceCandidates(combinedCandidates, out var filledCount))
            {
                var suffix = combinedCandidates.Count > filledCount
                    ? $" Filled first {filledCount} safe target(s)."
                    : $" Filled {filledCount} target(s).";
                _host.NotifyStatus($"Service discovery complete. {result.Verdict} Passive candidates: {passiveCandidates.Count}.{suffix}");
            }
            else
            {
                _host.NotifyStatus(string.IsNullOrWhiteSpace(result.SkippedReason)
                    ? result.Verdict
                    : result.SkippedReason);
            }
        }
        catch (OperationCanceledException)
        {
            if (ReferenceEquals(_serviceDiscoveryCancellation, serviceDiscoveryCancellation))
            {
                _host.NotifyStatus("Service discovery cancelled.");
            }
        }
        catch (Exception ex)
        {
            _logger.Error("Service discovery failed.", ex);
            if (ReferenceEquals(_serviceDiscoveryCancellation, serviceDiscoveryCancellation))
            {
                _host.NotifyStatus($"Service discovery failed: {ex.Message}");
            }
        }
        finally
        {
            serviceDiscoveryCancellation.Dispose();
            if (ReferenceEquals(_serviceDiscoveryCancellation, serviceDiscoveryCancellation))
            {
                _serviceDiscoveryCancellation = null;
                IsServiceDiscoveryRunning = false;
            }

            _host.NotifyBusy(false);
        }
    }

    private void CancelServiceDiscovery()
    {
        CancelQuietly(_serviceDiscoveryCancellation);
        _host.NotifyStatus("Stopping service discovery...");
    }

    private WorkflowInput BuildTestInput() => new()
    {
        ShareTarget = ShareTestTarget.Trim(),
        DomainControllerOverride = DomainControllerOverride.Trim(),
        ServiceTarget = ServiceTestTarget.Trim(),
        ServicePorts = ServiceTestPorts.Trim()
    };

    private void RememberTestTargets(string testKey)
    {
        if (testKey == ScenarioWorkflow.InternalShare)
            _host.RememberShareTarget(ShareTestTarget);
        if (testKey == ScenarioWorkflow.DnsDomain)
            _host.SetDomainControllerOverride(DomainControllerOverride.Trim());
        if (testKey == ScenarioWorkflow.ServiceAccess)
        {
            foreach (var target in SplitTargetList(ServiceTestTarget))
            {
                _host.RememberServiceTarget(target);
            }
        }
    }

    private bool TryValidateTestInput(string testKey, out string message)
    {
        message = string.Empty;
        if (testKey == ScenarioWorkflow.InternalShare &&
            string.IsNullOrWhiteSpace(ShareTestTarget) &&
            _host.NetworkProfile.FileServers.Count == 0)
        {
            message = "Enter a server or share target before running Internal Server or Share Access.";
            return false;
        }

        if (testKey == ScenarioWorkflow.InternalShare &&
            !string.IsNullOrWhiteSpace(ShareTestTarget) &&
            !TryValidateTargetList(ShareTestTarget, allowShare: true, out message))
        {
            return false;
        }

        if (testKey == ScenarioWorkflow.DnsDomain &&
            !string.IsNullOrWhiteSpace(DomainControllerOverride) &&
            !TryValidateTargetList(DomainControllerOverride, allowShare: false, out message))
        {
            return false;
        }

        if (testKey == ScenarioWorkflow.ServiceAccess)
        {
            if (string.IsNullOrWhiteSpace(ServiceTestTarget))
            {
                message = "Enter a service target before running Service Access.";
                return false;
            }

            if (!TryValidateTargetList(ServiceTestTarget, allowShare: false, out message))
            {
                return false;
            }

            if (!TryValidatePorts(ServiceTestPorts, out message))
            {
                return false;
            }
        }

        return true;
    }

    private void ShowTestValidationResult(string testKey, string message)
    {
        var scenario = new ScenarioDiagnosisResult
        {
            WorkflowKey = testKey,
            WorkflowName = ScenarioWorkflow.GetName(testKey),
            Title = message,
            Severity = "Warning",
            Confidence = "High",
            AffectedLayer = "Configuration",
            OwnerSuggestion = "Local Support",
            InputSummary = BuildTestInputSummary(testKey),
            BaselineSummary = "Independent targeted test did not run because required input is missing.",
            CompletedAt = DateTimeOffset.Now,
            NextChecks = [message],
            Limitations = ["No network tests were run for this targeted-test attempt."]
        };

        _host.LastTargetedScenario = scenario;

        _host.NotifyStatus(message);
        _host.CurrentPage = "TargetedTests";
        NotifyDiagnosisChanged();
    }

    private string BuildTestInputSummary(string testKey) => testKey switch
    {
        ScenarioWorkflow.InternalShare => string.IsNullOrWhiteSpace(ShareTestTarget) ? "No share/server target entered." : $"Share/server target: {ShareTestTarget.Trim()}",
        ScenarioWorkflow.DnsDomain => string.IsNullOrWhiteSpace(DomainControllerOverride) ? "Using detected/profile domain controller targets." : $"Domain controller override: {DomainControllerOverride.Trim()}",
        ScenarioWorkflow.ServiceAccess => $"Service target: {ValueOrNotEntered(ServiceTestTarget)}; ports: {ValueOrNotEntered(ServiceTestPorts)}",
        _ => "No manual target required."
    };

    private static string ValueOrNotEntered(string value) =>
        string.IsNullOrWhiteSpace(value) ? "not entered" : value.Trim();

    private static bool TryValidateTargetList(string targetsText, bool allowShare, out string message)
    {
        message = string.Empty;
        if (!TryValidateInputLength(targetsText, "Target list", out message))
        {
            return false;
        }

        var targets = SplitTargetList(targetsText).ToList();
        if (targets.Count == 0)
        {
            message = "Enter at least one target.";
            return false;
        }

        if (targets.Count > DiagnosticConstants.MaxTargetedTestTargets)
        {
            message = $"Enter {DiagnosticConstants.MaxTargetedTestTargets} or fewer targets in one run.";
            return false;
        }

        foreach (var target in targets)
        {
            string reason;
            var isValid = allowShare
                ? DiagnosticTargetValidator.TryNormalizeHostOrShare(target, out _, out reason)
                : DiagnosticTargetValidator.TryNormalizeHost(target, out _, out reason);

            if (!isValid)
            {
                message = $"Invalid target '{target}': {reason}";
                return false;
            }
        }

        return true;
    }

    private static bool TryValidatePorts(string portsText, out string message)
    {
        message = string.Empty;
        if (!TryValidateInputLength(portsText, "Port list", out message))
        {
            return false;
        }

        var ports = SplitPortList(portsText).ToList();
        if (ports.Count == 0)
        {
            message = "Enter at least one valid TCP port between 1 and 65535 before running Service Access.";
            return false;
        }

        if (ports.Count > DiagnosticConstants.MaxTargetedTestPorts)
        {
            message = $"Enter {DiagnosticConstants.MaxTargetedTestPorts} or fewer TCP ports in one run.";
            return false;
        }

        foreach (var portText in ports)
        {
            if (!portText.All(char.IsDigit) ||
                !int.TryParse(portText, out var port) ||
                port is < 1 or > 65535)
            {
                message = $"Invalid TCP port '{portText}'. Use numbers between 1 and 65535.";
                return false;
            }
        }

        return true;
    }

    private string GetPortValidationMessage()
    {
        TryValidatePorts(ServiceTestPorts, out var message);
        return message;
    }

    private static bool TryValidateInputLength(string value, string label, out string message)
    {
        message = string.Empty;
        if (value.Length <= DiagnosticConstants.MaxTargetedTestInputLength)
        {
            return true;
        }

        message = $"{label} must be {DiagnosticConstants.MaxTargetedTestInputLength} characters or less.";
        return false;
    }

    private static IEnumerable<string> SplitTargetList(string targetsText) =>
        targetsText.Split([';', ',', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static IEnumerable<string> SplitPortList(string portsText) =>
        portsText.Split([';', ',', ' ', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static IReadOnlyList<int> ParseValidatedPorts(string portsText) =>
        SplitPortList(portsText)
            .Select(portText => int.TryParse(portText, out var port) ? port : 0)
            .Where(port => port is > 0 and <= 65535)
            .Distinct()
            .Take(DiagnosticConstants.MaxTargetedTestPorts)
            .ToList();

    private bool ApplyShareCandidates(IEnumerable<ShareTargetCandidate> candidates, out int filledCount)
    {
        var targets = candidates
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate.Target))
            .Select(candidate => candidate.Target.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(DiagnosticConstants.MaxTargetedTestTargets)
            .ToList();

        filledCount = targets.Count;
        if (targets.Count == 0)
        {
            return false;
        }

        ShareTestTarget = string.Join(", ", targets);
        return true;
    }

    private bool ApplyDomainControllerCandidates(IEnumerable<DomainControllerCandidate> candidates, out int filledCount)
    {
        var targets = candidates
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate.Host))
            .Select(candidate => candidate.Host.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(DiagnosticConstants.MaxTargetedTestTargets)
            .ToList();

        filledCount = targets.Count;
        if (targets.Count == 0)
        {
            return false;
        }

        DomainControllerOverride = string.Join(", ", targets);
        return true;
    }

    private bool ApplyServiceCandidates(IEnumerable<ServiceTargetCandidate> candidates, out int filledCount)
    {
        var targets = candidates
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate.Host))
            .Select(candidate => candidate.Host.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(DiagnosticConstants.MaxTargetedTestTargets)
            .ToList();

        filledCount = targets.Count;
        if (targets.Count == 0)
        {
            return false;
        }

        ServiceTestTarget = string.Join(", ", targets);
        return true;
    }

    private void CopyTargetedTestSummary()
    {
        var summary = BuildTargetedTestTicketSummary(DateTimeOffset.Now);
        if (string.IsNullOrWhiteSpace(summary))
        {
            _host.NotifyStatus("Nothing to copy yet - run a targeted test first.");
            return;
        }

        try
        {
            System.Windows.Clipboard.SetText(summary);
            _host.NotifyStatus("Targeted test summary copied to clipboard.");
        }
        catch (Exception ex)
        {
            _logger.Error("Targeted test clipboard copy failed.", ex);
            _host.NotifyStatus($"Copy failed: {ex.Message}");
        }
    }

    public string BuildTargetedTestTicketSummary(DateTimeOffset timestamp)
    {
        var scenario = TestScenario;
        if (scenario is null)
        {
            return string.Empty;
        }

        var sb = new StringBuilder(2048);
        sb.AppendLine($"ISG Desk - Targeted Test Summary ({timestamp:yyyy-MM-dd HH:mm:ss})");
        sb.AppendLine(new string('-', 60));
        sb.AppendLine($"Workflow  : {ValueOrUnknown(scenario.WorkflowName)}");
        sb.AppendLine($"Verdict   : {ValueOrUnknown(scenario.Title)} [{ValueOrUnknown(scenario.Severity)}]");
        sb.AppendLine($"Layer     : {ValueOrUnknown(scenario.AffectedLayer)}");
        sb.AppendLine($"Owner     : {ValueOrUnknown(scenario.OwnerSuggestion)}");
        sb.AppendLine($"Confidence: {ValueOrUnknown(scenario.Confidence)}");
        sb.AppendLine($"Input     : {ValueOrUnknown(scenario.InputSummary)}");
        sb.AppendLine($"Baseline  : {ValueOrUnknown(scenario.BaselineSummary)}");

        if (scenario.Steps.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("STEPS");
            foreach (var step in scenario.Steps)
            {
                sb.AppendLine($"- {step.Name}: {step.Status} ({step.Layer}, {step.DurationDisplayText})");
                if (!string.IsNullOrWhiteSpace(step.Recommendation))
                {
                    sb.AppendLine($"  Recommendation: {step.Recommendation}");
                }

                if (!string.IsNullOrWhiteSpace(step.Error))
                {
                    sb.AppendLine($"  Error: {step.Error}");
                }
            }
        }

        if (scenario.TargetResults.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("TARGET RESULTS");
            foreach (var target in scenario.TargetResults)
            {
                sb.AppendLine($"- {ValueOrUnknown(target.Target.Host)} -> {target.Verdict}");
                sb.AppendLine($"  DNS {target.DnsStatus}; ping {target.PingStatus}; latency {target.LatencyText}; loss {target.LossText}; ports {target.PortSummary}; share {target.ShareStatus}; owner {target.OwnerSuggestion}");
                if (!string.IsNullOrWhiteSpace(target.ShareDetails))
                {
                    sb.AppendLine($"  Share details: {target.ShareDetails}");
                }
            }
        }

        AppendTextSection(sb, "EVIDENCE", scenario.Evidence);
        AppendTextSection(sb, "NEXT CHECKS", scenario.NextChecks);
        AppendTextSection(sb, "LIMITATIONS", scenario.Limitations);
        return sb.ToString();
    }

    private static void AppendTextSection(StringBuilder sb, string title, IReadOnlyList<string> items)
    {
        if (items.Count == 0)
        {
            return;
        }

        sb.AppendLine();
        sb.AppendLine(title);
        foreach (var item in items)
        {
            sb.AppendLine($"- {item}");
        }
    }

    private static string ValueOrUnknown(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "Unknown" : value.Trim();
}
