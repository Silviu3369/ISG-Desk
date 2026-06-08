using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using NetScopeDiagnosticCenter.Collectors.Wifi;
using NetScopeDiagnosticCenter.Core.Models;
using NetScopeDiagnosticCenter.Core.Models.Wifi;
using NetScopeDiagnosticCenter.Core.Wifi;
using NetScopeDiagnosticCenter.Infrastructure;

namespace NetScopeDiagnosticCenter.UI.ViewModels;

/// <summary>
/// Wi-Fi Analyzer module ViewModel — orchestrates the 12 UI sections.
///
/// <para>
/// Responsibilities:
/// <list type="bullet">
///   <item>Hold the current Wi-Fi state as discrete bindable properties (connection + visible APs + channel map + capabilities + health).</item>
///   <item>Maintain bounded rolling buffers for live sparklines (60 RSSI samples + 60 speed samples).</item>
///   <item>Maintain a bounded roaming-events history (last 50 events).</item>
///   <item>Expose commands: ForceScan, RefreshConnection, DetectIsp, ClearHistory.</item>
///   <item>Subscribe to <see cref="WifiSampler"/> events at 1 Hz, marshal updates onto the UI thread,
///         and run roaming detection on each new connection snapshot.</item>
/// </list>
/// </para>
///
/// <para>
/// Lifecycle: created via DI as singleton (one Wi-Fi page in the app). The sampler is started
/// when the user navigates to the Wi-Fi page and stopped on navigation away. Initial probe
/// (capabilities + first scan) happens on first navigation.
/// </para>
///
/// <para>
/// Thread safety: sampler raises events on a thread-pool thread; the VM marshals updates
/// onto the captured <see cref="SynchronizationContext"/> (UI thread) before touching the
/// ObservableCollections. Cross-thread WPF binding violations are thus impossible by construction.
/// </para>
/// </summary>
public sealed class WifiAnalyzerViewModel : ObservableObject, IDisposable
{
    /// <summary>Maximum samples held in the RSSI sparkline buffer (60 seconds at 1 Hz).</summary>
    public const int SparklineBufferSize = 60;

    /// <summary>Maximum roaming events retained for the history section.</summary>
    public const int RoamingHistorySize = 50;

    /// <summary>Maximum active scan channel snapshots retained for before-after comparison.</summary>
    public const int ChannelHistorySize = 24;

    private const string DeviceInventoryFilterAll = "All";
    private const string DeviceInventoryFilterConfirmed = "Confirmed";
    private const string DeviceInventoryFilterLikely = "Likely";
    private const string DeviceInventoryFilterNeedsName = "Needs name";
    private const string DeviceInventoryFilterPhonesTvIot = "Phones/TV/IoT";

    private readonly IWifiScanner _scanner;
    private readonly WifiSampler _sampler;
    private readonly WifiCapabilityProbe _capabilityProbe;
    private readonly WifiPublicIpProbe _publicIpProbe;
    private readonly WifiLanScanner _lanScanner;
    private readonly WifiRouterClientCollector _routerClientCollector;
    private readonly WifiDeviceFriendlyNameStore _friendlyNameStore;
    private readonly WifiDeviceTypeOverrideStore _deviceTypeStore;
    private readonly SnmpCredentialStore _snmpCredentialStore;
    private readonly WifiAnalyzerEngine _engine;
    private readonly WifiHistoryStore _history;
    private readonly ILoggingService _logger;
    private readonly SynchronizationContext _uiContext;

    private Guid? _adapterId;
    private CancellationTokenSource? _scanCancellation;
    private CancellationTokenSource? _ispCancellation;
    private CancellationTokenSource? _lanCancellation;
    private CancellationTokenSource? _routerClientCancellation;
    private bool _isInitialized;
    private bool _disposed;
    // Last connection seen by the 1 Hz sampler — the baseline for real-time roam detection.
    private WifiConnectionDetails? _lastSampledConnection;

    /// <summary>
    /// Per-BSSID running stats kept for the whole session (avg RSSI + detection count
    /// + first/last seen). Mirrors NirSoft WirelessNetView's "Average Signal" + "Detection %"
    /// columns. Keyed by BSSID (case-insensitive uppercase hex like "AA:BB:CC:DD:EE:FF").
    /// </summary>
    private readonly Dictionary<string, BssidStats> _bssidStats = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>BSSIDs the user un-checked in the Signal-Over-Time legend — survives rescans.</summary>
    private readonly HashSet<string> _trendHidden = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Total number of scans performed this session — denominator for Detection %.</summary>
    private int _totalScans;

    /// <summary>Periodic auto-scan timer — fires every 30 s while user is on the Wi-Fi page.</summary>
    private System.Threading.Timer? _autoScanTimer;

    // --- Backing fields for bindable state ---
    private WifiConnectionDetails _connection;
    private WifiAdapterCapabilities _capabilities;
    private WifiHealthReport _health;
    private WifiSecurityAuditReport _securityAudit;
    private WifiChannelRecommendation _channelRecommendation;
    private WifiChannelBeforeAfterSummary _channelBeforeAfterSummary;
    private WifiSavedProfileHygieneReport _savedProfileHygiene;
    private string _bestChannelAdvice = string.Empty;
    private WifiSavedProfileHygieneItem? _selectedSavedProfileHygieneItem;
    private bool _confirmForgetSavedProfile;
    private bool _isForgettingSavedProfile;
    private string _savedProfileForgetStatus = "Select a saved profile and confirm before forgetting it.";
    private bool _isAutoDetectingWirelessDevices;
    private DateTimeOffset? _lastScanAt;
    private string _status = "Ready";
    private bool _isScanning;
    private bool _isDetectingIsp;
    private int? _linkScore;
    private IReadOnlyDictionary<string, string> _friendlyNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyDictionary<string, string> _manualDeviceTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, WifiDevicePresence> _devicePresence = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<WifiDeviceInventoryItem> _wirelessInventorySnapshot = Array.Empty<WifiDeviceInventoryItem>();
    private string _deviceInventoryFilter = DeviceInventoryFilterAll;
    private string _linkScoreVerdict = "—";

    public WifiAnalyzerViewModel(
        IWifiScanner scanner,
        WifiSampler sampler,
        WifiCapabilityProbe capabilityProbe,
        WifiPublicIpProbe publicIpProbe,
        WifiLanScanner lanScanner,
        WifiRouterClientCollector routerClientCollector,
        WifiDeviceFriendlyNameStore friendlyNameStore,
        WifiDeviceTypeOverrideStore deviceTypeStore,
        SnmpCredentialStore snmpCredentialStore,
        WifiAnalyzerEngine engine,
        WifiHistoryStore history,
        ILoggingService logger)
    {
        _scanner = scanner ?? throw new ArgumentNullException(nameof(scanner));
        _sampler = sampler ?? throw new ArgumentNullException(nameof(sampler));
        _capabilityProbe = capabilityProbe ?? throw new ArgumentNullException(nameof(capabilityProbe));
        _publicIpProbe = publicIpProbe ?? throw new ArgumentNullException(nameof(publicIpProbe));
        _lanScanner = lanScanner ?? throw new ArgumentNullException(nameof(lanScanner));
        _routerClientCollector = routerClientCollector ?? throw new ArgumentNullException(nameof(routerClientCollector));
        _friendlyNameStore = friendlyNameStore ?? throw new ArgumentNullException(nameof(friendlyNameStore));
        _deviceTypeStore = deviceTypeStore ?? throw new ArgumentNullException(nameof(deviceTypeStore));
        _snmpCredentialStore = snmpCredentialStore ?? throw new ArgumentNullException(nameof(snmpCredentialStore));
        _snmpCredentialStore.PropertyChanged += (_, e) => NotifyRouterSnmpCredentialChanged(e.PropertyName);
        _snmpCredentialStore.Load();
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _history = history ?? throw new ArgumentNullException(nameof(history));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        // Captured at construction time so off-thread sampler callbacks can marshal back to UI.
        _uiContext = SynchronizationContext.Current ?? new SynchronizationContext();

        _connection = WifiConnectionDetails.Disconnected(Infrastructure.Wlan.WlanInterfaceState.NotReady);
        _capabilities = WifiAdapterCapabilities.Unknown(Guid.Empty, "Unknown adapter");
        _health = WifiHealthReport.NoData();
        _securityAudit = WifiSecurityAuditReport.NoData();
        _channelRecommendation = WifiChannelRecommendation.NoData();
        _channelBeforeAfterSummary = WifiChannelBeforeAfterSummary.NoData();
        _savedProfileHygiene = WifiSavedProfileHygieneReport.NoData();

        VisibleAccessPoints = new ObservableCollection<WifiAccessPoint>();
        FilteredAccessPoints = new ObservableCollection<WifiAccessPoint>();
        ChannelMap = new ObservableCollection<WifiChannelAnalysis>();
        ChannelHistory = new ObservableCollection<WifiChannelHistoryEntry>();
        SavedProfiles = new ObservableCollection<WifiSavedProfile>();
        SignalSamples = new ObservableCollection<WifiSignalSample>();
        SpeedSamples = new ObservableCollection<WifiSpeedSample>();
        RoamingHistory = new ObservableCollection<WifiRoamingEvent>();
        SignalTrends = new ObservableCollection<WifiSignalTrend>();
        LanDevices = new ObservableCollection<WifiLanDevice>();
        RouterClients = new ObservableCollection<WifiRouterClient>();
        DeviceInventory = new ObservableCollection<WifiDeviceInventoryItem>();
        _friendlyNames = _friendlyNameStore.Load();
        _manualDeviceTypes = _deviceTypeStore.Load();

        AutoDetectWirelessDevicesCommand = new AsyncRelayCommand(_ => AutoDetectWirelessDevicesAsync(), _ => CanAutoDetectWirelessDevices);
        ScanLanDevicesCommand = new AsyncRelayCommand(_ => ScanLanDevicesAsync(), _ => !IsLanScanning && !IsAutoDetectingWirelessDevices);
        ReadRouterClientsCommand = new AsyncRelayCommand(_ => ReadRouterClientsAsync(), _ => !IsRouterClientScanning && !IsAutoDetectingWirelessDevices);
        SetDeviceInventoryFilterCommand = new RelayCommand(parameter => DeviceInventoryFilter = parameter?.ToString() ?? DeviceInventoryFilterAll);
        ClearDeviceSessionCommand = new RelayCommand(_ => ClearDeviceSession());
        SaveDeviceFriendlyNameCommand = new RelayCommand(_ => SaveSelectedDeviceFriendlyName(), _ => SelectedDeviceInventoryItem is not null);
        ClearDeviceFriendlyNameCommand = new RelayCommand(_ => ClearSelectedDeviceFriendlyName(), _ => SelectedDeviceInventoryItem is not null);
        SaveDeviceTypeCommand = new RelayCommand(_ => SaveSelectedDeviceType(), _ => SelectedDeviceInventoryItem is not null);
        ClearDeviceTypeCommand = new RelayCommand(_ => ClearSelectedDeviceType(), _ => SelectedDeviceInventoryItem is not null);
        ForgetRouterSnmpCredentialCommand = new RelayCommand(_ => ForgetRouterSnmpCredential());
        ForgetSelectedSavedProfileCommand = new AsyncRelayCommand(_ => ForgetSelectedSavedProfileAsync(), _ => CanForgetSelectedSavedProfile);
        ExportReportCommand = new RelayCommand(_ => ExportReport(), _ => _adapterId is not null);
        CopyReportCommand = new RelayCommand(_ => CopyReport(), _ => _adapterId is not null);
        OpenHistoryCommand = new RelayCommand(_ => OpenHistoryFolder());
        StartMonitoringCommand = new AsyncRelayCommand(_ => StartMonitoringAsync(),
            _ => !IsMonitoring && _adapterId is not null);
        StopMonitoringCommand = new RelayCommand(_ => StopMonitoring(), _ => IsMonitoring);
        ScanNetworksCommand = new AsyncRelayCommand(_ => ScanNetworksAsync(),
            _ => IsMonitoring && !IsScanning && _adapterId is not null);
        RefreshConnectionCommand = new AsyncRelayCommand(_ => RefreshConnectionAsync(),
            _ => _adapterId is not null && !IsScanning);
        DetectIspCommand = new AsyncRelayCommand(_ => DetectIspAsync(), _ => Connection.IsConnected && !IsDetectingIsp);
        ClearRoamingHistoryCommand = new RelayCommand(_ => RoamingHistory.Clear());
        ClearNetworkFilterCommand = new RelayCommand(_ => NetworkFilter = string.Empty);

        // Sampler events: marshal back to UI thread before touching ObservableCollections.
        _sampler.SignalSampled += OnSignalSampled;
        _sampler.SpeedSampled += OnSpeedSampled;
        _sampler.ConnectionSampled += OnConnectionSampled;
    }

    // ============== Bindable properties ==============

    public WifiConnectionDetails Connection
    {
        get => _connection;
        private set
        {
            if (SetProperty(ref _connection, value))
            {
                (DetectIspCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                OnPropertyChanged(nameof(ConnectionStateLabel));
                OnPropertyChanged(nameof(ConnectionStateColor));
                OnPropertyChanged(nameof(ConnectionCapturedText));
                OnPropertyChanged(nameof(ConnectionBssidText));
                OnPropertyChanged(nameof(ConnectionVendorText));
                OnPropertyChanged(nameof(ConnectionSecurityText));
                OnPropertyChanged(nameof(HiddenSsidText));
                OnPropertyChanged(nameof(LiveSignalValueText));
                OnPropertyChanged(nameof(LiveLinkSpeedValueText));
                OnPropertyChanged(nameof(LinkSpeedRxText));
                OnPropertyChanged(nameof(LinkSpeedTxText));
                OnPropertyChanged(nameof(LinkSpeedStateText));
                OnPropertyChanged(nameof(LinkSpeedStateColor));
                OnPropertyChanged(nameof(LinkSpeedDeltaText));
                OnPropertyChanged(nameof(SignalStrengthLabel));
                OnPropertyChanged(nameof(SignalStrengthColor));
                OnPropertyChanged(nameof(SignalMarginText));
                OnPropertyChanged(nameof(SignalAlertStatusText));
            }
        }
    }

    public string ConnectionStateLabel => Connection.IsConnected
        ? "Connected"
        : Connection.InterfaceState switch
        {
            Infrastructure.Wlan.WlanInterfaceState.NotReady => "Adapter not ready",
            Infrastructure.Wlan.WlanInterfaceState.Disconnected => "Not connected",
            Infrastructure.Wlan.WlanInterfaceState.Disconnecting => "Disconnecting",
            Infrastructure.Wlan.WlanInterfaceState.Associating => "Associating",
            Infrastructure.Wlan.WlanInterfaceState.Authenticating => "Authenticating",
            Infrastructure.Wlan.WlanInterfaceState.Discovering => "Discovering",
            _ => Connection.InterfaceState.ToString()
        };

    public string ConnectionStateColor => Connection.IsConnected
        ? "#10B981"
        : Connection.InterfaceState switch
        {
            Infrastructure.Wlan.WlanInterfaceState.NotReady => "#94A3B8",
            Infrastructure.Wlan.WlanInterfaceState.Disconnected => "#F59E0B",
            Infrastructure.Wlan.WlanInterfaceState.Disconnecting => "#F59E0B",
            Infrastructure.Wlan.WlanInterfaceState.Associating => "#3B82F6",
            Infrastructure.Wlan.WlanInterfaceState.Authenticating => "#3B82F6",
            Infrastructure.Wlan.WlanInterfaceState.Discovering => "#3B82F6",
            _ => "#94A3B8"
        };

    public string ConnectionCapturedText => $"Snapshot {Connection.CapturedAt:HH:mm:ss}";

    public string ConnectionBssidText => string.IsNullOrWhiteSpace(Connection.Bssid)
        ? "Not connected"
        : Connection.Bssid;

    public string ConnectionVendorText => string.IsNullOrWhiteSpace(Connection.Vendor)
        ? "Vendor unknown"
        : Connection.Vendor;

    public string ConnectionSecurityText => Connection.Security?.Label
        ?? (Connection.IsConnected ? "Unknown security" : "Not connected");

    public string HiddenSsidText => Connection.IsConnected
        ? (Connection.IsHiddenSsid ? "Yes" : "No")
        : "Not connected";

    public bool HasLiveSignalSamples => SignalSamples.Count > 0;
    public bool HasLiveSpeedSamples => SpeedSamples.Count > 0;

    public string LiveSignalValueText => Connection.RssiDbm is { } rssi
        ? $"{rssi} dBm"
        : IsMonitoring ? "Sampling..." : "Idle";

    public string SignalStrengthLabel => Connection.RssiDbm is null
        ? (IsMonitoring ? "Waiting for RSSI" : "Idle")
        : Connection.SignalLevel.ToString();

    public string SignalStrengthColor => Connection.SignalLevel switch
    {
        WifiSignalLevel.Excellent => "#10B981",
        WifiSignalLevel.Good => "#3B82F6",
        WifiSignalLevel.Fair => "#F59E0B",
        WifiSignalLevel.Poor => "#EF4444",
        _ => "#94A3B8",
    };

    public string SignalMarginText
    {
        get
        {
            if (!Connection.IsConnected)
            {
                return "No active Wi-Fi connection";
            }

            if (Connection.RssiDbm is not { } rssi)
            {
                return IsMonitoring ? "Waiting for live RSSI sample" : "No live RSSI sample";
            }

            var delta = rssi - _signalAlertThresholdDbm;
            return delta >= 0
                ? $"{delta} dB above alert threshold"
                : $"{Math.Abs(delta)} dB below alert threshold";
        }
    }

    public string LiveLinkSpeedValueText =>
        Connection.RxRateMbps is { } rx && Connection.TxRateMbps is { } tx
            ? $"{rx:N0} / {tx:N0} Mbps"
            : IsMonitoring ? "Sampling..." : "Idle";

    public string LinkSpeedRxText => FormatPhyRate(Connection.RxRateMbps);

    public string LinkSpeedTxText => FormatPhyRate(Connection.TxRateMbps);

    public string LinkSpeedStateText
    {
        get
        {
            if (!Connection.IsConnected)
            {
                return "Not connected";
            }

            return Connection.RxRateMbps is not null && Connection.TxRateMbps is not null
                ? "PHY rate available"
                : IsMonitoring ? "Sampling PHY rate" : "No PHY sample";
        }
    }

    public string LinkSpeedStateColor => !Connection.IsConnected
        ? "#94A3B8"
        : Connection.RxRateMbps is not null && Connection.TxRateMbps is not null
            ? "#10B981"
            : "#F59E0B";

    public string LinkSpeedDeltaText
    {
        get
        {
            if (!Connection.IsConnected)
            {
                return "Connect to Wi-Fi to read negotiated PHY rate.";
            }

            if (Connection.RxRateMbps is not { } rx || Connection.TxRateMbps is not { } tx)
            {
                return IsMonitoring ? "Waiting for live link-rate sample." : "Start Analyzer to sample live link rate.";
            }

            var delta = Math.Abs(rx - tx);
            if (delta < 0.5)
            {
                return "Rx and Tx PHY rates are balanced.";
            }

            var side = rx > tx ? "Rx" : "Tx";
            return $"{side} PHY rate is {delta:N0} Mbps higher.";
        }
    }

    public string LiveThroughputValueText
    {
        get
        {
            if (SpeedSamples.Count == 0)
            {
                return IsMonitoring ? "Waiting for traffic sample" : "Idle";
            }

            var latest = SpeedSamples[SpeedSamples.Count - 1];
            return $"Rx {FormatThroughput(latest.RxThroughputMbps)} / Tx {FormatThroughput(latest.TxThroughputMbps)}";
        }
    }

    public string ReceiveThroughputNowText => SpeedSamples.Count == 0
        ? (IsMonitoring ? "Waiting for sample" : "Idle")
        : FormatThroughput(SpeedSamples[SpeedSamples.Count - 1].RxThroughputMbps);

    public string TransferThroughputNowText => SpeedSamples.Count == 0
        ? (IsMonitoring ? "Waiting for sample" : "Idle")
        : FormatThroughput(SpeedSamples[SpeedSamples.Count - 1].TxThroughputMbps);

    public string ReceiveThroughputStatsText => BuildThroughputDirectionStats(isTx: false);

    public string TransferThroughputStatsText => BuildThroughputDirectionStats(isTx: true);

    public string ReceiveThroughputStateText => BuildThroughputDirectionState(isTx: false);

    public string TransferThroughputStateText => BuildThroughputDirectionState(isTx: true);

    public string ReceiveThroughputStateColor => BuildThroughputDirectionColor(isTx: false);

    public string TransferThroughputStateColor => BuildThroughputDirectionColor(isTx: true);

    public string SignalSampleCountText => SignalSamples.Count == 0
        ? (IsMonitoring ? "Waiting for RSSI sample" : "No live samples")
        : $"{SignalSamples.Count}/{SparklineBufferSize} samples";

    public string SpeedSampleCountText => SpeedSamples.Count == 0
        ? (IsMonitoring ? "Waiting for speed sample" : "No live samples")
        : $"{SpeedSamples.Count}/{SparklineBufferSize} samples";

    public WifiAdapterCapabilities Capabilities
    {
        get => _capabilities;
        private set => SetProperty(ref _capabilities, value);
    }

    public WifiHealthReport Health
    {
        get => _health;
        private set
        {
            if (SetProperty(ref _health, value))
            {
                OnPropertyChanged(nameof(HasRecommendations));
                OnPropertyChanged(nameof(NoRecommendations));
            }
        }
    }

    /// <summary>True when the engine produced at least one actionable recommendation.</summary>
    public bool HasRecommendations => _health.Recommendations is { Count: > 0 };

    /// <summary>Inverse — drives the "✓ Wi-Fi is healthy" empty state.</summary>
    public bool NoRecommendations => !HasRecommendations && _health.Score is not null;

    public WifiSecurityAuditReport SecurityAudit
    {
        get => _securityAudit;
        private set
        {
            if (SetProperty(ref _securityAudit, value))
            {
                OnPropertyChanged(nameof(HasSecurityAuditFindings));
                OnPropertyChanged(nameof(NoSecurityAuditFindings));
                OnPropertyChanged(nameof(SecurityAuditSummary));
            }
        }
    }

    public bool HasSecurityAuditFindings => _securityAudit.Findings.Count > 0;
    public bool NoSecurityAuditFindings => !HasSecurityAuditFindings;
    public string SecurityAuditSummary => _securityAudit.Summary;

    public WifiChannelRecommendation ChannelRecommendation
    {
        get => _channelRecommendation;
        private set
        {
            if (SetProperty(ref _channelRecommendation, value))
            {
                OnPropertyChanged(nameof(HasChannelRecommendation));
            }
        }
    }

    public bool HasChannelRecommendation => _channelRecommendation.IsReady;

    public WifiChannelBeforeAfterSummary ChannelBeforeAfterSummary
    {
        get => _channelBeforeAfterSummary;
        private set
        {
            if (SetProperty(ref _channelBeforeAfterSummary, value))
            {
                OnPropertyChanged(nameof(HasChannelBeforeAfterSummary));
            }
        }
    }

    public bool HasChannelBeforeAfterSummary => _channelBeforeAfterSummary.IsReady;

    /// <summary>
    /// inSSIDer-style "use this channel" advice computed from the latest channel map.
    /// Empty until the first scan. Shown prominently in the Channel Map card.
    /// </summary>
    public string BestChannelAdvice
    {
        get => _bestChannelAdvice;
        private set
        {
            if (SetProperty(ref _bestChannelAdvice, value))
            {
                OnPropertyChanged(nameof(HasBestChannelAdvice));
            }
        }
    }

    public bool HasBestChannelAdvice => !string.IsNullOrEmpty(_bestChannelAdvice);

    /// <summary>
    /// Every AP from the latest scan. Feeds the channel graphs + exported report — always
    /// the complete set, never filtered. The Visible Networks grid binds to
    /// <see cref="FilteredAccessPoints"/> instead so search doesn't hide data elsewhere.
    /// </summary>
    public ObservableCollection<WifiAccessPoint> VisibleAccessPoints { get; }

    /// <summary>
    /// <see cref="VisibleAccessPoints"/> narrowed by <see cref="NetworkFilter"/>. Bound to the
    /// Visible Networks DataGrid. Equals the full list when the search box is empty.
    /// </summary>
    public ObservableCollection<WifiAccessPoint> FilteredAccessPoints { get; }

    public ObservableCollection<WifiChannelAnalysis> ChannelMap { get; }
    public ObservableCollection<WifiChannelHistoryEntry> ChannelHistory { get; }
    public bool HasChannelHistory => ChannelHistory.Count > 0;
    public string ChannelHistorySummary => ChannelHistory.Count == 1
        ? "1 channel snapshot this session"
        : $"{ChannelHistory.Count} channel snapshots this session";

    public ObservableCollection<WifiSavedProfile> SavedProfiles { get; }

    public WifiSavedProfileHygieneReport SavedProfileHygiene
    {
        get => _savedProfileHygiene;
        private set
        {
            if (SetProperty(ref _savedProfileHygiene, value))
            {
                OnPropertyChanged(nameof(HasSavedProfileHygieneItems));
                OnPropertyChanged(nameof(SavedProfileHygieneSummary));
            }
        }
    }

    public bool HasSavedProfileHygieneItems => _savedProfileHygiene.HasItems;
    public string SavedProfileHygieneSummary => _savedProfileHygiene.Summary;

    public WifiSavedProfileHygieneItem? SelectedSavedProfileHygieneItem
    {
        get => _selectedSavedProfileHygieneItem;
        set
        {
            if (SetProperty(ref _selectedSavedProfileHygieneItem, value))
            {
                ConfirmForgetSavedProfile = false;
                SavedProfileForgetStatus = value is null
                    ? "Select a saved profile and confirm before forgetting it."
                    : IsActiveSavedProfile(value.ProfileName)
                        ? "Active Wi-Fi profile cannot be forgotten while this PC is connected to it."
                        : $"Selected profile: {value.ProfileDisplay}. Confirm before forgetting.";
                OnPropertyChanged(nameof(SelectedSavedProfileName));
                OnPropertyChanged(nameof(IsSelectedSavedProfileActive));
                OnPropertyChanged(nameof(CanForgetSelectedSavedProfile));
                (ForgetSelectedSavedProfileCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            }
        }
    }

    public string SelectedSavedProfileName => SelectedSavedProfileHygieneItem?.ProfileDisplay ?? "No profile selected";

    public bool IsSelectedSavedProfileActive =>
        SelectedSavedProfileHygieneItem is not null
        && IsActiveSavedProfile(SelectedSavedProfileHygieneItem.ProfileName);

    public bool ConfirmForgetSavedProfile
    {
        get => _confirmForgetSavedProfile;
        set
        {
            if (SetProperty(ref _confirmForgetSavedProfile, value))
            {
                OnPropertyChanged(nameof(CanForgetSelectedSavedProfile));
                (ForgetSelectedSavedProfileCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsForgettingSavedProfile
    {
        get => _isForgettingSavedProfile;
        private set
        {
            if (SetProperty(ref _isForgettingSavedProfile, value))
            {
                OnPropertyChanged(nameof(CanForgetSelectedSavedProfile));
                (ForgetSelectedSavedProfileCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            }
        }
    }

    public string SavedProfileForgetStatus
    {
        get => _savedProfileForgetStatus;
        private set => SetProperty(ref _savedProfileForgetStatus, value);
    }

    public bool CanForgetSelectedSavedProfile =>
        _adapterId is not null
        && !_isForgettingSavedProfile
        && _confirmForgetSavedProfile
        && SelectedSavedProfileHygieneItem is not null
        && !IsSelectedSavedProfileActive;

    private string _savedProfilesSummary = "Start Analyzer to load saved Wi-Fi profiles.";
    public string SavedProfilesSummary
    {
        get => _savedProfilesSummary;
        private set => SetProperty(ref _savedProfilesSummary, value);
    }

    public bool HasSavedProfiles => SavedProfiles.Count > 0;
    public bool NoSavedProfiles => !HasSavedProfiles;

    private bool _savedProfilesLoaded;
    public string SavedProfilesEmptyText => _savedProfilesLoaded
        ? "No saved Wi-Fi profiles found on this adapter."
        : "Start Analyzer to load saved Wi-Fi profiles.";
    public ObservableCollection<WifiSignalSample> SignalSamples { get; }
    public ObservableCollection<WifiSpeedSample> SpeedSamples { get; }
    public ObservableCollection<WifiRoamingEvent> RoamingHistory { get; }

    private int _sessionRoamCount;
    private string _roamingSummary = "No roaming this session.";
    /// <summary>
    /// Live one-liner above the roaming table: how many AP hand-offs happened this
    /// monitoring session + when the last one was and whether it improved signal.
    /// </summary>
    public string RoamingSummary
    {
        get => _roamingSummary;
        private set => SetProperty(ref _roamingSummary, value);
    }

    /// <summary>Top-8 networks' RSSI history across scans — multi-line signal-over-time graph.</summary>
    public ObservableCollection<WifiSignalTrend> SignalTrends { get; }

    /// <summary>Devices found on the local subnet (ping sweep + ARP + reverse DNS + vendor).</summary>
    public ObservableCollection<WifiLanDevice> LanDevices { get; }

    /// <summary>Clients reported directly by the router/AP through read-only management data.</summary>
    public ObservableCollection<WifiRouterClient> RouterClients { get; }

    /// <summary>Unified operator-facing inventory merged from local and router/AP sources.</summary>
    public ObservableCollection<WifiDeviceInventoryItem> DeviceInventory { get; }
    public IReadOnlyList<string> DeviceInventoryFilterOptions { get; } =
    [
        DeviceInventoryFilterAll,
        DeviceInventoryFilterConfirmed,
        DeviceInventoryFilterLikely,
        DeviceInventoryFilterNeedsName,
        DeviceInventoryFilterPhonesTvIot
    ];

    public string DeviceInventoryFilter
    {
        get => _deviceInventoryFilter;
        set
        {
            var normalized = NormalizeDeviceInventoryFilter(value);
            if (!SetProperty(ref _deviceInventoryFilter, normalized))
            {
                return;
            }

            ApplyDeviceInventoryFilterToUi();
        }
    }

    public string DeviceInventorySummary => _wirelessInventorySnapshot.Count == 0
        ? "No wireless clients confirmed or likely yet. Use Auto Detect, then Read Router/AP where supported for AP/client evidence."
        : DeviceInventoryFilter == DeviceInventoryFilterAll
            ? $"{DeviceInventory.Count} wireless candidate(s) shown from confirmed/likely evidence."
            : $"{DeviceInventory.Count} of {_wirelessInventorySnapshot.Count} wireless candidate(s) shown - filter: {DeviceInventoryFilter}.";

    public string DeviceInventoryFilterSummary => _wirelessInventorySnapshot.Count == 0
        ? "Filters are ready. No wireless candidate evidence has been collected in this session."
        : DeviceInventoryFilter == DeviceInventoryFilterAll
            ? "Filter: All wireless candidates."
            : $"Filter: {DeviceInventoryFilter} - showing {DeviceInventory.Count} of {_wirelessInventorySnapshot.Count}.";

    public bool IsAutoDetectingWirelessDevices
    {
        get => _isAutoDetectingWirelessDevices;
        private set
        {
            if (SetProperty(ref _isAutoDetectingWirelessDevices, value))
            {
                OnPropertyChanged(nameof(CanAutoDetectWirelessDevices));
                (AutoDetectWirelessDevicesCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                (ScanLanDevicesCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                (ReadRouterClientsCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            }
        }
    }

    public bool CanAutoDetectWirelessDevices =>
        !IsAutoDetectingWirelessDevices && !IsLanScanning && !IsRouterClientScanning;

    private WifiDeviceInventoryItem? _selectedDeviceInventoryItem;
    public WifiDeviceInventoryItem? SelectedDeviceInventoryItem
    {
        get => _selectedDeviceInventoryItem;
        set
        {
            if (!SetProperty(ref _selectedDeviceInventoryItem, value))
            {
                return;
            }

            ManualDeviceName = value?.ManualName ?? value?.LearnedName ?? string.Empty;
            ManualDeviceType = value?.ManualDeviceType ?? string.Empty;
            OnPropertyChanged(nameof(SelectedDeviceSummary));
            (SaveDeviceFriendlyNameCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (ClearDeviceFriendlyNameCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (SaveDeviceTypeCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (ClearDeviceTypeCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    private string _manualDeviceName = string.Empty;
    public string ManualDeviceName
    {
        get => _manualDeviceName;
        set => SetProperty(ref _manualDeviceName, value ?? string.Empty);
    }

    public IReadOnlyList<string> ManualDeviceTypeOptions => WifiDeviceTypeCatalog.ManualTypes;

    private string _manualDeviceType = string.Empty;
    public string ManualDeviceType
    {
        get => _manualDeviceType;
        set => SetProperty(ref _manualDeviceType, value ?? string.Empty);
    }

    public string SelectedDeviceSummary => SelectedDeviceInventoryItem is null
        ? "Select a device to view details and assign a friendly name."
        : $"Selected: {SelectedDeviceInventoryItem.IpAddress} - {SelectedDeviceInventoryItem.DisplayName}";

    private bool _isLanScanning;
    public bool IsLanScanning
    {
        get => _isLanScanning;
        private set
        {
            if (SetProperty(ref _isLanScanning, value))
            {
                (ScanLanDevicesCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                (AutoDetectWirelessDevicesCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                OnPropertyChanged(nameof(CanAutoDetectWirelessDevices));
            }
        }
    }

    private string _lanScanStatus = "Press Auto Detect to discover wireless candidates from safe local evidence.";
    public string LanScanStatus
    {
        get => _lanScanStatus;
        private set => SetProperty(ref _lanScanStatus, value);
    }

    private bool _isRouterClientScanning;
    public bool IsRouterClientScanning
    {
        get => _isRouterClientScanning;
        private set
        {
            if (SetProperty(ref _isRouterClientScanning, value))
            {
                (ReadRouterClientsCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                (AutoDetectWirelessDevicesCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                OnPropertyChanged(nameof(CanAutoDetectWirelessDevices));
            }
        }
    }

    private bool _saveRouterSnmpCredentials;

    public IReadOnlyList<string> RouterSnmpProtocols { get; } = [SnmpProtocolVersion.V2C, SnmpProtocolVersion.V3AuthPriv];
    public IReadOnlyList<string> RouterSnmpAuthProtocols { get; } = ["SHA256", "SHA384", "SHA512", "SHA1", "MD5"];
    public IReadOnlyList<string> RouterSnmpPrivacyProtocols { get; } = ["AES128", "AES192", "AES256", "DES"];

    public string RouterSnmpCommunity
    {
        get => _snmpCredentialStore.Community;
        set => _snmpCredentialStore.Community = string.IsNullOrWhiteSpace(value) ? "public" : value.Trim();
    }

    public string RouterSelectedSnmpProtocol
    {
        get => _snmpCredentialStore.Protocol;
        set => _snmpCredentialStore.Protocol = value;
    }

    public bool IsRouterSnmpV2Selected => !_snmpCredentialStore.IsV3;
    public bool IsRouterSnmpV3Selected => _snmpCredentialStore.IsV3;
    public bool SaveRouterSnmpCredentials
    {
        get => _saveRouterSnmpCredentials;
        set => SetProperty(ref _saveRouterSnmpCredentials, value);
    }

    public bool HasSavedRouterSnmpCredentials => _snmpCredentialStore.HasSavedCredentials;
    public string RouterSnmpV3UserName { get => _snmpCredentialStore.UserName; set => _snmpCredentialStore.UserName = value; }
    public string RouterSnmpV3AuthProtocol { get => _snmpCredentialStore.AuthProtocol; set => _snmpCredentialStore.AuthProtocol = value; }
    public string RouterSnmpV3AuthPassword { get => _snmpCredentialStore.AuthPassword; set => _snmpCredentialStore.AuthPassword = value; }
    public string RouterSnmpV3PrivacyProtocol { get => _snmpCredentialStore.PrivacyProtocol; set => _snmpCredentialStore.PrivacyProtocol = value; }
    public string RouterSnmpV3PrivacyPassword { get => _snmpCredentialStore.PrivacyPassword; set => _snmpCredentialStore.PrivacyPassword = value; }

    private string _routerClientStatus = "Optional: read gateway/AP SNMP evidence. Standard ipNetToMedia is ARP/IP evidence, not guaranteed Wi-Fi association.";
    public string RouterClientStatus
    {
        get => _routerClientStatus;
        private set => SetProperty(ref _routerClientStatus, value);
    }

    // ---- Sparkline projections (Polyline.Points binding) ----
    // Each rebuilds when its source ObservableCollection changes; Freeze() makes the
    // PointCollection safe to read on the UI thread and faster to render.

    // Each metric exposes a LINE polyline + an AREA polygon (closed to the baseline) so the
    // XAML can render a filled telemetry chart, plus min/avg/max text annotations. The Y axis
    // auto-scales to the data window (a rock-steady -35 dBm signal still shows its ±1 dBm
    // jitter instead of being a dead flat line glued to the chart edge — the previous fixed
    // -100..-30 scale was why the monitors "did nothing").

    private string _rssiStats = "—";
    public string RssiStats   // "min -37 · avg -35 · max -34 dBm"
    {
        get => _rssiStats;
        private set => SetProperty(ref _rssiStats, value);
    }

    // ----- Low-signal alert (NetSpot-style "tell me when it drops") -----
    private int _signalAlertThresholdDbm = -75;
    /// <summary>
    /// User-set RSSI floor. When the live signal drops below this the Strength card shows
    /// a red alert banner and plays one alert sound. Clamped to a sane −100..−40 range so
    /// the slider/box can't be set to a value that's always- or never-true.
    /// </summary>
    public int SignalAlertThresholdDbm
    {
        get => _signalAlertThresholdDbm;
        set
        {
            var clamped = Math.Clamp(value, -100, -40);
            if (SetProperty(ref _signalAlertThresholdDbm, clamped))
            {
                OnPropertyChanged(nameof(SignalAlertCaption));
                OnPropertyChanged(nameof(SignalMarginText));
                OnPropertyChanged(nameof(SignalAlertStatusText));
                EvaluateSignalAlert(Connection.RssiDbm);   // re-test immediately
            }
        }
    }

    private bool _isSignalAlerting;
    public bool IsSignalAlerting
    {
        get => _isSignalAlerting;
        private set
        {
            if (SetProperty(ref _isSignalAlerting, value))
            {
                OnPropertyChanged(nameof(SignalAlertStatusText));
            }
        }
    }

    private string _signalAlertText = string.Empty;
    /// <summary>Live text shown in the alert banner, e.g. "Signal −82 dBm — below your −75 dBm alert".</summary>
    public string SignalAlertText
    {
        get => _signalAlertText;
        private set => SetProperty(ref _signalAlertText, value);
    }

    public string SignalAlertCaption => $"Alert below {_signalAlertThresholdDbm} dBm";

    private bool _signalAlertSoundEnabled;
    public bool SignalAlertSoundEnabled
    {
        get => _signalAlertSoundEnabled;
        set
        {
            if (SetProperty(ref _signalAlertSoundEnabled, value))
            {
                if (!value)
                {
                    _signalAlertSounded = false;
                }

                OnPropertyChanged(nameof(SignalAlertStatusText));
            }
        }
    }

    public string SignalAlertStatusText
    {
        get
        {
            if (!Connection.IsConnected)
            {
                return "Alert waits for an active Wi-Fi connection.";
            }

            if (Connection.RssiDbm is not { } rssi)
            {
                return IsMonitoring ? "Alert armed; waiting for RSSI sample." : "Alert idle until monitoring starts.";
            }

            var sound = SignalAlertSoundEnabled ? "sound on" : "sound off";
            return IsSignalAlerting
                ? $"Alert active: {rssi} dBm is below {_signalAlertThresholdDbm} dBm ({sound})."
                : $"Alert armed below {_signalAlertThresholdDbm} dBm ({sound}).";
        }
    }

    // True once the sound has fired for the current dip; reset (with 3 dB hysteresis) when
    // the signal recovers, so a signal hovering around the threshold doesn't beep every second.
    private bool _signalAlertSounded;

    private string _speedStats = "—";
    public string SpeedStats  // "Rx 120 · Tx 120 Mbps (max 144)"
    {
        get => _speedStats;
        private set => SetProperty(ref _speedStats, value);
    }

    private string _throughputStats = "No throughput samples";
    public string ThroughputStats
    {
        get => _throughputStats;
        private set => SetProperty(ref _throughputStats, value);
    }

    public DateTimeOffset? LastScanAt
    {
        get => _lastScanAt;
        private set => SetProperty(ref _lastScanAt, value);
    }

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public bool IsScanning
    {
        get => _isScanning;
        private set
        {
            if (SetProperty(ref _isScanning, value))
            {
                (ScanNetworksCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                (RefreshConnectionCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsDetectingIsp
    {
        get => _isDetectingIsp;
        private set
        {
            if (SetProperty(ref _isDetectingIsp, value))
            {
                (DetectIspCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            }
        }
    }

    // ---- On-demand control (user explicitly asked to start it only when needed) ----

    private bool _isMonitoring;
    /// <summary>True between Start Analyzer and Stop. Drives empty-state vs live UI.</summary>
    public bool IsMonitoring
    {
        get => _isMonitoring;
        private set
        {
            if (SetProperty(ref _isMonitoring, value))
            {
                OnPropertyChanged(nameof(IsIdle));
                OnPropertyChanged(nameof(LiveSignalValueText));
                OnPropertyChanged(nameof(SignalStrengthLabel));
                OnPropertyChanged(nameof(SignalMarginText));
                OnPropertyChanged(nameof(SignalAlertStatusText));
                OnPropertyChanged(nameof(LiveLinkSpeedValueText));
                OnPropertyChanged(nameof(LinkSpeedRxText));
                OnPropertyChanged(nameof(LinkSpeedTxText));
                OnPropertyChanged(nameof(LinkSpeedStateText));
                OnPropertyChanged(nameof(LinkSpeedStateColor));
                OnPropertyChanged(nameof(LinkSpeedDeltaText));
                OnPropertyChanged(nameof(LiveThroughputValueText));
                OnPropertyChanged(nameof(ReceiveThroughputNowText));
                OnPropertyChanged(nameof(TransferThroughputNowText));
                OnPropertyChanged(nameof(ReceiveThroughputStatsText));
                OnPropertyChanged(nameof(TransferThroughputStatsText));
                OnPropertyChanged(nameof(ReceiveThroughputStateText));
                OnPropertyChanged(nameof(TransferThroughputStateText));
                OnPropertyChanged(nameof(ReceiveThroughputStateColor));
                OnPropertyChanged(nameof(TransferThroughputStateColor));
                OnPropertyChanged(nameof(SignalSampleCountText));
                OnPropertyChanged(nameof(SpeedSampleCountText));
                RefreshCommandStates();
            }
        }
    }

    /// <summary>Inverse of <see cref="IsMonitoring"/> — for showing the empty-state panel.</summary>
    public bool IsIdle => !_isMonitoring;

    private bool _autoRescanEnabled;   // default OFF — user opts in to the 30 s timer
    /// <summary>
    /// When true, a fresh active scan runs every 30 s while monitoring. Default OFF so the
    /// analyzer does a single scan on Start and then stays quiet unless the user wants the
    /// continuous NetSpot-style refresh.
    /// </summary>
    public bool AutoRescanEnabled
    {
        get => _autoRescanEnabled;
        set
        {
            if (!SetProperty(ref _autoRescanEnabled, value)) return;
            // Live-apply: toggling while monitoring arms/disarms the timer immediately.
            if (IsMonitoring)
            {
                if (value) StartAutoScanTimer();
                else StopAutoScanTimer();
            }
        }
    }

    // ---- Link Score (inSSIDer-style composite metric, 0..100) ----
    // Computed from signal (40%) + congestion (30%) + security (15%) + PHY (15%).
    // Surfaces in the hero card as a single number — easier for non-experts than dBm.

    public int? LinkScore
    {
        get => _linkScore;
        private set => SetProperty(ref _linkScore, value);
    }

    public string LinkScoreVerdict
    {
        get => _linkScoreVerdict;
        private set => SetProperty(ref _linkScoreVerdict, value);
    }

    /// <summary>"#10B981" (green) / "#3B82F6" (blue) / "#F59E0B" (amber) / "#EF4444" (red) — for the Link Score badge.</summary>
    public string LinkScoreColor => _linkScore switch
    {
        null => "#94A3B8",
        >= 85 => "#10B981",     // emerald — Excellent
        >= 70 => "#3B82F6",     // blue    — Good
        >= 50 => "#F59E0B",     // amber   — Fair
        _ => "#EF4444",         // red     — Poor
    };

    /// <summary>True after at least one scan ran. UI hides empty-state messages once we have data.</summary>
    public bool HasScanData => _totalScans > 0;

    public string LastScanText => LastScanAt is null
        ? "Never"
        : $"{LastScanAt.Value:HH:mm:ss} · {VisibleAccessPoints.Count} APs found";

    public string ScannerSourceLabel => _scanner is WlanApiScanner ? "P/Invoke (WLAN API)" : "alternate scanner";

    // ---- Visible Networks search / filter ----

    private string _networkFilter = string.Empty;
    /// <summary>
    /// Free-text filter for the Visible Networks grid. Matched case-insensitively against
    /// SSID, BSSID, vendor, band, 802.11 standard and (exact) channel number. Empty shows
    /// every AP. The channel graphs + exported report keep using the full unfiltered list.
    /// </summary>
    public string NetworkFilter
    {
        get => _networkFilter;
        set
        {
            if (SetProperty(ref _networkFilter, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(HasNetworkFilter));
                OnPropertyChanged(nameof(NoNetworkFilter));
                ApplyNetworkFilter();
            }
        }
    }

    /// <summary>True when a search term is active — drives the clear (✕) button's visibility.</summary>
    public bool HasNetworkFilter => !string.IsNullOrWhiteSpace(_networkFilter);

    /// <summary>Inverse of <see cref="HasNetworkFilter"/> — drives the search-box placeholder hint.</summary>
    public bool NoNetworkFilter => !HasNetworkFilter;

    private string _networkFilterSummary = string.Empty;
    /// <summary>"6 of 14 networks" caption shown beside the search box while a filter is active.</summary>
    public string NetworkFilterSummary
    {
        get => _networkFilterSummary;
        private set => SetProperty(ref _networkFilterSummary, value);
    }

    /// <summary>
    /// Rebuilds <see cref="FilteredAccessPoints"/> from <see cref="VisibleAccessPoints"/> using
    /// the current <see cref="NetworkFilter"/>. Called after every scan refresh and whenever the
    /// search text changes. Both call sites are already on the UI thread.
    /// </summary>
    private void ApplyNetworkFilter()
    {
        var term = _networkFilter.Trim();
        if (term.Length == 0)
        {
            ReplaceItems(FilteredAccessPoints, VisibleAccessPoints);
            NetworkFilterSummary = string.Empty;
            return;
        }

        var matches = VisibleAccessPoints.Where(ap => MatchesNetworkFilter(ap, term)).ToList();
        ReplaceItems(FilteredAccessPoints, matches);
        NetworkFilterSummary = $"{matches.Count} of {VisibleAccessPoints.Count} networks";
    }

    private static bool MatchesNetworkFilter(WifiAccessPoint ap, string term)
        => ap.DisplaySsid.Contains(term, StringComparison.OrdinalIgnoreCase)
           || ap.Bssid.Contains(term, StringComparison.OrdinalIgnoreCase)
           || ap.VendorDisplay.Contains(term, StringComparison.OrdinalIgnoreCase)
           || ap.BandLabel.Contains(term, StringComparison.OrdinalIgnoreCase)
           || ap.PhyShortLabel.Contains(term, StringComparison.OrdinalIgnoreCase)
           || ap.PhyFriendlyName.Contains(term, StringComparison.OrdinalIgnoreCase)
           || ap.Security.Label.Contains(term, StringComparison.OrdinalIgnoreCase)
           || ap.ChannelLabel.Contains(term, StringComparison.OrdinalIgnoreCase)
           || ap.ChannelWidthLabel.Contains(term, StringComparison.OrdinalIgnoreCase)
           || ap.Channel.ToString(System.Globalization.CultureInfo.InvariantCulture)
                 .Equals(term, StringComparison.OrdinalIgnoreCase);

    // ============== Commands ==============

    public ICommand AutoDetectWirelessDevicesCommand { get; }
    public ICommand ScanLanDevicesCommand { get; }
    public ICommand ReadRouterClientsCommand { get; }
    public ICommand SetDeviceInventoryFilterCommand { get; }
    public ICommand ClearDeviceSessionCommand { get; }
    public ICommand SaveDeviceFriendlyNameCommand { get; }
    public ICommand ClearDeviceFriendlyNameCommand { get; }
    public ICommand SaveDeviceTypeCommand { get; }
    public ICommand ClearDeviceTypeCommand { get; }
    public ICommand ForgetRouterSnmpCredentialCommand { get; }
    public ICommand ForgetSelectedSavedProfileCommand { get; }
    public ICommand ExportReportCommand { get; }
    public ICommand CopyReportCommand { get; }
    public ICommand OpenHistoryCommand { get; }
    public ICommand StartMonitoringCommand { get; }
    public ICommand StopMonitoringCommand { get; }
    public ICommand ScanNetworksCommand { get; }
    public ICommand RefreshConnectionCommand { get; }
    public ICommand DetectIspCommand { get; }
    public ICommand ClearRoamingHistoryCommand { get; }
    public ICommand ClearNetworkFilterCommand { get; }

    // ============== Public lifecycle ==============

    /// <summary>
    /// Called when the user navigates to the Wi-Fi page. Does the one-time probe (capabilities)
    /// + first connection snapshot, then starts the 1 Hz sampler. Idempotent.
    /// </summary>
    /// <remarks>
    /// This method is invoked from the <see cref="MainViewModel.CurrentPage"/> setter on the UI
    /// thread. We DO NOT use <c>ConfigureAwait(false)</c> on awaits here — every continuation
    /// after an await touches properties bound to the UI (Capabilities, Connection, Status),
    /// so we must stay on the UI thread. Cross-thread DependencyObject access is the most
    /// common WPF crash and ConfigureAwait(false) is the most common cause.
    /// </remarks>
    /// <summary>
    /// Called when the user navigates to the Wi-Fi page. ON-DEMAND model (per user request):
    /// this does ONLY the cheap one-time setup — resolve adapter, read capabilities, and one
    /// connection snapshot from the existing scan cache. It does NOT trigger an active scan,
    /// does NOT start the 1 Hz sampler, and does NOT start the auto-rescan timer. Nothing
    /// touches the radio or the network until the user explicitly presses "Start Analyzer".
    /// </summary>
    public async Task ActivateAsync(CancellationToken cancellationToken)
    {
        if (_disposed) return;

        if (!_isInitialized)
        {
            _adapterId = _scanner.GetPrimaryAdapterId();
            if (_adapterId is null)
            {
                Status = "No Wi-Fi adapter detected — Wi-Fi unavailable on this PC.";
                _logger.Warning("Wi-Fi analyzer: no adapter detected.");
                _isInitialized = true;
                RefreshCommandStates();
                return;
            }

            try
            {
                Capabilities = await _capabilityProbe
                    .ProbeAsync(_adapterId.Value, /*adapterDescription*/ "Wi-Fi", cancellationToken);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                _logger.Warning($"Wi-Fi capability probe failed: {ex.Message}");
            }

            _isInitialized = true;
        }

        RefreshCommandStates();

        // One cheap connection snapshot from the cache so the Connection card isn't empty.
        // No radio activity, no sampler, no scan — those wait for Start.
        await RefreshConnectionAsync();

        Status = IsMonitoring
            ? "Monitoring…"
            : "Idle — press Start Analyzer to scan networks and begin live monitoring.";
    }

    /// <summary>
    /// User pressed "Start Analyzer". Begins the 1 Hz live sampler, runs an initial active
    /// scan, fetches ISP once, and (only if <see cref="AutoRescanEnabled"/>) arms the 30 s
    /// auto-rescan timer.
    /// </summary>
    private async Task StartMonitoringAsync()
    {
        if (_disposed || _adapterId is null || IsMonitoring) return;

        IsMonitoring = true;
        Status = "Starting Wi-Fi analyzer…";

        // Fresh monitoring session → reset the live roam baseline + counter.
        _lastSampledConnection = null;
        _sessionRoamCount = 0;
        RoamingSummary = "No roaming this session.";

        if (!_sampler.IsRunning)
        {
            _sampler.Start(_adapterId.Value);
        }

        // First active scan. Public IP / ISP lookup stays explicit via Detect ISP.
        await ScanNetworksAsync();
        if (_disposed || !IsMonitoring)
        {
            return;
        }

        if (AutoRescanEnabled)
        {
            StartAutoScanTimer();
        }
    }

    /// <summary>User pressed "Stop". Halts active Wi-Fi work. Data stays on screen.</summary>
    private void StopMonitoring()
    {
        IsMonitoring = false;
        _sampler.Stop();
        StopAutoScanTimer();
        CancelActiveOperations();
        Status = "Stopped. Last results kept on screen - press Start Analyzer to refresh.";
    }

    /// <summary>
    /// Re-evaluates CanExecute for all commands whose enabled state depends on fields that
    /// don't fire PropertyChanged (e.g. <see cref="_adapterId"/>). Safe to call from any thread —
    /// <see cref="AsyncRelayCommand.RaiseCanExecuteChanged"/> marshals to the WPF Dispatcher.
    /// </summary>
    private void RefreshCommandStates()
    {
        (ScanNetworksCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (RefreshConnectionCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (StartMonitoringCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (StopMonitoringCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (DetectIspCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (AutoDetectWirelessDevicesCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (ScanLanDevicesCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (ReadRouterClientsCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (SaveDeviceFriendlyNameCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (ClearDeviceFriendlyNameCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (SaveDeviceTypeCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (ClearDeviceTypeCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (ForgetSelectedSavedProfileCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (ExportReportCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (CopyReportCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    /// <summary>Called when the user navigates away. Stops everything to save resources.</summary>
    public void Deactivate()
    {
        // Navigating away always halts radio/network activity. Returning to the page starts
        // idle so the toolbar state matches the fact that sampler/timers are no longer live.
        IsMonitoring = false;
        _sampler.Stop();
        StopAutoScanTimer();
        CancelActiveOperations();
    }

    private void StartAutoScanTimer()
    {
        StopAutoScanTimer();
        _autoScanTimer = new System.Threading.Timer(
            _ => QueueAutoScanFromTimer(),
            state: null,
            dueTime: TimeSpan.FromSeconds(30),
            period: TimeSpan.FromSeconds(30));
    }

    private void StopAutoScanTimer()
    {
        _autoScanTimer?.Dispose();
        _autoScanTimer = null;
    }

    private bool CanRunAutoRescan =>
        !_disposed && IsMonitoring && AutoRescanEnabled && !IsScanning && _adapterId is not null;

    private void QueueAutoScanFromTimer()
    {
        if (!CanRunAutoRescan) return;

        _uiContext.Post(_ =>
        {
            if (!CanRunAutoRescan) return;
            _ = ScanNetworksAsync();
        }, null);
    }

    // ============== Command bodies ==============

    private async Task ScanNetworksAsync()
    {
        // _disposed guard: the 30 s auto-rescan timer fires _ = ScanNetworksAsync() as a
        // fire-and-forget. If the page/VM was torn down between the timer tick and this
        // running, _scanCancellation has already been disposed → .Cancel() would throw
        // ObjectDisposedException on a thread-pool thread (unobserved). Bail early.
        if (_disposed || _adapterId is null || IsScanning || !IsMonitoring) return;

        CancelQuietly(_scanCancellation);
        var scanCancellation = new CancellationTokenSource();
        _scanCancellation = scanCancellation;
        var ct = scanCancellation.Token;

        IsScanning = true;
        Status = "Scanning for Wi-Fi networks…";
        try
        {
            // No ConfigureAwait(false) — we set Status / LastScanAt afterwards which must
            // happen on the UI thread (those properties are bound to UI controls).
            var ok = await _scanner.ForceScanAsync(_adapterId.Value, ct);
            if (_disposed || !ReferenceEquals(_scanCancellation, scanCancellation))
            {
                return;
            }

            if (!ok)
            {
                // The kernel rejects WlanScan when the radio is off (Wi-Fi toggle / airplane
                // mode) or the WLAN AutoConfig service is stopped — by far the most common
                // causes. Point the tech at those instead of a bare "rejected".
                Status = "Scan request rejected — the Wi-Fi radio is likely off "
                       + "(check the Wi-Fi toggle / airplane mode) or the WLAN AutoConfig service is stopped.";
                return;
            }
            var previousConnection = _connection;
            var freshConnection = _scanner.GetCurrentConnection(_adapterId.Value)
                ?? WifiConnectionDetails.Disconnected(Infrastructure.Wlan.WlanInterfaceState.Disconnected);
            var scanRoam = _engine.DetectRoaming(previousConnection, freshConnection);
            if (scanRoam is not null)
            {
                AddRoamingEventOnUi(scanRoam.Value);
                _logger.Info($"Wi-Fi roam detected during scan: {scanRoam.Value.FromBssid} -> {scanRoam.Value.ToBssid} ({scanRoam.Value.DeltaLabel})");
            }
            Connection = freshConnection;

            await RefreshScanResultsAsync(ct, recordChannelHistory: true);
            if (_disposed || !ReferenceEquals(_scanCancellation, scanCancellation))
            {
                return;
            }

            LastScanAt = DateTimeOffset.Now;
            _totalScans++;
            OnPropertyChanged(nameof(LastScanText));
            OnPropertyChanged(nameof(HasScanData));
            Status = $"Scan complete · {VisibleAccessPoints.Count} APs visible";
        }
        catch (OperationCanceledException)
        {
            Status = IsMonitoring
                ? "Scan cancelled."
                : "Stopped. Last results kept on screen - press Start Analyzer to refresh.";
        }
        catch (Exception ex)
        {
            Status = $"Scan failed: {ex.Message}";
            _logger.Error("Wi-Fi scan failed", ex);
        }
        finally
        {
            scanCancellation.Dispose();
            if (ReferenceEquals(_scanCancellation, scanCancellation))
            {
                _scanCancellation = null;
                IsScanning = false;
            }
        }
    }

    private async Task RefreshConnectionAsync()
    {
        if (_disposed || _adapterId is null) return;

        try
        {
            Status = "Refreshing Wi-Fi connection snapshot...";
            var previous = _connection;
            var fresh = _scanner.GetCurrentConnection(_adapterId.Value)
                ?? WifiConnectionDetails.Disconnected(Infrastructure.Wlan.WlanInterfaceState.Disconnected);

            // Roaming detection — must happen BEFORE we replace Connection.
            var roam = _engine.DetectRoaming(previous, fresh);
            if (roam is not null)
            {
                AddRoamingEventOnUi(roam.Value);
                _logger.Info($"Wi-Fi roam detected: {roam.Value.FromBssid} → {roam.Value.ToBssid} ({roam.Value.DeltaLabel})");
            }

            Connection = fresh;

            // Re-read visible APs (cheap — uses scan cache, no fresh scan).
            // Don't ConfigureAwait(false) — caller may set UI-bound props after we return.
            await RefreshScanResultsAsync();
            if (_disposed)
            {
                return;
            }

            Status = fresh.IsConnected
                ? $"Connection refreshed - {fresh.Ssid ?? "hidden SSID"}"
                : $"Connection refreshed - {ConnectionStateLabel}.";
        }
        catch (Exception ex)
        {
            Status = $"Connection refresh failed: {ex.Message}";
            _logger.Warning($"Wi-Fi connection refresh failed: {ex.Message}");
        }
    }

    private async Task RefreshScanResultsAsync(CancellationToken cancellationToken = default, bool recordChannelHistory = false)
    {
        if (_adapterId is null) return;
        cancellationToken.ThrowIfCancellationRequested();
        var adapterId = _adapterId.Value;

        // Off-thread heavy reads - we WANT the heavy work off the UI thread, hence Task.Run.
        // The await DOES use ConfigureAwait(false) inside so the continuation runs on the
        // thread pool; that's fine because the actual UI-touching work below is wrapped in
        // PostToUi() which marshals back to the captured UI sync-context.
        var aps = await Task.Run(() => _scanner.GetVisibleAccessPoints(adapterId), cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var profiles = await Task.Run(() => _scanner.GetSavedProfiles(adapterId), cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var savedProfiles = WifiSavedProfile.FromNames(profiles);

        // Identify the "us" AP from the freshly captured list for IsCurrentConnection in channel map.
        var currentAp = aps.FirstOrDefault(a => a.IsCurrentConnection);
        var channelMap = _engine.AnalyzeChannels(aps, currentAp);
        var health = _engine.GenerateHealthReport(Connection, channelMap, aps, Capabilities);
        var securityAudit = WifiSecurityAuditBuilder.Build(Connection, aps, savedProfiles, Capabilities);
        var savedProfileHygiene = WifiSavedProfileHygieneBuilder.Build(Connection, aps, savedProfiles);
        var channelRecommendation = WifiAnalyzerEngine.BuildChannelRecommendation(channelMap, Connection);
        var bestChannelAdvice = WifiAnalyzerEngine.BuildBestChannelAdvice(channelMap, Connection);

        // Fold the fresh APs into the session-long per-BSSID stats (avg RSSI, detection count,
        // first/last seen). This is what powers the NirSoft-style "Avg" / "Seen %" columns.
        var scanIndex = _totalScans + 1;   // this scan is about to become #(_totalScans+1)
        var enriched = new List<WifiAccessPoint>(aps.Count);
        foreach (var ap in aps)
        {
            if (!_bssidStats.TryGetValue(ap.Bssid, out var st))
            {
                st = new BssidStats { FirstSeen = ap.FirstSeenAt };
                _bssidStats[ap.Bssid] = st;
            }
            st.Observe(ap.RssiDbm, ap.LastSeenAt, scanIndex);

            enriched.Add(ap with
            {
                FirstSeenAt = st.FirstSeen,
                LastSeenAt = st.LastSeen,
                AverageRssiDbm = st.AverageRssi,
                DetectionPercent = st.DetectionPercent(scanIndex),
            });
        }

        // Compute Link Score (inSSIDer-style) from the health report contributions.
        var (score, verdict) = ComputeLinkScore(health, Connection);
        var channelHistoryEntry = recordChannelHistory
            ? BuildChannelHistoryEntry(Connection, channelMap, channelRecommendation, score)
            : null;

        // Build the multi-network signal-over-time series: top-8 strongest networks
        // (always include the connected one), each with its RSSI history across scans.
        var trends = enriched
            .OrderByDescending(a => a.IsCurrentConnection)
            .ThenByDescending(a => a.RssiDbm)
            .Take(8)
            .Select(a =>
            {
                _bssidStats.TryGetValue(a.Bssid, out var st);
                var color = UI.Controls.WifiColorPalette.ForBssid(a.Bssid);
                var colorHex = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
                // Preserve the user's show/hide choice across rescans (default visible).
                var visible = !_trendHidden.Contains(a.Bssid);
                return new WifiSignalTrend(
                    a.Bssid,
                    a.DisplaySsid,
                    a.IsCurrentConnection,
                    st?.RssiHistory.ToArray() ?? Array.Empty<int>(),
                    colorHex,
                    visible);
            })
            .ToList();
        // Wire each trend's checkbox so toggling persists across rescans + redraws graph.
        foreach (var t in trends)
        {
            t.PropertyChanged += OnTrendVisibilityChanged;
        }

        PostToUi(() =>
        {
            if (_disposed || cancellationToken.IsCancellationRequested) return;

            ReplaceItems(VisibleAccessPoints, enriched);
            ApplyNetworkFilter();   // re-narrow the grid to the active search term (if any)
            ReplaceItems(ChannelMap, channelMap);
            ChannelRecommendation = channelRecommendation;
            BestChannelAdvice = bestChannelAdvice;
            ReplaceItems(SavedProfiles, savedProfiles);
            SavedProfileHygiene = savedProfileHygiene;
            RefreshSelectedSavedProfileAfterHygiene(savedProfileHygiene);
            _savedProfilesLoaded = true;
            SavedProfilesSummary = BuildSavedProfilesSummary(savedProfiles.Count);
            OnPropertyChanged(nameof(HasSavedProfiles));
            OnPropertyChanged(nameof(NoSavedProfiles));
            OnPropertyChanged(nameof(SavedProfilesEmptyText));
            ReplaceItems(SignalTrends, trends);
            Health = health;
            SecurityAudit = securityAudit;
            LinkScore = score;
            LinkScoreVerdict = verdict;
            if (channelHistoryEntry is not null)
            {
                AddChannelHistoryEntryOnUi(channelHistoryEntry);
            }
            OnPropertyChanged(nameof(LinkScoreColor));
            OnPropertyChanged(nameof(LastScanText));
        });
    }

    /// <summary>
    /// inSSIDer-style composite 0..100. We reuse the engine's weighted contributions
    /// (signal 40 / congestion 30 / security 15 / phy 15) which already sum to the
    /// health score — Link Score IS that score, but surfaced as a first-class hero metric
    /// with its own colour + verdict word so non-experts get an instant read.
    /// </summary>
    private static (int? Score, string Verdict) ComputeLinkScore(WifiHealthReport health, WifiConnectionDetails conn)
    {
        if (!conn.IsConnected || health.Score is null)
        {
            return (null, conn.IsConnected ? "Analyzing…" : "Not connected");
        }
        var s = health.Score.Value;
        var verdict = s switch
        {
            >= 85 => "Excellent",
            >= 70 => "Good",
            >= 50 => "Fair",
            _ => "Poor",
        };
        return (s, verdict);
    }

    private static WifiChannelHistoryEntry? BuildChannelHistoryEntry(
        WifiConnectionDetails connection,
        IReadOnlyList<WifiChannelAnalysis> channelMap,
        WifiChannelRecommendation recommendation,
        int? linkScore)
    {
        if (!connection.IsConnected
            || connection.Channel is not { } channel
            || connection.Band == WifiBand.Unknown)
        {
            return null;
        }

        var current = channelMap.FirstOrDefault(c => c.Band == connection.Band && c.Channel == channel);
        var recommendationText = recommendation.IsReady
            ? $"{recommendation.Recommended}: {recommendation.Reason}"
            : string.Empty;

        return new WifiChannelHistoryEntry(
            Timestamp: DateTimeOffset.Now,
            Ssid: connection.Ssid,
            Bssid: connection.Bssid,
            Band: connection.Band,
            Channel: channel,
            RssiDbm: connection.RssiDbm,
            LinkScore: linkScore,
            ExactApCount: current?.ApsOnExactChannel ?? 0,
            OverlappingApCount: current?.ApsOnOverlappingChannels ?? 0,
            CongestionLevel: current?.CongestionLevel ?? WifiCongestionLevel.Free,
            IsDfsChannel: current?.IsDfsChannel ?? WifiFrequencyHelper.IsDfsChannel(connection.Band, channel),
            RecommendationSeverity: recommendation.SeverityDisplay,
            Recommendation: recommendationText);
    }

    private void AddChannelHistoryEntryOnUi(WifiChannelHistoryEntry entry)
    {
        ChannelHistory.Insert(0, entry);
        while (ChannelHistory.Count > ChannelHistorySize)
        {
            ChannelHistory.RemoveAt(ChannelHistory.Count - 1);
        }

        ChannelBeforeAfterSummary = BuildChannelBeforeAfterSummary(ChannelHistory);
        OnPropertyChanged(nameof(HasChannelHistory));
        OnPropertyChanged(nameof(ChannelHistorySummary));
    }

    private static WifiChannelBeforeAfterSummary BuildChannelBeforeAfterSummary(IList<WifiChannelHistoryEntry> history)
    {
        if (history.Count == 0)
        {
            return WifiChannelBeforeAfterSummary.NoData();
        }

        var latest = history[0];
        if (history.Count == 1)
        {
            return new WifiChannelBeforeAfterSummary(
                Severity: "Info",
                Title: "Baseline captured",
                Before: "No previous active scan in this session.",
                After: latest.CompactDisplay,
                Delta: "Scan again after a router/AP channel change to compare.",
                Detail: "This baseline is built from the latest active WLAN scan, current RSSI and Link Score.",
                GeneratedAt: DateTimeOffset.Now);
        }

        var previous = history[1];
        var sameNetwork = SameWifiNetwork(previous, latest);
        var channelChanged = previous.Band != latest.Band || previous.Channel != latest.Channel;
        var scoreDelta = latest.LinkScore.HasValue && previous.LinkScore.HasValue
            ? latest.LinkScore.Value - previous.LinkScore.Value
            : (int?)null;
        var rssiDelta = latest.RssiDbm.HasValue && previous.RssiDbm.HasValue
            ? latest.RssiDbm.Value - previous.RssiDbm.Value
            : (int?)null;
        var loadDelta = latest.ComparisonLoad - previous.ComparisonLoad;

        var improved = (scoreDelta ?? 0) >= 5 || (rssiDelta ?? 0) >= 3 || loadDelta <= -2;
        var worsened = (scoreDelta ?? 0) <= -5 || (rssiDelta ?? 0) <= -3 || loadDelta >= 2;
        var severity = !sameNetwork
            ? "Info"
            : improved && !worsened
                ? "OK"
                : worsened && !improved
                    ? "Warning"
                    : "Info";
        var title = !sameNetwork
            ? "Network changed"
            : channelChanged
                ? "Channel changed"
                : "Same channel";
        if (sameNetwork && improved && worsened)
        {
            title += " - mixed result";
        }

        return new WifiChannelBeforeAfterSummary(
            Severity: severity,
            Title: title,
            Before: previous.CompactDisplay,
            After: latest.CompactDisplay,
            Delta: FormatChannelDelta(scoreDelta, rssiDelta, loadDelta),
            Detail: BuildChannelComparisonDetail(sameNetwork, channelChanged, previous, latest),
            GeneratedAt: DateTimeOffset.Now);
    }

    private static bool SameWifiNetwork(WifiChannelHistoryEntry previous, WifiChannelHistoryEntry latest)
    {
        if (!string.IsNullOrWhiteSpace(previous.Bssid)
            && !string.IsNullOrWhiteSpace(latest.Bssid))
        {
            return string.Equals(previous.Bssid, latest.Bssid, StringComparison.OrdinalIgnoreCase);
        }

        return string.Equals(previous.Ssid, latest.Ssid, StringComparison.OrdinalIgnoreCase);
    }

    private static string FormatChannelDelta(int? scoreDelta, int? rssiDelta, int loadDelta)
    {
        var parts = new List<string>(3);
        if (scoreDelta is { } score)
        {
            parts.Add($"Link Score {Signed(score)}");
        }
        if (rssiDelta is { } rssi)
        {
            parts.Add($"RSSI {Signed(rssi)} dB");
        }

        var loadNoun = Math.Abs(loadDelta) == 1 ? "AP" : "APs";
        parts.Add($"Load {Signed(loadDelta)} {loadNoun}");
        return string.Join(", ", parts);
    }

    private static string BuildChannelComparisonDetail(
        bool sameNetwork,
        bool channelChanged,
        WifiChannelHistoryEntry previous,
        WifiChannelHistoryEntry latest)
    {
        if (!sameNetwork)
        {
            return "Latest and previous scans are for different SSID/BSSID values, so the comparison is informational only.";
        }

        if (channelChanged)
        {
            return $"Before was {previous.ChannelDisplay}; after is {latest.ChannelDisplay}. Lower load and higher RSSI/Link Score indicate improvement.";
        }

        return "No channel change was detected between the last two active scans; use this as a stability trend for the current channel.";
    }

    private static string Signed(int value) => value > 0
        ? $"+{value}"
        : value.ToString();

    private async Task ForgetSelectedSavedProfileAsync()
    {
        if (_adapterId is not { } adapterId || SelectedSavedProfileHygieneItem is null)
        {
            SavedProfileForgetStatus = "Select a saved Wi-Fi profile before using Forget.";
            return;
        }

        var profileName = SelectedSavedProfileHygieneItem.ProfileName.Trim();
        if (string.IsNullOrWhiteSpace(profileName))
        {
            SavedProfileForgetStatus = "Selected profile name is empty; nothing was changed.";
            ConfirmForgetSavedProfile = false;
            return;
        }

        if (IsActiveSavedProfile(profileName))
        {
            SavedProfileForgetStatus = "Active Wi-Fi profile cannot be forgotten while this PC is connected to it.";
            Status = SavedProfileForgetStatus;
            ConfirmForgetSavedProfile = false;
            return;
        }

        IsForgettingSavedProfile = true;
        SavedProfileForgetStatus = $"Forgetting saved profile '{profileName}'...";
        Status = SavedProfileForgetStatus;
        try
        {
            var removed = await Task.Run(() => _scanner.ForgetSavedProfile(adapterId, profileName));
            if (!removed)
            {
                SavedProfileForgetStatus = $"Windows did not delete saved profile '{profileName}'. It may already be removed or access was denied.";
                Status = SavedProfileForgetStatus;
                return;
            }

            SavedProfileForgetStatus = $"Forgot saved profile '{profileName}'. Refreshing profile list...";
            Status = SavedProfileForgetStatus;
            await RefreshScanResultsAsync();
            SavedProfileForgetStatus = $"Forgot saved profile '{profileName}'.";
            Status = SavedProfileForgetStatus;
        }
        catch (Exception ex)
        {
            SavedProfileForgetStatus = $"Forget profile failed: {ex.Message}";
            Status = SavedProfileForgetStatus;
            _logger.Warning($"Wi-Fi saved profile forget failed: {ex.Message}");
        }
        finally
        {
            ConfirmForgetSavedProfile = false;
            IsForgettingSavedProfile = false;
        }
    }

    private void RefreshSelectedSavedProfileAfterHygiene(WifiSavedProfileHygieneReport report)
    {
        if (SelectedSavedProfileHygieneItem is null)
        {
            return;
        }

        var selectedName = SelectedSavedProfileHygieneItem.ProfileName;
        var replacement = report.Items.FirstOrDefault(item =>
            string.Equals(item.ProfileName, selectedName, StringComparison.OrdinalIgnoreCase));
        SelectedSavedProfileHygieneItem = replacement;
    }

    private bool IsActiveSavedProfile(string? profileName)
    {
        if (!Connection.IsConnected || string.IsNullOrWhiteSpace(profileName))
        {
            return false;
        }

        return string.Equals(Connection.ProfileName, profileName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(Connection.Ssid, profileName, StringComparison.OrdinalIgnoreCase);
    }

    private async Task DetectIspAsync()
    {
        // Async command can outlive a teardown. Same guard as ScanNetworksAsync /
        // ScanLanDevicesAsync: after Dispose
        // _ispCancellation is disposed, so touching it would throw ObjectDisposedException
        // on a thread-pool thread (unobserved → process-level risk).
        if (_disposed || IsDetectingIsp) return;

        CancelQuietly(_ispCancellation);
        var ispCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        _ispCancellation = ispCancellation;
        var ct = ispCancellation.Token;

        IsDetectingIsp = true;
        Status = "Detecting public IP and ISP…";
        try
        {
            // No ConfigureAwait(false) — we set Connection / IspLookupAttempted / Status afterwards
            // and those bind to UI controls. Cross-thread DependencyObject access otherwise.
            var info = await _publicIpProbe.ProbeAsync(ct);
            if (_disposed || !ReferenceEquals(_ispCancellation, ispCancellation))
            {
                return;
            }

            // Merge result into the Connection record (fresh copy — records are immutable).
            Connection = Connection with
            {
                PublicIpAddress = info.PublicIpAddress,
                IspName = info.IspName,
                IspLocation = info.Location,
            };

            Status = info.IsSuccess
                ? $"ISP detected: {info.IspName ?? info.PublicIpAddress}"
                : $"ISP lookup failed: {info.ErrorMessage}";
        }
        catch (OperationCanceledException)
        {
            Status = "ISP lookup cancelled.";
        }
        catch (Exception ex)
        {
            Status = $"ISP lookup failed: {ex.Message}";
            _logger.Warning($"Wi-Fi ISP lookup error: {ex.Message}");
        }
        finally
        {
            ispCancellation.Dispose();
            if (ReferenceEquals(_ispCancellation, ispCancellation))
            {
                _ispCancellation = null;
                IsDetectingIsp = false;
            }
        }
    }

    // ============== Sampler event handlers ==============

    private void OnSignalSampled(object? sender, WifiSignalSample sample)
    {
        if (_disposed) return;

        PostToUi(() =>
        {
            if (_disposed) return;

            SignalSamples.Add(sample);
            while (SignalSamples.Count > SparklineBufferSize) SignalSamples.RemoveAt(0);

            // Update Connection's live RSSI so the Strength card stays current at 1 Hz
            // without re-querying the full connection. Cheap immutable update.
            Connection = Connection with
            {
                RssiDbm = sample.RssiDbm,
                SignalLevel = sample.Level,
            };

            // The live curve is drawn by WifiSparklineGraph straight from SignalSamples;
            // here we only keep the min/avg/max readout.
            if (SignalSamples.Count > 0)
            {
                var rssi = SignalSamples.Select(s => (double)s.RssiDbm).ToList();
                RssiStats = $"min {rssi.Min():N0} · avg {rssi.Average():N0} · max {rssi.Max():N0} dBm";
            }

            RssiStats = RssiStats.Replace("\u00C2\u00B7", "\u00B7");
            OnPropertyChanged(nameof(HasLiveSignalSamples));
            OnPropertyChanged(nameof(LiveSignalValueText));
            OnPropertyChanged(nameof(SignalStrengthLabel));
            OnPropertyChanged(nameof(SignalStrengthColor));
            OnPropertyChanged(nameof(SignalMarginText));
            OnPropertyChanged(nameof(SignalSampleCountText));
            EvaluateSignalAlert(sample.RssiDbm);
        });
    }

    /// <summary>
    /// Drives the low-signal alert. Hysteresis (3 dB) + a one-shot sound flag prevent a
    /// signal hovering at the threshold from flapping the banner / beeping every second.
    /// Called on every 1 Hz sample and whenever the threshold changes.
    /// </summary>
    private void EvaluateSignalAlert(int? rssiDbm)
    {
        if (rssiDbm is not { } r || !Connection.IsConnected)
        {
            IsSignalAlerting = false;
            _signalAlertSounded = false;
            OnPropertyChanged(nameof(SignalMarginText));
            OnPropertyChanged(nameof(SignalAlertStatusText));
            return;
        }

        if (r < _signalAlertThresholdDbm)
        {
            SignalAlertText = $"Signal {r} dBm — below your {_signalAlertThresholdDbm} dBm alert. Move closer to the AP or check obstructions.";
            IsSignalAlerting = true;
            if (SignalAlertSoundEnabled && !_signalAlertSounded)
            {
                _signalAlertSounded = true;
                try { System.Media.SystemSounds.Exclamation.Play(); } catch { /* headless / no audio device */ }
            }
        }
        else if (r >= _signalAlertThresholdDbm + 3)   // recovered past the hysteresis band
        {
            IsSignalAlerting = false;
            _signalAlertSounded = false;
        }

        SignalAlertText = SignalAlertText
            .Replace("\u00E2\u20AC\u201D", "-")
            .Replace("\u2014", "-");
        OnPropertyChanged(nameof(SignalMarginText));
        OnPropertyChanged(nameof(SignalAlertStatusText));
    }

    private void OnSpeedSampled(object? sender, WifiSpeedSample sample)
    {
        if (_disposed) return;

        PostToUi(() =>
        {
            if (_disposed) return;

            SpeedSamples.Add(sample);
            while (SpeedSamples.Count > SparklineBufferSize) SpeedSamples.RemoveAt(0);

            Connection = Connection with
            {
                RxRateMbps = sample.RxRateMbps,
                TxRateMbps = sample.TxRateMbps,
            };

            // The live curve is drawn by WifiSparklineGraph straight from SpeedSamples;
            // here we only keep the Rx/Tx/peak readout.
            if (SpeedSamples.Count > 0)
            {
                var rxVals = SpeedSamples.Select(s => s.RxRateMbps).ToList();
                var txVals = SpeedSamples.Select(s => s.TxRateMbps).ToList();
                SpeedStats = $"Rx {rxVals[^1]:N0} · Tx {txVals[^1]:N0} Mbps · peak {rxVals.Concat(txVals).Max():N0}";
            }

            SpeedStats = SpeedStats.Replace("\u00C2\u00B7", "\u00B7");
            var peakRx = 0.0;
            var peakTx = 0.0;
            foreach (var s in SpeedSamples)
            {
                peakRx = Math.Max(peakRx, s.RxThroughputMbps);
                peakTx = Math.Max(peakTx, s.TxThroughputMbps);
            }

            ThroughputStats = $"Now {LiveThroughputValueText} · Peak Rx {FormatThroughput(peakRx)} / Tx {FormatThroughput(peakTx)}";
            OnPropertyChanged(nameof(HasLiveSpeedSamples));
            OnPropertyChanged(nameof(LiveLinkSpeedValueText));
            OnPropertyChanged(nameof(LinkSpeedRxText));
            OnPropertyChanged(nameof(LinkSpeedTxText));
            OnPropertyChanged(nameof(LinkSpeedStateText));
            OnPropertyChanged(nameof(LinkSpeedStateColor));
            OnPropertyChanged(nameof(LinkSpeedDeltaText));
            OnPropertyChanged(nameof(LiveThroughputValueText));
            OnPropertyChanged(nameof(ReceiveThroughputNowText));
            OnPropertyChanged(nameof(TransferThroughputNowText));
            OnPropertyChanged(nameof(ReceiveThroughputStatsText));
            OnPropertyChanged(nameof(TransferThroughputStatsText));
            OnPropertyChanged(nameof(ReceiveThroughputStateText));
            OnPropertyChanged(nameof(TransferThroughputStateText));
            OnPropertyChanged(nameof(ReceiveThroughputStateColor));
            OnPropertyChanged(nameof(TransferThroughputStateColor));
            OnPropertyChanged(nameof(SpeedSampleCountText));

            // Persist this 1 Hz sample to the on-disk rolling history (cheap enqueue;
            // the store flushes on its own background timer).
            _history.Record(new WifiHistoryStore.Row(
                Timestamp: sample.Timestamp,
                Ssid: Connection.Ssid,
                Bssid: Connection.Bssid,
                RssiDbm: Connection.RssiDbm,
                RxThroughputMbps: sample.RxThroughputMbps,
                TxThroughputMbps: sample.TxThroughputMbps,
                RxPhyMbps: sample.RxRateMbps,
                TxPhyMbps: sample.TxRateMbps));
        });
    }

    /// <summary>
    /// 1 Hz roam detector. The sampler hands us the current connection every second; a
    /// BSSID change for the same SSID is a roam. This catches hand-offs in real time even
    /// when the user never presses Scan (the old behaviour only saw roams between scans).
    /// </summary>
    private void OnConnectionSampled(object? sender, WifiConnectionDetails conn)
    {
        if (_disposed) return;
        PostToUi(() =>
        {
            if (_disposed) return;
            var previous = _lastSampledConnection;
            _lastSampledConnection = conn;
            if (previous is null) return;

            var roam = _engine.DetectRoaming(previous, conn);
            if (roam is not null)
            {
                AddRoamingEventOnUi(roam.Value);
                _logger.Info($"Wi-Fi roam (live): {roam.Value.FromBssid} → {roam.Value.ToBssid} ({roam.Value.DeltaLabel})");
            }
        });
    }

    private void AddRoamingEventOnUi(WifiRoamingEvent ev)
    {
        PostToUi(() =>
        {
            // De-dupe: the scan path and the 1 Hz sampler can both observe the same
            // hand-off within a second or two — count and list it only once.
            if (RoamingHistory.Count > 0)
            {
                var last = RoamingHistory[0];
                if (string.Equals(last.FromBssid, ev.FromBssid, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(last.ToBssid, ev.ToBssid, StringComparison.OrdinalIgnoreCase)
                    && Math.Abs((ev.Timestamp - last.Timestamp).TotalSeconds) < 4)
                {
                    return;
                }
            }

            RoamingHistory.Insert(0, ev);
            while (RoamingHistory.Count > RoamingHistorySize) RoamingHistory.RemoveAt(RoamingHistory.Count - 1);

            _sessionRoamCount++;
            RoamingSummary = $"{_sessionRoamCount} roam(s) this session · last {ev.Timestamp:HH:mm:ss} ({ev.DeltaLabel}: {ev.FromBssid} → {ev.ToBssid})";
        });
    }

    /// <summary>
    /// A Signal-Over-Time legend checkbox was toggled. Persist the choice keyed by BSSID so
    /// it survives the next rescan rebuild, and re-raise SignalTrends so the bound graph
    /// redraws with the new visibility set.
    /// </summary>
    /// <summary>
    /// "Scan devices" — bounded ping sweep of the local /24 + ARP read + reverse DNS.
    /// No ConfigureAwait(false): we mutate LanDevices / LanScanStatus afterwards (UI-bound).
    /// </summary>
    private async Task AutoDetectWirelessDevicesAsync()
    {
        if (_disposed || IsAutoDetectingWirelessDevices || IsLanScanning || IsRouterClientScanning)
        {
            return;
        }

        IsAutoDetectingWirelessDevices = true;
        Status = "Auto detecting wireless candidates from safe local evidence...";
        LanScanStatus = "Auto Detect: collecting local evidence first.";
        RouterClientStatus = "Auto Detect: gateway/AP evidence will be attempted when local evidence completes.";
        try
        {
            await ScanLanDevicesAsync();
            if (_disposed)
            {
                return;
            }

            await AutoReadRouterClientEvidenceAsync();
            if (_disposed)
            {
                return;
            }

            Status = $"Wireless auto-detect complete - {DeviceInventory.Count} candidate(s).";
            LanScanStatus = $"{LanScanStatus} Wireless candidates shown: {DeviceInventory.Count}.";
        }
        finally
        {
            IsAutoDetectingWirelessDevices = false;
        }
    }

    private async Task ScanLanDevicesAsync()
    {
        if (_disposed || IsLanScanning) return;

        CancelQuietly(_lanCancellation);
        var lanCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        _lanCancellation = lanCancellation;
        var ct = lanCancellation.Token;

        IsLanScanning = true;
        LanScanStatus = "Collecting local LAN evidence... ping + ARP + mDNS/Bonjour + SSDP/UPnP + DNS/NetBIOS names";
        try
        {
            var devices = await _lanScanner.ScanAsync(Connection, ct);
            if (_disposed || !ReferenceEquals(_lanCancellation, lanCancellation))
            {
                return;
            }

            ReplaceItems(LanDevices, devices);
            RebuildDeviceInventory(recordPresence: true);
            LanScanStatus = devices.Count == 0
                ? "No local evidence found - are you on a private network? (public ranges are not swept)"
                : BuildLanScanStatus(devices, DateTimeOffset.Now);
        }
        catch (OperationCanceledException)
        {
            LanScanStatus = "Device scan cancelled.";
        }
        catch (Exception ex)
        {
            LanScanStatus = $"Device scan failed: {ex.Message}";
            _logger.Warning($"Wi-Fi LAN scan error: {ex.Message}");
        }
        finally
        {
            lanCancellation.Dispose();
            if (ReferenceEquals(_lanCancellation, lanCancellation))
            {
                _lanCancellation = null;
                IsLanScanning = false;
            }
        }
    }

    /// <summary>
    /// Reads clients directly from the gateway/AP when it exposes the standard SNMP
    /// ipNetToMedia table. This is optional and separate from local LAN discovery.
    /// </summary>
    private async Task AutoReadRouterClientEvidenceAsync()
    {
        if (_disposed || IsRouterClientScanning)
        {
            return;
        }

        var snmpOptions = BuildAutoDetectRouterSnmpOptions();
        CancelQuietly(_routerClientCancellation);
        var routerCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        _routerClientCancellation = routerCancellation;
        var ct = routerCancellation.Token;

        IsRouterClientScanning = true;
        RouterClientStatus = $"Auto Detect: trying gateway/AP read-only {snmpOptions.Protocol} evidence.";
        try
        {
            var result = await _routerClientCollector.ReadSnmpClientsAsync(
                Connection,
                snmpOptions,
                LanDevices,
                ct);
            if (_disposed || !ReferenceEquals(_routerClientCancellation, routerCancellation))
            {
                return;
            }

            ReplaceItems(RouterClients, result.Clients);
            RebuildDeviceInventory(recordPresence: true);
            RouterClientStatus = result.IsSuccess
                ? $"{result.Status} Note: standard ipNetToMedia is ARP/IP evidence, not guaranteed Wi-Fi association."
                : $"Auto Detect: {result.Status}";
        }
        catch (OperationCanceledException)
        {
            RouterClientStatus = "Auto Detect: gateway/AP evidence timed out or was cancelled.";
        }
        catch (Exception ex)
        {
            RouterClientStatus = $"Auto Detect: gateway/AP evidence failed: {ex.Message}";
            _logger.Warning($"Wi-Fi auto-detect router/AP evidence failed: {ex.Message}");
        }
        finally
        {
            routerCancellation.Dispose();
            if (ReferenceEquals(_routerClientCancellation, routerCancellation))
            {
                _routerClientCancellation = null;
                IsRouterClientScanning = false;
            }
        }
    }

    private async Task ReadRouterClientsAsync()
    {
        if (_disposed || IsRouterClientScanning) return;

        var snmpOptions = BuildRouterSnmpOptions();
        if (!ValidateRouterSnmpOptions(snmpOptions, out var validationMessage))
        {
            RouterClientStatus = validationMessage;
            return;
        }

        SaveRouterSnmpCredentialsIfRequested();

        CancelQuietly(_routerClientCancellation);
        var routerCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        _routerClientCancellation = routerCancellation;
        var ct = routerCancellation.Token;

        IsRouterClientScanning = true;
        RouterClientStatus = $"Reading router/AP client table via read-only {snmpOptions.Protocol}...";
        try
        {
            var result = await _routerClientCollector.ReadSnmpClientsAsync(
                Connection,
                snmpOptions,
                LanDevices,
                ct);
            if (_disposed || !ReferenceEquals(_routerClientCancellation, routerCancellation))
            {
                return;
            }

            ReplaceItems(RouterClients, result.Clients);
            RebuildDeviceInventory(recordPresence: true);
            RouterClientStatus = result.Status;
        }
        catch (OperationCanceledException)
        {
            RouterClientStatus = "Router/AP client read cancelled.";
        }
        catch (Exception ex)
        {
            RouterClientStatus = $"Router/AP client read failed: {ex.Message}";
            _logger.Warning($"Wi-Fi router/AP client read failed: {ex.Message}");
        }
        finally
        {
            routerCancellation.Dispose();
            if (ReferenceEquals(_routerClientCancellation, routerCancellation))
            {
                _routerClientCancellation = null;
                IsRouterClientScanning = false;
            }
        }
    }

    private SnmpSessionOptions BuildRouterSnmpOptions()
    {
        var options = _snmpCredentialStore.BuildSessionOptions(Connection.Ipv4Gateway ?? string.Empty);
        options.TimeoutMs = 3500;
        return options;
    }

    private SnmpSessionOptions BuildAutoDetectRouterSnmpOptions()
    {
        var options = BuildRouterSnmpOptions();
        if (ValidateRouterSnmpOptions(options, out _))
        {
            options.TimeoutMs = 2500;
            return options;
        }

        return new SnmpSessionOptions
        {
            Target = Connection.Ipv4Gateway ?? string.Empty,
            Protocol = SnmpProtocolVersion.V2C,
            Community = "public",
            TimeoutMs = 2500
        };
    }

    private static bool ValidateRouterSnmpOptions(SnmpSessionOptions options, out string message)
    {
        if (options.Protocol == SnmpProtocolVersion.V3AuthPriv &&
            (string.IsNullOrWhiteSpace(options.UserName) ||
             string.IsNullOrWhiteSpace(options.AuthPassword) ||
             string.IsNullOrWhiteSpace(options.PrivacyPassword)))
        {
            message = "SNMPv3 authPriv requires username, authentication password and privacy password. No SNMP request was sent.";
            return false;
        }

        message = string.Empty;
        return true;
    }

    private void SaveRouterSnmpCredentialsIfRequested()
    {
        if (!SaveRouterSnmpCredentials)
        {
            return;
        }

        _snmpCredentialStore.Save();
        OnPropertyChanged(nameof(HasSavedRouterSnmpCredentials));
    }

    private void ForgetRouterSnmpCredential()
    {
        _snmpCredentialStore.Forget();
        SaveRouterSnmpCredentials = false;
        OnPropertyChanged(nameof(HasSavedRouterSnmpCredentials));
    }

    private void NotifyRouterSnmpCredentialChanged(string? propertyName)
    {
        switch (propertyName)
        {
            case nameof(SnmpCredentialStore.Protocol):
            case nameof(SnmpCredentialStore.IsV3):
                OnPropertyChanged(nameof(RouterSelectedSnmpProtocol));
                OnPropertyChanged(nameof(IsRouterSnmpV2Selected));
                OnPropertyChanged(nameof(IsRouterSnmpV3Selected));
                break;
            case nameof(SnmpCredentialStore.Community):
                OnPropertyChanged(nameof(RouterSnmpCommunity));
                break;
            case nameof(SnmpCredentialStore.UserName):
                OnPropertyChanged(nameof(RouterSnmpV3UserName));
                break;
            case nameof(SnmpCredentialStore.AuthProtocol):
                OnPropertyChanged(nameof(RouterSnmpV3AuthProtocol));
                break;
            case nameof(SnmpCredentialStore.AuthPassword):
                OnPropertyChanged(nameof(RouterSnmpV3AuthPassword));
                break;
            case nameof(SnmpCredentialStore.PrivacyProtocol):
                OnPropertyChanged(nameof(RouterSnmpV3PrivacyProtocol));
                break;
            case nameof(SnmpCredentialStore.PrivacyPassword):
                OnPropertyChanged(nameof(RouterSnmpV3PrivacyPassword));
                break;
            case nameof(SnmpCredentialStore.HasSavedCredentials):
                OnPropertyChanged(nameof(HasSavedRouterSnmpCredentials));
                break;
        }
    }

    private void RebuildDeviceInventory(bool recordPresence = false)
    {
        var selectedKey = SelectedDeviceInventoryItem?.Key;
        var now = DateTimeOffset.Now;
        var allInventory = WifiDeviceInventoryBuilder.Build(
            LanDevices,
            RouterClients,
            _friendlyNames,
            _manualDeviceTypes,
            _devicePresence,
            now);
        _wirelessInventorySnapshot = FilterWirelessInventory(allInventory);

        if (recordPresence)
        {
            MarkDevicePresence(_wirelessInventorySnapshot, now);
            allInventory = WifiDeviceInventoryBuilder.Build(
                LanDevices,
                RouterClients,
                _friendlyNames,
                _manualDeviceTypes,
                _devicePresence,
                now);
            _wirelessInventorySnapshot = FilterWirelessInventory(allInventory);
        }

        ApplyDeviceInventoryFilterToUi(selectedKey);
    }

    private static IReadOnlyList<WifiDeviceInventoryItem> FilterWirelessInventory(
        IReadOnlyList<WifiDeviceInventoryItem> inventory) =>
        inventory
            .Where(item => item.IsWirelessCandidate)
            .ToArray();

    private void ApplyDeviceInventoryFilterToUi(string? selectedKey = null)
    {
        selectedKey ??= SelectedDeviceInventoryItem?.Key;
        var filtered = _wirelessInventorySnapshot
            .Where(item => MatchesDeviceInventoryFilter(item, DeviceInventoryFilter))
            .ToArray();

        ReplaceItems(DeviceInventory, filtered);
        OnPropertyChanged(nameof(DeviceInventorySummary));
        OnPropertyChanged(nameof(DeviceInventoryFilterSummary));

        SelectedDeviceInventoryItem = !string.IsNullOrWhiteSpace(selectedKey)
            ? DeviceInventory.FirstOrDefault(item => string.Equals(item.Key, selectedKey, StringComparison.OrdinalIgnoreCase))
            : null;
    }

    private static bool MatchesDeviceInventoryFilter(WifiDeviceInventoryItem item, string filter)
    {
        return NormalizeDeviceInventoryFilter(filter) switch
        {
            DeviceInventoryFilterConfirmed => IsConfirmedWireless(item.WirelessStatusDisplay),
            DeviceInventoryFilterLikely => item.WirelessStatusDisplay.Contains("Likely", StringComparison.OrdinalIgnoreCase),
            DeviceInventoryFilterNeedsName => NeedsDeviceName(item),
            DeviceInventoryFilterPhonesTvIot => IsPersonalWirelessDeviceType(item.DeviceType),
            _ => true,
        };
    }

    private static string NormalizeDeviceInventoryFilter(string? value)
    {
        var trimmed = string.IsNullOrWhiteSpace(value) ? DeviceInventoryFilterAll : value.Trim();
        if (string.Equals(trimmed, DeviceInventoryFilterConfirmed, StringComparison.OrdinalIgnoreCase))
        {
            return DeviceInventoryFilterConfirmed;
        }

        if (string.Equals(trimmed, DeviceInventoryFilterLikely, StringComparison.OrdinalIgnoreCase))
        {
            return DeviceInventoryFilterLikely;
        }

        if (string.Equals(trimmed, DeviceInventoryFilterNeedsName, StringComparison.OrdinalIgnoreCase))
        {
            return DeviceInventoryFilterNeedsName;
        }

        if (string.Equals(trimmed, DeviceInventoryFilterPhonesTvIot, StringComparison.OrdinalIgnoreCase))
        {
            return DeviceInventoryFilterPhonesTvIot;
        }

        return DeviceInventoryFilterAll;
    }

    private static bool IsConfirmedWireless(string wirelessStatus) =>
        wirelessStatus.StartsWith("Confirmed", StringComparison.OrdinalIgnoreCase)
        || string.Equals(wirelessStatus, "Wi-Fi infrastructure", StringComparison.OrdinalIgnoreCase);

    private static bool NeedsDeviceName(WifiDeviceInventoryItem item) =>
        string.IsNullOrWhiteSpace(item.LearnedName)
        && string.IsNullOrWhiteSpace(item.ManualName)
        && string.Equals(item.DisplayName, "Unnamed device", StringComparison.OrdinalIgnoreCase);

    private static bool IsPersonalWirelessDeviceType(string deviceType) =>
        deviceType is "Phone / Tablet"
            or "TV / Media"
            or "Console"
            or "Camera / NVR"
            or "Apple device"
            or "IoT / Smart Home";

    private void MarkDevicePresence(IEnumerable<WifiDeviceInventoryItem> inventory, DateTimeOffset now)
    {
        foreach (var item in inventory)
        {
            _devicePresence[item.Key] = _devicePresence.TryGetValue(item.Key, out var existing)
                ? existing.MarkSeen(now)
                : new WifiDevicePresence(now, now, 1);
        }
    }

    private void ClearDeviceSession()
    {
        if (IsAutoDetectingWirelessDevices || IsLanScanning || IsRouterClientScanning)
        {
            LanScanStatus = "Wait for the current device detection to finish before clearing the session.";
            return;
        }

        LanDevices.Clear();
        RouterClients.Clear();
        _devicePresence.Clear();
        _wirelessInventorySnapshot = Array.Empty<WifiDeviceInventoryItem>();
        DeviceInventoryFilter = DeviceInventoryFilterAll;
        ApplyDeviceInventoryFilterToUi();
        ManualDeviceName = string.Empty;
        ManualDeviceType = string.Empty;
        LanScanStatus = "Device session cleared. Press Auto Detect to collect fresh wireless evidence.";
        RouterClientStatus = "Device session cleared. Router/AP evidence will be read only when you run Auto Detect or Read Router/AP.";
        Status = "Wireless device session cleared.";
    }

    private void SaveSelectedDeviceFriendlyName()
    {
        var selected = SelectedDeviceInventoryItem;
        if (selected is null)
        {
            return;
        }

        _friendlyNameStore.SaveName(selected.Key, ManualDeviceName);
        _friendlyNames = _friendlyNameStore.Load();
        RebuildDeviceInventory();
    }

    private void ClearSelectedDeviceFriendlyName()
    {
        var selected = SelectedDeviceInventoryItem;
        if (selected is null)
        {
            return;
        }

        _friendlyNameStore.RemoveName(selected.Key);
        _friendlyNames = _friendlyNameStore.Load();
        ManualDeviceName = selected.LearnedName ?? string.Empty;
        RebuildDeviceInventory();
    }

    private void SaveSelectedDeviceType()
    {
        var selected = SelectedDeviceInventoryItem;
        if (selected is null)
        {
            return;
        }

        _deviceTypeStore.SaveType(selected.Key, ManualDeviceType);
        _manualDeviceTypes = _deviceTypeStore.Load();
        RebuildDeviceInventory();
    }

    private void ClearSelectedDeviceType()
    {
        var selected = SelectedDeviceInventoryItem;
        if (selected is null)
        {
            return;
        }

        _deviceTypeStore.RemoveType(selected.Key);
        _manualDeviceTypes = _deviceTypeStore.Load();
        ManualDeviceType = string.Empty;
        RebuildDeviceInventory();
    }

    /// <summary>Builds the local evidence caption with discovered, named, and unnamed device counts.</summary>
    private static string BuildLanScanStatus(IReadOnlyCollection<WifiLanDevice> devices, DateTimeOffset now)
    {
        var named = devices.Count(device => device.HasResolvedName);
        var unnamed = devices.Count - named;
        return $"{devices.Count} local LAN evidence row(s) - {named} named, {unnamed} unnamed - {now:HH:mm:ss}";
    }

    /// <summary>
    /// "Export report" - writes a plain-text snapshot of connection, health, APs, channel map and LAN devices.
    /// </summary>
    private void ExportReport()
    {
        try
        {
            var now = DateTimeOffset.Now;
            var text = BuildReportText(now);
            var dir = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            var path = System.IO.Path.Combine(dir, $"ISG-Desk-WiFi-{now:yyyyMMdd-HHmmss}.txt");
            System.IO.File.WriteAllText(path, text);
            Status = $"Report exported → {path}";
            _logger.Info($"Wi-Fi report exported: {path}");
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Status = $"Export failed: {ex.Message}";
            _logger.Warning($"Wi-Fi report export failed: {ex.Message}");
        }
    }

    /// <summary>"Copy for ticket" — same snapshot as Export, straight to the clipboard.</summary>
    private void CopyReport()
    {
        try
        {
            System.Windows.Clipboard.SetText(BuildReportText(DateTimeOffset.Now));
            Status = "Wi-Fi summary copied to clipboard.";
        }
        catch (Exception ex)
        {
            Status = $"Copy failed: {ex.Message}";
        }
    }

    /// <summary>Builds the plain-text Wi-Fi snapshot shared by Export (to file) and Copy (to clipboard).</summary>
    private string BuildReportText(DateTimeOffset now)
    {
        {
            var sb = new System.Text.StringBuilder(4096);
            sb.AppendLine("ISG Desk — Wi-Fi Analyzer Report");
            sb.AppendLine($"Generated: {now:yyyy-MM-dd HH:mm:ss zzz}");
            sb.AppendLine($"Machine:   {Environment.MachineName}");
            sb.AppendLine(new string('=', 64));

            sb.AppendLine();
            sb.AppendLine("CONNECTION");
            sb.AppendLine(new string('-', 64));
            var c = Connection;
            sb.AppendLine($"  SSID            : {c.Ssid ?? "(not connected)"}");
            sb.AppendLine($"  BSSID           : {c.Bssid ?? "—"}  {c.Vendor}");
            sb.AppendLine($"  Band / Channel  : {BandStr(c.Band)}  ch {c.Channel?.ToString() ?? "—"}  ({c.FrequencyText})");
            sb.AppendLine($"  PHY / Security  : {c.PhyFriendlyName} ({Core.Models.Wifi.WifiPhyStandard.ToIeeeName(c.PhyType)})  /  {c.Security?.Label ?? "—"}");
            sb.AppendLine($"  Signal          : {c.RssiDbm?.ToString() ?? "—"} dBm ({c.SignalLevel})");
            sb.AppendLine($"  Link rate Rx/Tx : {c.RxRateMbps?.ToString() ?? "—"} / {c.TxRateMbps?.ToString() ?? "—"} Mbps");
            sb.AppendLine($"  IPv4 / Mask     : {c.Ipv4Address ?? "—"}  /  {c.Ipv4SubnetMask ?? "—"}");
            sb.AppendLine($"  Gateway / DNS   : {c.Ipv4Gateway ?? "—"}  /  {c.DnsServersText}");

            sb.AppendLine();
            sb.AppendLine("HEALTH");
            sb.AppendLine(new string('-', 64));
            sb.AppendLine($"  Link Score : {LinkScore?.ToString() ?? "—"} / 100  ({LinkScoreVerdict})");
            sb.AppendLine($"  Verdict    : {Health.Verdict}");
            if (Health.Recommendations is { Count: > 0 })
            {
                sb.AppendLine("  Recommendations:");
                foreach (var r in Health.Recommendations) sb.AppendLine($"    • {r}");
            }
            if (HasBestChannelAdvice)
                sb.AppendLine($"  Best channel: {BestChannelAdvice}");
            if (HasChannelRecommendation)
            {
                sb.AppendLine("  Channel recommendation:");
                sb.AppendLine($"    Severity    : {ChannelRecommendation.SeverityDisplay}");
                sb.AppendLine($"    Current     : {ChannelRecommendation.Current}");
                sb.AppendLine($"    Recommended : {ChannelRecommendation.Recommended}");
                sb.AppendLine($"    Reason      : {ChannelRecommendation.Reason}");
                sb.AppendLine($"    Detail      : {ChannelRecommendation.Detail}");
            }

            sb.AppendLine();
            sb.AppendLine($"WI-FI SECURITY AUDIT ({SecurityAudit.Findings.Count})");
            sb.AppendLine(new string('-', 64));
            sb.AppendLine($"  Summary: {SecurityAudit.Summary}");
            if (SecurityAudit.Findings.Count == 0)
                sb.AppendLine("  (no security audit findings available)");
            foreach (var finding in SecurityAudit.Findings)
            {
                sb.AppendLine($"  [{finding.SeverityDisplay}] {finding.Category}: {finding.Finding}");
                sb.AppendLine($"    Evidence: {finding.Evidence}");
                sb.AppendLine($"    Action  : {finding.Recommendation}");
            }

            sb.AppendLine();
            sb.AppendLine($"VISIBLE NETWORKS ({VisibleAccessPoints.Count})");
            sb.AppendLine(new string('-', 64));
            sb.AppendLine($"  {"SSID",-28} {"BSSID",-18} {"Band",-7} {"Ch",-4} {"Width",-8} {"RSSI",-6} Security");
            foreach (var ap in VisibleAccessPoints)
                sb.AppendLine($"  {Trunc(ap.DisplaySsid, 28),-28} {ap.Bssid,-18} {ap.BandLabel,-7} {ap.Channel,-4} {ap.ChannelWidthLabel,-8} {ap.RssiDbm,-6} {ap.Security.Label}");

            sb.AppendLine();
            sb.AppendLine($"CHANNEL CONGESTION ({ChannelMap.Count})");
            sb.AppendLine(new string('-', 64));
            foreach (var ch in ChannelMap)
                sb.AppendLine($"  {ch.BandLabel,-7} ch {ch.Channel,-4} {ch.ApsOnOverlappingChannels,2} AP(s)  {ch.CongestionLabel}{(ch.IsCurrentConnectionChannel ? "  <- YOU" : "")}");

            sb.AppendLine();
            sb.AppendLine($"CHANNEL HISTORY ({ChannelHistory.Count})");
            sb.AppendLine(new string('-', 64));
            if (ChannelHistory.Count == 0)
            {
                sb.AppendLine("  (no active scan channel history yet)");
            }
            else
            {
                if (HasChannelBeforeAfterSummary)
                {
                    sb.AppendLine($"  Summary : [{ChannelBeforeAfterSummary.SeverityDisplay}] {ChannelBeforeAfterSummary.Title}");
                    sb.AppendLine($"  Before  : {ChannelBeforeAfterSummary.Before}");
                    sb.AppendLine($"  After   : {ChannelBeforeAfterSummary.After}");
                    sb.AppendLine($"  Delta   : {ChannelBeforeAfterSummary.Delta}");
                    sb.AppendLine($"  Detail  : {ChannelBeforeAfterSummary.Detail}");
                    sb.AppendLine();
                }

                foreach (var entry in ChannelHistory)
                {
                    sb.AppendLine($"  {entry.TimeDisplay,-8} {Trunc(entry.NetworkDisplay, 18),-18} {entry.ChannelDisplay,-12} {entry.RssiDisplay,-12} {entry.LinkScoreDisplay,-9} {Trunc(entry.LoadDisplay, 18),-18} {entry.DfsDisplay,-8} {Trunc(entry.RecommendationDisplay, 48)}");
                }
            }

            sb.AppendLine();
            sb.AppendLine($"SAVED WI-FI PROFILES ({SavedProfiles.Count})");
            sb.AppendLine(new string('-', 64));
            if (SavedProfiles.Count == 0)
                sb.AppendLine($"  {SavedProfilesEmptyText}");
            foreach (var profile in SavedProfiles)
                sb.AppendLine($"  {profile.DisplayName}");

            sb.AppendLine();
            sb.AppendLine($"SAVED PROFILE HYGIENE ({SavedProfileHygiene.Items.Count})");
            sb.AppendLine(new string('-', 64));
            sb.AppendLine($"  Summary: {SavedProfileHygieneSummary}");
            if (SavedProfileHygiene.Items.Count == 0)
                sb.AppendLine("  (no saved profile hygiene items)");
            foreach (var item in SavedProfileHygiene.Items)
            {
                sb.AppendLine($"  [{item.SeverityDisplay}] {item.ProfileDisplay} - {item.StateDisplay} - {item.SecurityDisplay}");
                sb.AppendLine($"    Evidence: {item.EvidenceDisplay}");
                sb.AppendLine($"    Action  : {item.RecommendationDisplay}");
            }

            sb.AppendLine();
            sb.AppendLine($"WIRELESS DEVICE CANDIDATES ({DeviceInventory.Count})");
            sb.AppendLine(new string('-', 64));
            if (DeviceInventory.Count == 0)
                sb.AppendLine("  (no confirmed/likely wireless candidates yet - run Auto Detect)");
            foreach (var item in DeviceInventory)
                sb.AppendLine($"  {item.IpAddress,-16} {Trunc(item.DisplayName, 22),-22} {item.MacDisplay,-18} {Trunc(item.DeviceType, 16),-16} {Trunc(item.WirelessStatusDisplay, 26),-26} {item.Confidence,-8} seen {item.SeenCountDisplay,-3} {Trunc(item.SourceDisplay, 28)}");

            sb.AppendLine();
            sb.AppendLine($"LOCAL LAN EVIDENCE - NOT WIRELESS PROOF ({LanDevices.Count})");
            sb.AppendLine(new string('-', 64));
            if (LanDevices.Count == 0)
                sb.AppendLine("  (no local evidence scan run - press Auto Detect before exporting to include this)");
            foreach (var d in LanDevices)
                sb.AppendLine($"  {d.IpAddress,-16} {Trunc(d.HostDisplay, 22),-22} {d.MacDisplay,-18} {Trunc(d.VendorDisplay, 22),-22} {Trunc(d.NameSourceDisplay, 18),-18} {Trunc(d.RoleLabel, 28),-28} {Trunc(d.RoleSourceDisplay, 34)}");

            sb.AppendLine();
            sb.AppendLine($"ROUTER/AP REPORTED CLIENTS ({RouterClients.Count})");
            sb.AppendLine(new string('-', 64));
            sb.AppendLine($"  Status: {RouterClientStatus}");
            if (RouterClients.Count == 0)
                sb.AppendLine("  (no router/AP SNMP client data loaded)");
            foreach (var client in RouterClients)
                sb.AppendLine($"  {client.IpAddress,-16} {Trunc(client.HostDisplay, 22),-22} {client.MacDisplay,-18} {Trunc(client.VendorDisplay, 22),-22} {Trunc(client.WirelessAssociationDisplay, 28),-28} ifIndex {client.InterfaceIndex,-6} {client.LocalMatchLabel}");

            sb.AppendLine();
            sb.AppendLine(new string('=', 64));
            sb.AppendLine("Generated by ISG Desk · Wi-Fi Analyzer");

            return sb.ToString();
        }

        static string Trunc(string? s, int n)
            => string.IsNullOrEmpty(s) ? "—" : (s.Length <= n ? s : s[..(n - 1)] + "…");

        static string BandStr(WifiBand b) => b switch
        {
            WifiBand.TwoPointFourGhz => "2.4 GHz",
            WifiBand.FiveGhz => "5 GHz",
            WifiBand.SixGhz => "6 GHz",
            _ => "—",
        };
    }

    /// <summary>
    /// Flush the latest buffered samples and open the history folder in Explorer so the
    /// tech can open the per-day CSV (e.g. in Excel) to see signal/throughput from hours
    /// ago. Failures are surfaced in Status, never thrown.
    /// </summary>
    private void OpenHistoryFolder()
    {
        try
        {
            _history.Flush();
            System.IO.Directory.CreateDirectory(_history.HistoryDirectory);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_history.HistoryDirectory)
            {
                UseShellExecute = true,
            });
            Status = $"History folder: {_history.HistoryDirectory}";
        }
        catch (Exception ex)
        {
            Status = $"Could not open history folder: {ex.Message}";
            _logger.Warning($"Wi-Fi history open failed: {ex.Message}");
        }
    }

    private void OnTrendVisibilityChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(WifiSignalTrend.IsVisible) || sender is not WifiSignalTrend t) return;
        if (t.IsVisible) _trendHidden.Remove(t.Bssid);
        else _trendHidden.Add(t.Bssid);
        OnPropertyChanged(nameof(SignalTrends));
    }

    // ============== Helpers ==============

    /// <summary>Post a delegate to the UI thread (no-op if context is null — won't happen in WPF).</summary>
    private void PostToUi(Action action)
    {
        if (SynchronizationContext.Current == _uiContext)
        {
            action();
        }
        else
        {
            _uiContext.Post(_ => action(), null);
        }
    }

    private static void ReplaceItems<T>(ObservableCollection<T> target, IEnumerable<T> source)
    {
        target.Clear();
        foreach (var item in source) target.Add(item);
    }

    private static string BuildSavedProfilesSummary(int count) => count switch
    {
        <= 0 => "No saved Wi-Fi profiles found on this adapter.",
        1 => "1 saved Wi-Fi profile",
        _ => $"{count} saved Wi-Fi profiles",
    };

    private static string FormatThroughput(double mbps)
    {
        if (double.IsNaN(mbps) || double.IsInfinity(mbps) || mbps <= 0)
        {
            return "0 Kbps";
        }

        return mbps < 1
            ? $"{mbps * 1000:N0} Kbps"
            : mbps < 10
                ? $"{mbps:N1} Mbps"
                : $"{mbps:N0} Mbps";
    }

    private string BuildThroughputDirectionStats(bool isTx)
    {
        if (SpeedSamples.Count == 0)
        {
            return IsMonitoring ? "Waiting for byte-counter sample." : "Start Analyzer to sample adapter byte counters.";
        }

        var latest = 0.0;
        var sum = 0.0;
        var peak = 0.0;
        foreach (var sample in SpeedSamples)
        {
            var value = isTx ? sample.TxThroughputMbps : sample.RxThroughputMbps;
            latest = value;
            sum += value;
            peak = Math.Max(peak, value);
        }

        var avg = sum / SpeedSamples.Count;
        return $"Now {FormatThroughput(latest)} · Avg {FormatThroughput(avg)} · Peak {FormatThroughput(peak)}";
    }

    private string BuildThroughputDirectionState(bool isTx)
    {
        if (SpeedSamples.Count == 0)
        {
            return IsMonitoring ? "Sampling" : "Idle";
        }

        var latest = isTx
            ? SpeedSamples[SpeedSamples.Count - 1].TxThroughputMbps
            : SpeedSamples[SpeedSamples.Count - 1].RxThroughputMbps;

        return latest switch
        {
            < 0.01 => "Idle traffic",
            < 1 => "Light traffic",
            < 20 => "Active traffic",
            _ => "Heavy traffic",
        };
    }

    private string BuildThroughputDirectionColor(bool isTx)
    {
        if (SpeedSamples.Count == 0)
        {
            return "#94A3B8";
        }

        var latest = isTx
            ? SpeedSamples[SpeedSamples.Count - 1].TxThroughputMbps
            : SpeedSamples[SpeedSamples.Count - 1].RxThroughputMbps;

        return latest switch
        {
            < 0.01 => "#64748B",
            < 1 => "#3B82F6",
            < 20 => "#10B981",
            _ => "#F59E0B",
        };
    }

    private static string FormatPhyRate(double? mbps) => mbps is { } value
        ? $"{value:N0} Mbps"
        : "No sample";

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _sampler.SignalSampled -= OnSignalSampled;
        _sampler.SpeedSampled -= OnSpeedSampled;
        _sampler.ConnectionSampled -= OnConnectionSampled;

        CancelQuietly(_scanCancellation);
        CancelQuietly(_ispCancellation);
        CancelQuietly(_lanCancellation);
        CancelQuietly(_routerClientCancellation);
        DisposeIfIdle(ref _scanCancellation, IsScanning);
        DisposeIfIdle(ref _ispCancellation, IsDetectingIsp);
        DisposeIfIdle(ref _lanCancellation, IsLanScanning);
        DisposeIfIdle(ref _routerClientCancellation, IsRouterClientScanning);
        StopAutoScanTimer();

        _sampler.Stop();
    }

    private static void CancelQuietly(CancellationTokenSource? cancellation)
    {
        try { cancellation?.Cancel(); } catch (ObjectDisposedException) { }
    }

    private void CancelActiveOperations()
    {
        CancelQuietly(_scanCancellation);
        CancelQuietly(_ispCancellation);
        CancelQuietly(_lanCancellation);
        CancelQuietly(_routerClientCancellation);
    }

    private static void DisposeIfIdle(ref CancellationTokenSource? cancellation, bool isRunning)
    {
        if (isRunning)
        {
            return;
        }

        cancellation?.Dispose();
        cancellation = null;
    }

    /// <summary>
    /// Session-long accumulator for one BSSID. Tracks running-average RSSI, first/last seen,
    /// and how many scans included this BSSID (for Detection %). Mirrors the historical
    /// columns in NirSoft WirelessNetView / Wireless Network Watcher.
    /// </summary>
    private sealed class BssidStats
    {
        private long _rssiSum;
        private int _observations;

        public DateTimeOffset FirstSeen { get; init; }
        public DateTimeOffset LastSeen { get; private set; }

        /// <summary>Highest scan index this BSSID was seen in — used with total scans for %.</summary>
        private int _scansSeen;

        /// <summary>
        /// Bounded RSSI history (one point per scan, last 40 scans). Powers the
        /// multi-network signal-over-time graph (the 2nd signature inSSIDer view).
        /// A ring via List + RemoveAt(0) is fine at this size — 40 ints, rebuilt rarely.
        /// </summary>
        private readonly List<int> _rssiHistory = new();
        private const int HistoryCap = 40;

        public IReadOnlyList<int> RssiHistory => _rssiHistory;

        public void Observe(int rssiDbm, DateTimeOffset seenAt, int scanIndex)
        {
            _rssiSum += rssiDbm;
            _observations++;
            _scansSeen++;
            LastSeen = seenAt;

            _rssiHistory.Add(rssiDbm);
            if (_rssiHistory.Count > HistoryCap) _rssiHistory.RemoveAt(0);
        }

        /// <summary>Session-average RSSI rounded to the nearest dBm. Null before any observation.</summary>
        public int? AverageRssi => _observations == 0
            ? null
            : (int)Math.Round((double)_rssiSum / _observations);

        /// <summary>
        /// Percent of scans this BSSID appeared in. Clamped 0..100. A value well below 100
        /// flags a transient / flapping AP (mesh node going to sleep, distant neighbour, etc.).
        /// </summary>
        public int DetectionPercent(int totalScans) => totalScans <= 0
            ? 100
            : (int)Math.Clamp(Math.Round(100.0 * _scansSeen / totalScans), 0, 100);
    }
}
