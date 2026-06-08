using System.Windows.Input;
using NetScopeDiagnosticCenter.Collectors;
using NetScopeDiagnosticCenter.Core;
using NetScopeDiagnosticCenter.Core.Models;
using NetScopeDiagnosticCenter.Core.Monitoring;
using NetScopeDiagnosticCenter.Infrastructure;
using NetScopeDiagnosticCenter.Reports;
using NetScopeDiagnosticCenter.UI.ViewModels;

namespace NetScopeDiagnosticCenter.UI;

public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private const string DefaultPageKey = "TechnicianHome";

    private static readonly Dictionary<string, string> ValidPageKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        [DefaultPageKey] = DefaultPageKey,
        ["Diagnosis"] = "Diagnosis",
        ["TargetedTests"] = "TargetedTests",
        ["Printers"] = "Printers",
        ["NetworkDevices"] = "NetworkDevices",
        ["Wifi"] = "Wifi",
        ["Reports"] = "Reports",
    };

    private readonly AppStorageService _appStorage;
    private readonly LoggingService _loggingService;
    private readonly ActivityFeedService _activityFeedService;

    private string _currentPage = DefaultPageKey;
    private bool _isBusy;
    private int _busyOperationCount;
    private string _statusMessage = "Ready.";
    private NetworkDiagnosisResult? _lastDiagnosis;
    private ScenarioDiagnosisResult? _lastTargetedScenario;
    private bool _disposed;

    /// <summary>
    /// Always-on gateway ping used by the sidebar status panel. Started in the ctor against
    /// the auto-detected default gateway; can be re-targeted via <see cref="StatusMonitor"/>.
    /// </summary>
    public IMonitoringSession StatusMonitor { get; }

    public MainViewModel(
        DiagnosticEngine diagnosticEngine,
        PortTestCollector portTestCollector,
        WifiCollector wifiCollector,
        ScenarioEngine scenarioEngine,
        TargetShareDiscoveryCollector targetShareDiscoveryCollector,
        TargetServiceDiscoveryCollector targetServiceDiscoveryCollector,
        DomainControllerDiscoveryCollector domainControllerDiscoveryCollector,
        LocalPrinterCollector localPrinterCollector,
        PrintServerCollector printServerCollector,
        PrinterDiscoveryCollector printerDiscoveryCollector,
        SnmpPrinterCollector snmpPrinterCollector,
        PrinterQueueInstaller printerQueueInstaller,
        NetworkDeviceCollector networkDeviceCollector,
        ReportStorageService reportStorageService,
        SnmpCredentialStore snmpCredentialStore,
        RuleEngine ruleEngine,
        HealthScoreCalculator healthScoreCalculator,
        AppStorageService appStorage,
        LoggingService loggingService,
        ActivityFeedService activityFeedService,
        IMonitoringSessionFactory monitoringSessionFactory,
        ISystemOverviewCollector systemOverviewCollector,
        WifiAnalyzerViewModel wifiAnalyzer)
    {
        WifiAnalyzer = wifiAnalyzer ?? throw new ArgumentNullException(nameof(wifiAnalyzer));
        _appStorage = appStorage;
        _loggingService = loggingService;
        _activityFeedService = activityFeedService;

        // Phase 3 — sidebar live status: 2-second interval gateway ping.
        // The session auto-starts against the detected default gateway. If detection fails
        // we still create the session — the user can reassign target later.
        StatusMonitor = monitoringSessionFactory.Create(intervalMs: 2000, maxSamples: 60);
        var initialGateway = GatewayDetector.DetectDefaultGateway();
        if (!string.IsNullOrWhiteSpace(initialGateway))
        {
            _ = StatusMonitor.StartAsync(initialGateway);
        }

        // Phase 2c migration: ActivityViewModel owns the live activity feed.
        Activity = new ActivityViewModel(activityFeedService, this);

        // Internal profile data remains available for diagnostics/reports, but the
        // Settings module/ViewModel has been removed from the product surface.
        LoadNetworkProfile();

        // Phase 2c migration: ReportsViewModel owns the report-generation logic.
        Reports = new ReportsViewModel(this, reportStorageService, wifiCollector, _loggingService);

        _recentTargets = _appStorage.LoadRecentTargets();

        // Phase 2c migration: PrintersViewModel owns printer-specific state and SNMP credentials.
        // It loads saved SNMPv3 credentials in its own ctor.
        Printers = new PrintersViewModel(
            printerDiscoveryCollector,
            localPrinterCollector,
            printServerCollector,
            snmpPrinterCollector,
            printerQueueInstaller,
            snmpCredentialStore,
            _loggingService,
            this,
            initialPrinterTarget: _recentTargets.PrinterTargets.FirstOrDefault() ?? string.Empty,
            initialPrintServer: _recentTargets.LastPrintServer);

        // Phase 2c migration: NetworkDevicesViewModel owns SNMP identification + LAN scan state.
        NetworkDevices = new NetworkDevicesViewModel(networkDeviceCollector, _loggingService, this, snmpCredentialStore);

        // Phase 2d migration: DiagnosisViewModel owns Quick Diagnosis + manual port test.
        Diagnosis = new DiagnosisViewModel(diagnosticEngine, portTestCollector, healthScoreCalculator, ruleEngine, _loggingService, this);

        // Phase 2d migration: TargetedTestsViewModel owns scenario runners + targeted-test result projection.
        TargetedTests = new TargetedTestsViewModel(
            scenarioEngine,
            targetShareDiscoveryCollector,
            targetServiceDiscoveryCollector,
            domainControllerDiscoveryCollector,
            _loggingService,
            this,
            initialShareTarget: _recentTargets.ServerTargets.FirstOrDefault() ?? string.Empty,
            initialServiceTarget: _recentTargets.ServiceTargets.FirstOrDefault() ?? string.Empty,
            initialDomainControllerOverride: _recentTargets.DomainControllerOverride);

        // Phase 2d migration: LinkQualityViewModel owns deep ping + path diagnostics.
        LinkQuality = new LinkQualityViewModel(_loggingService, this);

        // Technician Home: read-only local system overview + last-diagnosis summary.
        // Built last because IHost reads LastDiagnosis owned by this VM.
        TechnicianHome = new TechnicianHomeViewModel(this, systemOverviewCollector, activityFeedService);

        // Phase 2c migration: XAML still binds against MainViewModel via the shim properties.
        // Sub-VMs raise PropertyChanged on their own instances; we forward those events here so
        // bindings on MainViewModel refresh whenever a sub-VM property (or a derived computed
        // property like IsSnmpV3Selected) changes. Removed in 2d when XAML binds directly to
        // sub-VMs through DataTemplates.
        Activity.PropertyChanged += (_, e) => OnPropertyChanged(e.PropertyName);
        Reports.PropertyChanged += (_, e) => OnPropertyChanged(e.PropertyName);
        Printers.PropertyChanged += (_, e) =>
        {
            OnPropertyChanged(e.PropertyName);
            if (e.PropertyName == nameof(PrintersViewModel.LastPrinterDiscovery))
            {
                RefreshTechnicianHomeStatus();
            }
        };
        NetworkDevices.PropertyChanged += (_, e) =>
        {
            OnPropertyChanged(e.PropertyName);
            if (e.PropertyName is nameof(NetworkDevicesViewModel.LastNetworkDevice)
                or nameof(NetworkDevicesViewModel.LastNetworkDeviceScan))
            {
                RefreshTechnicianHomeStatus();
            }
        };
        Diagnosis.PropertyChanged += (_, e) => OnPropertyChanged(e.PropertyName);
        TargetedTests.PropertyChanged += (_, e) => OnPropertyChanged(e.PropertyName);
        LinkQuality.PropertyChanged += (_, e) => OnPropertyChanged(e.PropertyName);
        TechnicianHome.PropertyChanged += (_, e) => OnPropertyChanged(e.PropertyName);
        // Default landing page → load the local snapshot now (local-only, non-blocking).
        _ = TechnicianHome.EnsureLoadedAsync();
        WifiAnalyzer.PropertyChanged += (_, e) =>
        {
            OnPropertyChanged(e.PropertyName);
            if (e.PropertyName == nameof(WifiAnalyzerViewModel.Health))
            {
                RefreshTechnicianHomeStatus();
            }
        };
        // StatusMonitor: WPF resolves nested property paths automatically — bindings on
        // {Binding StatusMonitor.LastStatus} refresh from the session's own PropertyChanged.

        NavigateCommand = new RelayCommand(parameter => CurrentPage = parameter?.ToString() ?? DefaultPageKey);
        // Diagnosis commands delegate to DiagnosisViewModel via expression-bodied props in MainViewModel.Diagnosis.cs.
        // Link Quality commands delegate to LinkQualityViewModel via expression-bodied props in MainViewModel.LinkQuality.cs.
        // Targeted-test commands delegate to TargetedTestsViewModel via expression-bodied props in MainViewModel.TargetedTests.cs.
        // Printer + NetworkDevice commands delegate to their VMs via expression-bodied
        // props in MainViewModel.Printers.cs / MainViewModel.NetworkDevices.cs.
        ForgetRecentTargetsCommand = new RelayCommand(_ => ForgetRecentTargets());
        CancelCurrentOperationCommand = new RelayCommand(_ => CancelCurrentOperation());
        // GenerateReportCommand / CopySummaryCommand / GenerateWlanReportCommand
        // are exposed as expression-bodied delegates in MainViewModel.Reports.cs.
        // ClearActivityFeedCommand delegates to ActivityViewModel below.
    }

    public ICommand NavigateCommand { get; }
    public ICommand CancelCurrentOperationCommand { get; }
    // Diagnosis commands live in MainViewModel.Diagnosis.cs (delegate to DiagnosisViewModel).
    // Link Quality commands live in MainViewModel.LinkQuality.cs (delegate to LinkQualityViewModel).
    // Targeted-test commands live in MainViewModel.TargetedTests.cs (delegate to TargetedTestsViewModel).
    // Printer commands live in MainViewModel.Printers.cs (delegate to PrintersViewModel).
    // Network device commands live in MainViewModel.NetworkDevices.cs (delegate to NetworkDevicesViewModel).
    // CancelPrinterScanCommand lives in MainViewModel.Printers.cs.
    // CancelNetworkDeviceScanCommand lives in MainViewModel.NetworkDevices.cs.
    public ICommand ForgetRecentTargetsCommand { get; }
    // GenerateReportCommand / CopySummaryCommand / GenerateWlanReportCommand
    // live in MainViewModel.Reports.cs (delegate to the dedicated ReportsViewModel).
    // ClearActivityFeedCommand delegates to ActivityViewModel.
    public ICommand ClearActivityFeedCommand => Activity.ClearActivityFeedCommand;

    public IReadOnlyList<int> CommonPorts => DiagnosticConstants.CommonPorts;
    // TestDefinitions lives in MainViewModel.TargetedTests.cs (delegate to TargetedTestsViewModel).

    public string CurrentPage
    {
        get => _currentPage;
        set
        {
            value = NormalizePageKey(value);
            var previous = _currentPage;
            if (!SetProperty(ref _currentPage, value)) return;

            // Page-name display surface - top header bar shows the active module's friendly name.
            OnPropertyChanged(nameof(CurrentPageDisplayName));
            OnPropertyChanged(nameof(CurrentPageSubtitle));
            OnPropertyChanged(nameof(CurrentPageIcon));
            OnPropertyChanged(nameof(CurrentPageAccentStart));
            OnPropertyChanged(nameof(CurrentPageAccentEnd));

            // Wi-Fi page lifecycle — start 1 Hz sampler on entry, stop on exit so we don't
            // burn CPU + WLAN handle reads when the user is on another module.
            if (string.Equals(value, "Wifi", StringComparison.OrdinalIgnoreCase))
            {
                _ = WifiAnalyzer.ActivateAsync(CancellationToken.None);
            }
            else if (string.Equals(previous, "Wifi", StringComparison.OrdinalIgnoreCase))
            {
                WifiAnalyzer.Deactivate();
            }

            // Technician Home: load the read-only local snapshot once on first entry.
            // Strictly local (no network), so safe to trigger on navigation.
            if (string.Equals(value, "TechnicianHome", StringComparison.OrdinalIgnoreCase))
            {
                _ = TechnicianHome.EnsureLoadedAsync();
            }
        }
    }

    /// <summary>
    /// Friendly display name for the active module — bound by the custom title bar so the user
    /// always sees which module they are on. Maps the internal nav-key (CurrentPage) to the
    /// human label shown on each NavButton.
    /// </summary>
    public string CurrentPageDisplayName => _currentPage switch
    {
        "TechnicianHome" => "Technician Home",
        "Diagnosis" => "Diagnosis",
        "TargetedTests" => "Targeted Tests",
        "Printers" => "Printers",
        "NetworkDevices" => "Network Devices",
        "Wifi" => "Wi-Fi Analyzer",
        "Reports" => "Report Center",
        _ => "ISG Desk",
    };

    /// <summary>Short subtitle shown under the module title in the 3D card header.</summary>
    public string CurrentPageSubtitle => _currentPage switch
    {
        "TechnicianHome" => "Local system overview - read-only",
        "Diagnosis" => "Quick diagnosis, deep ping and port test",
        "TargetedTests" => "Targeted connectivity tests",
        "Printers" => "Discover & install printers",
        "NetworkDevices" => "SNMP device inspection",
        "Wifi" => "Wireless signal & nearby networks",
        "Reports" => "Export reports for tickets",
        _ => "IT Support & Network Diagnostics",
    };

    public string CurrentPageIcon => _currentPage switch
    {
        "TechnicianHome" => "\uE80F",
        "Diagnosis" => "\uE9D9",
        "TargetedTests" => "\uE8F1",
        "Printers" => "\uE749",
        "NetworkDevices" => "\uE968",
        "Wifi" => "\uE701",
        "Reports" => "\uE8A5",
        _ => "\uE946",
    };

    public string CurrentPageAccentStart => _currentPage switch
    {
        "TechnicianHome" => "#3B82F6",
        "Diagnosis" => "#14B8A6",
        "TargetedTests" => "#D97706",
        "Printers" => "#A855F7",
        "NetworkDevices" => "#10B981",
        "Wifi" => "#0EA5E9",
        "Reports" => "#E11D48",
        _ => "#3B82F6",
    };

    public string CurrentPageAccentEnd => _currentPage switch
    {
        "TechnicianHome" => "#2563EB",
        "Diagnosis" => "#0F766E",
        "TargetedTests" => "#B45309",
        "Printers" => "#9333EA",
        "NetworkDevices" => "#047857",
        "Wifi" => "#0369A1",
        "Reports" => "#BE123C",
        _ => "#2563EB",
    };

    /// <summary>
    /// Wi-Fi Analyzer sub-VM. Wired via DI; activated by <see cref="CurrentPage"/> setter
    /// when the user navigates to the Wi-Fi page so the live sampler runs only when visible.
    /// </summary>
    public WifiAnalyzerViewModel WifiAnalyzer { get; }

    public bool IsBusy
    {
        get => _isBusy;
        set => SetProperty(ref _isBusy, value);
    }

    private void SetBusyState(bool busy)
    {
        if (busy)
        {
            _busyOperationCount++;
        }
        else if (_busyOperationCount > 0)
        {
            _busyOperationCount--;
        }

        IsBusy = _busyOperationCount > 0;
    }

    public string StatusMessage
    {
        get => _statusMessage;
        set => SetProperty(ref _statusMessage, value);
    }

    public NetworkDiagnosisResult? LastDiagnosis
    {
        get => _lastDiagnosis;
        set
        {
            if (SetProperty(ref _lastDiagnosis, value))
            {
                NotifyDiagnosisSurfaceChanged();
                Reports?.NotifyDiagnosisChanged();
                RefreshLinkQualityFromLastDiagnosis();
                RefreshTechnicianHomeStatus();
            }
        }
    }

    public ScenarioDiagnosisResult? LastTargetedScenario
    {
        get => _lastTargetedScenario;
        set
        {
            if (SetProperty(ref _lastTargetedScenario, value))
            {
                RefreshTargetedTestsResultStatus();
                RefreshNavigationSeverityStatus();
            }
        }
    }

    public bool HasDiagnosis => LastDiagnosis is not null;
    public bool HasNoDiagnosis => !HasDiagnosis;
    public bool HasDiagnosticSteps => LastDiagnosis?.Steps.Count > 0;
    public bool HasNoDiagnosticSteps => !HasDiagnosticSteps;
    public bool HasVerdictEvidence => LastDiagnosis?.Verdict.Evidence.Count > 0;
    public bool HasNoVerdictEvidence => !HasVerdictEvidence;
    public bool HasVerdictLimitations => LastDiagnosis?.Verdict.Limitations.Count > 0;
    public bool HasNoVerdictLimitations => !HasVerdictLimitations;
    public bool HasVerdictRecommendations => LastDiagnosis?.Verdict.Recommendations.Count > 0;
    public bool HasNoVerdictRecommendations => !HasVerdictRecommendations;
    public bool HasCollectorWarnings => LastDiagnosis?.CollectorWarnings.Count > 0;
    public bool HasNoCollectorWarnings => !HasCollectorWarnings;
    public bool HasHealthPenalties => LastDiagnosis?.HealthScore.Penalties.Count > 0;
    public bool HasNoHealthPenalties => !HasHealthPenalties;
    public bool HasPortTest => LastDiagnosis?.LastPortTest is not null;
    public bool HasNoPortTest => !HasPortTest;
    public string DiagnosisNextActionTitle => LastDiagnosis?.Verdict.Severity switch
    {
        "Critical" => "Immediate action",
        "Warning" => "Recommended next check",
        "OK" => "Baseline looks usable",
        _ => "Ready to collect baseline"
    };

    public string DiagnosisProblemText => LastDiagnosis?.Verdict.Title ?? "No Quick Diagnosis result yet.";
    public string DiagnosisEvidenceText => FirstNonEmpty(
        LastDiagnosis?.Verdict.Evidence,
        LastDiagnosis?.Steps.SelectMany(step => step.Evidence),
        "Run Quick Diagnosis to collect adapter, IP, gateway, DNS, internet and PC context evidence.");

    public string DiagnosisRecommendedActionText => FirstNonEmpty(
        LastDiagnosis?.Verdict.Recommendations,
        LastDiagnosis is null
            ? new[] { "Run Diagnose This PC to establish the local baseline." }
            : LastDiagnosis.Verdict.Severity == "OK"
                ? new[] { "If the user still reports an issue, continue with Deep Ping & Path or Manual Port Test against the affected service." }
                : new[] { "Review the diagnostic steps and run a focused test against the affected service or path." },
        "Review the diagnostic steps and run a focused test against the affected service or path.");

    private void NotifyDiagnosisSurfaceChanged()
    {
        OnPropertyChanged(nameof(LastDiagnosis));
        OnPropertyChanged(nameof(HasDiagnosis));
        OnPropertyChanged(nameof(HasNoDiagnosis));
        OnPropertyChanged(nameof(HasDiagnosticSteps));
        OnPropertyChanged(nameof(HasNoDiagnosticSteps));
        OnPropertyChanged(nameof(HasVerdictEvidence));
        OnPropertyChanged(nameof(HasNoVerdictEvidence));
        OnPropertyChanged(nameof(HasVerdictLimitations));
        OnPropertyChanged(nameof(HasNoVerdictLimitations));
        OnPropertyChanged(nameof(HasVerdictRecommendations));
        OnPropertyChanged(nameof(HasNoVerdictRecommendations));
        OnPropertyChanged(nameof(HasCollectorWarnings));
        OnPropertyChanged(nameof(HasNoCollectorWarnings));
        OnPropertyChanged(nameof(HasHealthPenalties));
        OnPropertyChanged(nameof(HasNoHealthPenalties));
        OnPropertyChanged(nameof(HasPortTest));
        OnPropertyChanged(nameof(HasNoPortTest));
        OnPropertyChanged(nameof(DiagnosisNextActionTitle));
        OnPropertyChanged(nameof(DiagnosisProblemText));
        OnPropertyChanged(nameof(DiagnosisEvidenceText));
        OnPropertyChanged(nameof(DiagnosisRecommendedActionText));
        OnPropertyChanged(nameof(SummaryText));
        Diagnosis?.NotifyDiagnosisContextChanged();
    }

    private static string FirstNonEmpty(IEnumerable<string>? primary, IEnumerable<string>? secondary, string fallback)
    {
        var value = primary?.FirstOrDefault(item => !string.IsNullOrWhiteSpace(item))
            ?? secondary?.FirstOrDefault(item => !string.IsNullOrWhiteSpace(item));

        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }

    private static string FirstNonEmpty(IEnumerable<string>? primary, string fallback) =>
        FirstNonEmpty(primary, null, fallback);

    // ----- Header context (read once at startup; cheap, never changes during a session) -----

    /// <summary>NetBIOS / hostname of the current PC. Shown in the top header for at-a-glance "which machine am I on".</summary>
    public string MachineName { get; } = Environment.MachineName;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        StatusMonitor.Stop();
        if (StatusMonitor is IDisposable disposableMonitor)
        {
            disposableMonitor.Dispose();
        }
    }

    /// <summary>Currently logged-in Windows user. Domain prefix is dropped for compactness.</summary>
    public string UserName { get; } = StripDomain(Environment.UserName);

    /// <summary>True when the app is running elevated (admin token). Determined once at construction.</summary>
    public bool IsRunningAsAdmin { get; } = DetectAdmin();

    /// <summary>"Admin" / "Standard" label for the header badge.</summary>
    public string AdminBadgeText => IsRunningAsAdmin ? "Admin" : "Standard";

    /// <summary>Inverse of <see cref="IsRunningAsAdmin"/> for visibility binding of the "Standard" badge.</summary>
    public bool IsNotAdmin => !IsRunningAsAdmin;

    private static bool DetectAdmin()
    {
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            return new System.Security.Principal.WindowsPrincipal(identity)
                .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    private static string StripDomain(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return raw;
        var slash = raw.LastIndexOf('\\');
        return slash >= 0 && slash + 1 < raw.Length ? raw[(slash + 1)..] : raw;
    }

    private static string NormalizePageKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return DefaultPageKey;
        }

        var trimmed = value.Trim();
        return ValidPageKeys.TryGetValue(trimmed, out var canonical)
            ? canonical
            : DefaultPageKey;
    }

    private static string MaskCommunity(string community) =>
        string.IsNullOrWhiteSpace(community) ? "(empty)" : "********";

    private static string FirstNonEmpty(params string[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? string.Empty;

    private static void RememberValue(List<string> values, string value)
    {
        var normalized = value.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
            return;
        values.RemoveAll(item => item.Equals(normalized, StringComparison.OrdinalIgnoreCase));
        values.Insert(0, normalized);
        if (values.Count > 10)
            values.RemoveRange(10, values.Count - 10);
    }
}
