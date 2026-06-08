using System.Windows.Input;
using NetScopeDiagnosticCenter.Collectors;
using NetScopeDiagnosticCenter.Collectors.Shared;
using NetScopeDiagnosticCenter.Core;
using NetScopeDiagnosticCenter.Core.Models;
using NetScopeDiagnosticCenter.Infrastructure;

namespace NetScopeDiagnosticCenter.UI.ViewModels;

/// <summary>
/// Network devices module ViewModel: SNMP identification, IF-MIB interface read,
/// LAN scan (auto + range), and per-device interface filtering.
/// </summary>
/// <remarks>
/// SNMP credential fields share <see cref="SnmpCredentialStore"/> with the Printers module
/// so technicians do not have to re-enter the same read-only community/user values twice.
/// </remarks>
public sealed class NetworkDevicesViewModel : ObservableObject
{
    public interface IHost
    {
        void NotifyStatus(string message);
        void NotifyBusy(bool busy);
        /// <summary>Publish an identified device to the host and refresh navigation state.</summary>
        void AttachNetworkDevice(NetworkDeviceResult device);
        /// <summary>Publish a LAN-scan result to the host and refresh navigation state.</summary>
        void AttachNetworkDeviceScan(NetworkDeviceScanResult scan);
    }

    private readonly NetworkDeviceCollector _collector;
    private readonly ILoggingService _logger;
    private readonly SnmpCredentialStore _snmpCredentialStore;
    private readonly IHost _host;

    private NetworkDeviceResult? _lastNetworkDevice;
    private NetworkDeviceScanResult? _lastNetworkDeviceScan;
    private NetworkDeviceResult? _selectedNetworkDeviceScanResult;
    private CancellationTokenSource? _networkDeviceScanCancellation;
    private string _networkDeviceTarget = string.Empty;
    private bool _networkDeviceTargetFromScanSelection;
    private string _networkDeviceScanRange = string.Empty;
    private string _networkDeviceDetectedIp = string.Empty;
    private string _networkDeviceDetectedRange = string.Empty;
    private string _networkDeviceInterfaceFilter = "All";
    private bool _isNetworkDeviceScanRunning;
    private bool _saveNetworkDeviceSnmpCredentials;

    public NetworkDevicesViewModel(
        NetworkDeviceCollector collector,
        ILoggingService logger,
        IHost host,
        SnmpCredentialStore snmpCredentialStore)
    {
        _collector = collector ?? throw new ArgumentNullException(nameof(collector));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _snmpCredentialStore = snmpCredentialStore ?? throw new ArgumentNullException(nameof(snmpCredentialStore));
        _snmpCredentialStore.PropertyChanged += (_, e) => NotifySnmpCredentialChanged(e.PropertyName);
        _snmpCredentialStore.Load();

        IdentifyNetworkDeviceCommand = new AsyncRelayCommand(_ => IdentifyNetworkDeviceAsync());
        ReadNetworkDeviceInterfacesCommand = new AsyncRelayCommand(_ => ReadNetworkDeviceInterfacesAsync());
        ReadSelectedNetworkDeviceInterfacesCommand = new AsyncRelayCommand(_ => ReadSelectedNetworkDeviceInterfacesAsync());
        DetectNetworkDeviceScanRangeCommand = new AsyncRelayCommand(_ => DetectNetworkDeviceScanRangeAsync());
        ScanLocalNetworkDevicesCommand = new AsyncRelayCommand(_ => ScanLocalNetworkDevicesAsync());
        ScanNetworkDeviceRangeCommand = new AsyncRelayCommand(_ => ScanNetworkDeviceRangeAsync());
        CancelNetworkDeviceScanCommand = new RelayCommand(_ => CancelNetworkDeviceScan());
        CopyNetworkDeviceSummaryCommand = new RelayCommand(_ => CopyNetworkDeviceSummary());
        ForgetNetworkDeviceSnmpCredentialCommand = new RelayCommand(_ => ForgetNetworkDeviceSnmpCredential());
    }

    public IReadOnlyList<string> NetworkDeviceSnmpProtocols { get; } = [SnmpProtocolVersion.V2C, SnmpProtocolVersion.V3AuthPriv];
    public IReadOnlyList<string> NetworkDeviceSnmpAuthProtocols { get; } = ["SHA256", "SHA384", "SHA512", "SHA1", "MD5"];
    public IReadOnlyList<string> NetworkDeviceSnmpPrivacyProtocols { get; } = ["AES128", "AES192", "AES256", "DES"];
    public IReadOnlyList<string> NetworkDeviceInterfaceFilters { get; } =
    [
        "All", "Needs Attention", "Down", "100 Mbps", "Errors / Discards", "High Traffic", "Unknown Speed"
    ];

    public ICommand IdentifyNetworkDeviceCommand { get; }
    public ICommand ReadNetworkDeviceInterfacesCommand { get; }
    public ICommand ReadSelectedNetworkDeviceInterfacesCommand { get; }
    public ICommand DetectNetworkDeviceScanRangeCommand { get; }
    public ICommand ScanLocalNetworkDevicesCommand { get; }
    public ICommand ScanNetworkDeviceRangeCommand { get; }
    public ICommand CancelNetworkDeviceScanCommand { get; }
    public ICommand CopyNetworkDeviceSummaryCommand { get; }
    public ICommand ForgetNetworkDeviceSnmpCredentialCommand { get; }
    public bool IsNetworkDeviceScanRunning { get => _isNetworkDeviceScanRunning; private set => SetProperty(ref _isNetworkDeviceScanRunning, value); }
    public bool SaveNetworkDeviceSnmpCredentials { get => _saveNetworkDeviceSnmpCredentials; set => SetProperty(ref _saveNetworkDeviceSnmpCredentials, value); }
    public bool HasSavedNetworkDeviceSnmpCredentials => _snmpCredentialStore.HasSavedCredentials;
    public bool HasNetworkDeviceConfirmedFindings => LastNetworkDevice?.ConfirmedFindings.Count > 0;
    public bool HasNoNetworkDeviceConfirmedFindings => LastNetworkDevice is not null && !HasNetworkDeviceConfirmedFindings;
    public bool HasNetworkDeviceProbableFindings => LastNetworkDevice?.ProbableFindings.Count > 0;
    public bool HasNoNetworkDeviceProbableFindings => LastNetworkDevice is not null && !HasNetworkDeviceProbableFindings;
    public bool HasNetworkDeviceUnknownFindings => LastNetworkDevice?.UnknownFindings.Count > 0;
    public bool HasNoNetworkDeviceUnknownFindings => LastNetworkDevice is not null && !HasNetworkDeviceUnknownFindings;
    public bool HasNetworkDeviceEvidence => LastNetworkDevice?.Evidence.Count > 0;
    public bool HasNoNetworkDeviceEvidence => LastNetworkDevice is not null && !HasNetworkDeviceEvidence;
    public bool HasNetworkDeviceLimitations => LastNetworkDevice?.Limitations.Count > 0;
    public bool HasNoNetworkDeviceLimitations => LastNetworkDevice is not null && !HasNetworkDeviceLimitations;
    public bool HasNetworkDeviceRecommendations => LastNetworkDevice?.Recommendations.Count > 0;
    public bool HasNoNetworkDeviceRecommendations => LastNetworkDevice is not null && !HasNetworkDeviceRecommendations;
    public bool HasNetworkDevicePortsNeedingAttention => LastNetworkDevice?.PortsNeedingAttention.Count > 0;
    public bool HasNoNetworkDevicePortsNeedingAttention => LastNetworkDevice is not null && !HasNetworkDevicePortsNeedingAttention;
    public bool HasFilteredNetworkDeviceInterfaces => FilteredNetworkDeviceInterfaces.Count > 0;
    public bool HasNoFilteredNetworkDeviceInterfaces => LastNetworkDevice is not null && !HasFilteredNetworkDeviceInterfaces;
    public bool HasNetworkDeviceScanResults => LastNetworkDeviceScan?.Devices.Count > 0;
    public bool HasNoNetworkDeviceScanResults => LastNetworkDeviceScan is not null && !HasNetworkDeviceScanResults;
    public bool HasSelectedNetworkDeviceScanResult => SelectedNetworkDeviceScanResult is not null;

    public string NetworkDeviceScanResultsEmptyText
    {
        get
        {
            var scan = LastNetworkDeviceScan;
            if (scan is null)
            {
                return "No LAN scan has been run yet.";
            }

            if (scan.Devices.Count > 0)
            {
                return string.Empty;
            }

            if (scan.IsRunning)
            {
                return "Scan is running. Devices that answer SNMP will appear here.";
            }

            if (!string.IsNullOrWhiteSpace(scan.SkippedReason))
            {
                return scan.SkippedReason;
            }

            if (!string.IsNullOrWhiteSpace(scan.Source) || scan.ScannedHosts > 0)
            {
                return "No SNMP network devices were found in this scan. Check the range, SNMP credentials, UDP 161, and device ACLs.";
            }

            return "No LAN scan results yet.";
        }
    }

    public string SelectedNetworkDeviceScanResultSummary
    {
        get
        {
            var selected = SelectedNetworkDeviceScanResult;
            if (selected is null)
            {
                return HasNetworkDeviceScanResults
                    ? "No device selected."
                    : "No scan device selected.";
            }

            return $"Selected: {selected.AddressDisplay} - {selected.DeviceTypeDisplay} - {selected.IdentityNameDisplay}";
        }
    }

    public string NetworkDeviceTarget { get => _networkDeviceTarget; set => SetNetworkDeviceTarget(value, fromScanSelection: false); }
    public string NetworkDeviceScanRange { get => _networkDeviceScanRange; set => SetProperty(ref _networkDeviceScanRange, value); }
    public string NetworkDeviceDetectedIp
    {
        get => _networkDeviceDetectedIp;
        set
        {
            if (SetProperty(ref _networkDeviceDetectedIp, value))
            {
                OnPropertyChanged(nameof(NetworkDeviceDetectedIpDisplay));
            }
        }
    }

    public string NetworkDeviceDetectedRange
    {
        get => _networkDeviceDetectedRange;
        set
        {
            if (SetProperty(ref _networkDeviceDetectedRange, value))
            {
                OnPropertyChanged(nameof(NetworkDeviceDetectedRangeDisplay));
            }
        }
    }

    public string NetworkDeviceDetectedIpDisplay => ValueOrUnknown(NetworkDeviceDetectedIp);
    public string NetworkDeviceDetectedRangeDisplay => ValueOrUnknown(NetworkDeviceDetectedRange);
    public string NetworkDeviceCommunity
    {
        get => _snmpCredentialStore.Community;
        set => _snmpCredentialStore.Community = value;
    }

    public string NetworkDeviceSelectedSnmpProtocol
    {
        get => _snmpCredentialStore.Protocol;
        set => _snmpCredentialStore.Protocol = value;
    }

    public bool IsNetworkDeviceSnmpV2Selected => !_snmpCredentialStore.IsV3;
    public bool IsNetworkDeviceSnmpV3Selected => _snmpCredentialStore.IsV3;

    public string NetworkDeviceSnmpV3UserName { get => _snmpCredentialStore.UserName; set => _snmpCredentialStore.UserName = value; }
    public string NetworkDeviceSnmpV3AuthProtocol { get => _snmpCredentialStore.AuthProtocol; set => _snmpCredentialStore.AuthProtocol = value; }
    public string NetworkDeviceSnmpV3AuthPassword { get => _snmpCredentialStore.AuthPassword; set => _snmpCredentialStore.AuthPassword = value; }
    public string NetworkDeviceSnmpV3PrivacyProtocol { get => _snmpCredentialStore.PrivacyProtocol; set => _snmpCredentialStore.PrivacyProtocol = value; }
    public string NetworkDeviceSnmpV3PrivacyPassword { get => _snmpCredentialStore.PrivacyPassword; set => _snmpCredentialStore.PrivacyPassword = value; }

    public string NetworkDeviceInterfaceFilter
    {
        get => _networkDeviceInterfaceFilter;
        set
        {
            var nextFilter = NetworkDeviceInterfaceFilters.Contains(value) ? value : "All";
            if (SetProperty(ref _networkDeviceInterfaceFilter, nextFilter))
            {
                OnPropertyChanged(nameof(FilteredNetworkDeviceInterfaces));
                OnPropertyChanged(nameof(NetworkDeviceInterfaceFilterSummary));
                OnPropertyChanged(nameof(FilteredNetworkDeviceInterfacesEmptyText));
                NotifyNetworkDeviceInterfaceTableStateChanged();
            }
        }
    }

    public IReadOnlyList<NetworkDeviceInterfaceInfo> FilteredNetworkDeviceInterfaces
    {
        get
        {
            var interfaces = LastNetworkDevice?.Interfaces ?? [];
            return FilterNetworkDeviceInterfaces(interfaces, NetworkDeviceInterfaceFilter);
        }
    }

    public string NetworkDeviceInterfaceFilterSummary
    {
        get
        {
            var total = LastNetworkDevice?.Interfaces.Count ?? 0;
            var shown = FilteredNetworkDeviceInterfaces.Count;
            return $"{shown} of {total} interfaces shown";
        }
    }

    public string FilteredNetworkDeviceInterfacesEmptyText
    {
        get
        {
            if (LastNetworkDevice is null)
            {
                return "No network device result yet.";
            }

            if (LastNetworkDevice.Interfaces.Count == 0)
            {
                return "No IF-MIB interface rows were returned for this result.";
            }

            return $"No interfaces match the '{NetworkDeviceInterfaceFilter}' filter.";
        }
    }

    public NetworkDeviceResult? LastNetworkDevice
    {
        get => _lastNetworkDevice;
        set
        {
            if (SetProperty(ref _lastNetworkDevice, value))
            {
                EnsureInterfaceFilterShowsResult(value);
                OnPropertyChanged(nameof(FilteredNetworkDeviceInterfaces));
                OnPropertyChanged(nameof(NetworkDeviceInterfaceFilterSummary));
                OnPropertyChanged(nameof(FilteredNetworkDeviceInterfacesEmptyText));
                NotifyNetworkDeviceDiagnosticListsChanged();
                NotifyNetworkDeviceInterfaceTableStateChanged();
            }
        }
    }

    public NetworkDeviceScanResult? LastNetworkDeviceScan
    {
        get => _lastNetworkDeviceScan;
        set
        {
            if (SetProperty(ref _lastNetworkDeviceScan, value))
            {
                var selectedAddress = SelectedNetworkDeviceScanResult?.Address;
                var nextSelection =
                    value?.Devices.FirstOrDefault(device => device.Address.Equals(selectedAddress, StringComparison.OrdinalIgnoreCase))
                    ?? value?.Devices.FirstOrDefault();
                SelectedNetworkDeviceScanResult = nextSelection;
                if (nextSelection is null && _networkDeviceTargetFromScanSelection)
                {
                    SetNetworkDeviceTarget(string.Empty, fromScanSelection: false);
                }

                NotifyNetworkDeviceScanStateChanged();
            }
        }
    }

    public NetworkDeviceResult? SelectedNetworkDeviceScanResult
    {
        get => _selectedNetworkDeviceScanResult;
        set
        {
            if (!SetProperty(ref _selectedNetworkDeviceScanResult, value))
            {
                return;
            }

            if (value is not null && !string.IsNullOrWhiteSpace(value.Address))
            {
                SetNetworkDeviceTarget(value.Address, fromScanSelection: true);
            }

            NotifyNetworkDeviceScanStateChanged();
        }
    }

    private void SetNetworkDeviceTarget(string value, bool fromScanSelection)
    {
        SetProperty(ref _networkDeviceTarget, value ?? string.Empty, nameof(NetworkDeviceTarget));
        _networkDeviceTargetFromScanSelection = fromScanSelection;
    }

    private void NotifyNetworkDeviceScanStateChanged()
    {
        OnPropertyChanged(nameof(HasNetworkDeviceScanResults));
        OnPropertyChanged(nameof(HasNoNetworkDeviceScanResults));
        OnPropertyChanged(nameof(HasSelectedNetworkDeviceScanResult));
        OnPropertyChanged(nameof(NetworkDeviceScanResultsEmptyText));
        OnPropertyChanged(nameof(SelectedNetworkDeviceScanResultSummary));
    }

    private void NotifyNetworkDeviceDiagnosticListsChanged()
    {
        OnPropertyChanged(nameof(HasNetworkDeviceConfirmedFindings));
        OnPropertyChanged(nameof(HasNoNetworkDeviceConfirmedFindings));
        OnPropertyChanged(nameof(HasNetworkDeviceProbableFindings));
        OnPropertyChanged(nameof(HasNoNetworkDeviceProbableFindings));
        OnPropertyChanged(nameof(HasNetworkDeviceUnknownFindings));
        OnPropertyChanged(nameof(HasNoNetworkDeviceUnknownFindings));
        OnPropertyChanged(nameof(HasNetworkDeviceEvidence));
        OnPropertyChanged(nameof(HasNoNetworkDeviceEvidence));
        OnPropertyChanged(nameof(HasNetworkDeviceLimitations));
        OnPropertyChanged(nameof(HasNoNetworkDeviceLimitations));
        OnPropertyChanged(nameof(HasNetworkDeviceRecommendations));
        OnPropertyChanged(nameof(HasNoNetworkDeviceRecommendations));
    }

    private void NotifyNetworkDeviceInterfaceTableStateChanged()
    {
        OnPropertyChanged(nameof(HasNetworkDevicePortsNeedingAttention));
        OnPropertyChanged(nameof(HasNoNetworkDevicePortsNeedingAttention));
        OnPropertyChanged(nameof(HasFilteredNetworkDeviceInterfaces));
        OnPropertyChanged(nameof(HasNoFilteredNetworkDeviceInterfaces));
    }

    private async Task IdentifyNetworkDeviceAsync()
    {
        var target = NetworkDeviceTarget.Trim();
        if (string.IsNullOrWhiteSpace(target))
        {
            _host.NotifyStatus("Enter a network device target before identification.");
            return;
        }

        if (!TryBuildValidatedNetworkDeviceSnmpOptions(target, out var options, out var validationResult))
        {
            LastNetworkDevice = validationResult;
            _host.AttachNetworkDevice(LastNetworkDevice);
            _host.NotifyStatus(LastNetworkDevice.Verdict);
            return;
        }

        _host.NotifyBusy(true);
        _host.NotifyStatus($"Identifying network device {options.Target}...");
        try
        {
            LastNetworkDevice = await _collector.IdentifyAsync(options);
            SaveNetworkDeviceSnmpCredentialsIfRequested();
            _host.AttachNetworkDevice(LastNetworkDevice);
            _host.NotifyStatus(LastNetworkDevice.Verdict);
        }
        catch (OperationCanceledException)
        {
            _host.NotifyStatus("Network device identification cancelled.");
        }
        catch (Exception ex)
        {
            _logger.Error("Network device identification failed.", ex);
            _host.NotifyStatus($"Network device identification failed: {ex.Message}");
        }
        finally
        {
            _host.NotifyBusy(false);
        }
    }

    private async Task ReadNetworkDeviceInterfacesAsync()
    {
        var target = NetworkDeviceTarget.Trim();
        if (string.IsNullOrWhiteSpace(target))
        {
            _host.NotifyStatus("Enter a network device target before reading interfaces.");
            return;
        }

        if (!TryBuildValidatedNetworkDeviceSnmpOptions(target, out var options, out var validationResult))
        {
            LastNetworkDevice = validationResult;
            _host.AttachNetworkDevice(LastNetworkDevice);
            _host.NotifyStatus(LastNetworkDevice.Verdict);
            return;
        }

        _host.NotifyBusy(true);
        _host.NotifyStatus($"Reading interfaces from {options.Target}...");
        try
        {
            LastNetworkDevice = await _collector.ReadInterfacesAsync(options);
            SaveNetworkDeviceSnmpCredentialsIfRequested();
            _host.AttachNetworkDevice(LastNetworkDevice);
            _host.NotifyStatus(LastNetworkDevice.Verdict);
        }
        catch (OperationCanceledException)
        {
            _host.NotifyStatus("Interface read cancelled.");
        }
        catch (Exception ex)
        {
            _logger.Error("Network device interface read failed.", ex);
            var message = SanitizeNetworkDeviceError(ex.Message, options);
            LastNetworkDevice = BuildInterfaceReadFailureResult(options, message);
            _host.AttachNetworkDevice(LastNetworkDevice);
            _host.NotifyStatus($"Interface read failed: {message}");
        }
        finally
        {
            _host.NotifyBusy(false);
        }
    }

    private async Task ReadSelectedNetworkDeviceInterfacesAsync()
    {
        var target = SelectedNetworkDeviceScanResult?.Address;
        if (string.IsNullOrWhiteSpace(target))
        {
            _host.NotifyStatus("Select a device from the scan results before reading interfaces.");
            return;
        }

        if (!TryBuildValidatedNetworkDeviceSnmpOptions(target, out var options, out var validationResult))
        {
            LastNetworkDevice = validationResult;
            _host.AttachNetworkDevice(LastNetworkDevice);
            _host.NotifyStatus(LastNetworkDevice.Verdict);
            return;
        }

        _host.NotifyBusy(true);
        _host.NotifyStatus($"Reading interfaces from {options.Target}...");
        try
        {
            LastNetworkDevice = await _collector.ReadInterfacesAsync(options);
            SaveNetworkDeviceSnmpCredentialsIfRequested();
            _host.AttachNetworkDevice(LastNetworkDevice);
            _host.NotifyStatus(LastNetworkDevice.Verdict);
        }
        catch (OperationCanceledException)
        {
            _host.NotifyStatus("Interface read cancelled.");
        }
        catch (Exception ex)
        {
            _logger.Error("Selected network device interface read failed.", ex);
            var message = SanitizeNetworkDeviceError(ex.Message, options);
            LastNetworkDevice = BuildInterfaceReadFailureResult(options, message);
            _host.AttachNetworkDevice(LastNetworkDevice);
            _host.NotifyStatus($"Interface read failed: {message}");
        }
        finally
        {
            _host.NotifyBusy(false);
        }
    }

    private async Task DetectNetworkDeviceScanRangeAsync()
    {
        _host.NotifyBusy(true);
        _host.NotifyStatus("Detecting local private IPv4 scan range...");
        try
        {
            var info = await _collector.DetectLocalScanRangeAsync();
            NetworkDeviceDetectedIp = info.LocalIpAddress;
            NetworkDeviceDetectedRange = info.ScanRange;
            if (!string.IsNullOrWhiteSpace(info.ScanRange))
                NetworkDeviceScanRange = info.ScanRange;
            _host.NotifyStatus(string.IsNullOrWhiteSpace(info.Error)
                ? $"Detected local IP {info.LocalIpAddress} on {info.AdapterName}, safe scan range {info.ScanRange}."
                : info.Error);
        }
        catch (Exception ex)
        {
            _logger.Error("Network device scan range detection failed.", ex);
            _host.NotifyStatus($"Network device scan range detection failed: {ex.Message}");
        }
        finally
        {
            _host.NotifyBusy(false);
        }
    }

    private async Task ScanLocalNetworkDevicesAsync()
    {
        // Cancel a previous scan but DON'T dispose it here — a still-running scan may be
        // mid-await on the old token; Cancel+Dispose+reassign would race it into
        // ObjectDisposedException. The previous run's finally disposes its own CTS.
        if (!TryBuildValidatedNetworkDeviceScanOptions(out var options, out var validationResult))
        {
            LastNetworkDeviceScan = validationResult;
            _host.AttachNetworkDeviceScan(LastNetworkDeviceScan);
            _host.NotifyStatus(LastNetworkDeviceScan.Verdict);
            return;
        }

        CancelQuietly(_networkDeviceScanCancellation);
        _host.NotifyBusy(true);
        _host.NotifyStatus("Running auto safe scan for SNMP network devices...");
        var networkDeviceScanCancellation = new CancellationTokenSource();
        _networkDeviceScanCancellation = networkDeviceScanCancellation;
        IsNetworkDeviceScanRunning = true;
        var token = networkDeviceScanCancellation.Token;
        try
        {
            LastNetworkDeviceScan = await _collector.ScanLocalSubnetAsync(
                options,
                token,
                CreateNetworkDeviceScanProgressHandler());
            if (!ReferenceEquals(_networkDeviceScanCancellation, networkDeviceScanCancellation))
            {
                return;
            }

            _host.AttachNetworkDeviceScan(LastNetworkDeviceScan);
            SaveNetworkDeviceSnmpCredentialsIfRequested();
            _host.NotifyStatus(LastNetworkDeviceScan.Verdict);
        }
        catch (OperationCanceledException)
        {
            if (ReferenceEquals(_networkDeviceScanCancellation, networkDeviceScanCancellation))
            {
                MarkNetworkDeviceScanStopped("Network device scan cancelled.", "Warning");
                _host.NotifyStatus("Network device scan cancelled.");
            }
        }
        catch (Exception ex)
        {
            _logger.Error("Local network device scan failed.", ex);
            if (ReferenceEquals(_networkDeviceScanCancellation, networkDeviceScanCancellation))
            {
                _host.NotifyStatus($"Network device scan failed: {ex.Message}");
            }
        }
        finally
        {
            networkDeviceScanCancellation.Dispose();
            if (ReferenceEquals(_networkDeviceScanCancellation, networkDeviceScanCancellation))
            {
                _networkDeviceScanCancellation = null;
                IsNetworkDeviceScanRunning = false;
            }

            _host.NotifyBusy(false);
        }
    }

    private async Task ScanNetworkDeviceRangeAsync()
    {
        var range = NetworkDeviceScanRange.Trim();
        if (string.IsNullOrWhiteSpace(range))
        {
            _host.NotifyStatus("Enter a scan range: a single private IPv4, a private CIDR up to /24, or a start-end range. Only RFC-1918 ranges are permitted.");
            return;
        }

        if (!TryBuildValidatedNetworkDeviceScanOptions(out var options, out var validationResult))
        {
            LastNetworkDeviceScan = validationResult;
            _host.AttachNetworkDeviceScan(LastNetworkDeviceScan);
            _host.NotifyStatus(LastNetworkDeviceScan.Verdict);
            return;
        }

        // Same pattern as ScanLocalNetworkDevicesAsync — cancel only, dispose in finally.
        CancelQuietly(_networkDeviceScanCancellation);
        _host.NotifyBusy(true);
        _host.NotifyStatus($"Scanning network device range {range}...");
        var networkDeviceScanCancellation = new CancellationTokenSource();
        _networkDeviceScanCancellation = networkDeviceScanCancellation;
        IsNetworkDeviceScanRunning = true;
        var token = networkDeviceScanCancellation.Token;
        try
        {
            LastNetworkDeviceScan = await _collector.ScanRangeAsync(
                range,
                options,
                token,
                CreateNetworkDeviceScanProgressHandler());
            if (!ReferenceEquals(_networkDeviceScanCancellation, networkDeviceScanCancellation))
            {
                return;
            }

            _host.AttachNetworkDeviceScan(LastNetworkDeviceScan);
            SaveNetworkDeviceSnmpCredentialsIfRequested();
            _host.NotifyStatus(LastNetworkDeviceScan.Verdict);
        }
        catch (OperationCanceledException)
        {
            if (ReferenceEquals(_networkDeviceScanCancellation, networkDeviceScanCancellation))
            {
                MarkNetworkDeviceScanStopped("Network device range scan cancelled.", "Warning");
                _host.NotifyStatus("Network device range scan cancelled.");
            }
        }
        catch (Exception ex)
        {
            _logger.Error("Network device range scan failed.", ex);
            if (ReferenceEquals(_networkDeviceScanCancellation, networkDeviceScanCancellation))
            {
                _host.NotifyStatus($"Network device range scan failed: {ex.Message}");
            }
        }
        finally
        {
            networkDeviceScanCancellation.Dispose();
            if (ReferenceEquals(_networkDeviceScanCancellation, networkDeviceScanCancellation))
            {
                _networkDeviceScanCancellation = null;
                IsNetworkDeviceScanRunning = false;
            }

            _host.NotifyBusy(false);
        }
    }

    private void CancelNetworkDeviceScan()
    {
        // Cancel() throws ObjectDisposedException if the scan already finished and its
        // finally disposed the CTS — swallow that benign Stop-after-done race.
        CancelQuietly(_networkDeviceScanCancellation);
        _host.NotifyStatus("Stopping network device scan...");
    }

    private static void CancelQuietly(CancellationTokenSource? cancellation)
    {
        try { cancellation?.Cancel(); } catch (ObjectDisposedException) { }
    }

    /// <summary>
    /// "Copy for ticket" — render a plain-text summary of the last device check + LAN
    /// scan (identity, reachability, interfaces needing attention, scan results) to the
    /// clipboard so a tech can paste it straight into a helpdesk ticket.
    /// </summary>
    private void CopyNetworkDeviceSummary()
    {
        var summary = BuildNetworkDeviceTicketSummary(DateTimeOffset.Now);
        if (string.IsNullOrWhiteSpace(summary))
        {
            _host.NotifyStatus("Nothing to copy yet - identify a device or run a scan first.");
            return;
        }

        try
        {
            System.Windows.Clipboard.SetText(summary);
            _host.NotifyStatus("Network device summary copied to clipboard.");
        }
        catch (Exception ex)
        {
            _host.NotifyStatus($"Copy failed: {ex.Message}");
        }
    }

    public string BuildNetworkDeviceTicketSummary(DateTimeOffset timestamp)
    {
        var device = LastNetworkDevice;
        var scan = LastNetworkDeviceScan;
        if (device is null && scan is null)
        {
            return string.Empty;
        }

        var sb = new System.Text.StringBuilder(2048);
        sb.AppendLine($"ISG Desk - Network Device Summary ({timestamp:yyyy-MM-dd HH:mm:ss})");
        sb.AppendLine(new string('-', 60));

        if (device is not null)
        {
            sb.AppendLine($"Verdict   : {device.Verdict} [{device.Severity}]");
            sb.AppendLine($"Target    : {ValueOrUnknown(device.Target)} ({ValueOrUnknown(device.Address)})");
            sb.AppendLine($"Type      : {ValueOrUnknown(device.DeviceType)} (confidence {ValueOrUnknown(device.Confidence)})");
            sb.AppendLine($"Reach     : DNS {ValueOrUnknown(device.DnsStatus)} | ping {ValueOrUnknown(device.PingStatus)} | latency {device.LatencyText} | loss {device.LossText}");
            sb.AppendLine($"MAC       : {ValueOrUnknown(device.MacAddress)} ({ValueOrUnknown(device.MacVendor)})");
            sb.AppendLine($"Open ports: {device.OpenPortSummary}");
            AppendTextSection(sb, "CONFIRMED FINDINGS", device.ConfirmedFindings);
            AppendTextSection(sb, "PROBABLE FINDINGS", device.ProbableFindings);
            AppendTextSection(sb, "UNKNOWN / LIMITS", device.UnknownFindings);
            AppendTextSection(sb, "EVIDENCE", device.Evidence);
            AppendTextSection(sb, "LIMITATIONS", device.Limitations);
            AppendTextSection(sb, "RECOMMENDED NEXT CHECKS", device.Recommendations);

            if (device.Identity is not null)
            {
                sb.AppendLine();
                sb.AppendLine("SNMP IDENTITY");
                sb.AppendLine($"- Address    : {device.Identity.AddressDisplay}");
                sb.AppendLine($"- Protocol   : {device.Identity.ProtocolDisplay}");
                sb.AppendLine($"- Name       : {device.Identity.SysNameDisplay}");
                sb.AppendLine($"- Description: {device.Identity.SysDescrDisplay}");
                sb.AppendLine($"- Location   : {device.Identity.SysLocationDisplay}");
                sb.AppendLine($"- Contact    : {device.Identity.SysContactDisplay}");
                sb.AppendLine($"- Uptime     : {device.Identity.SysUpTimeDisplay}");
            }

            if (device.Interfaces.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine($"INTERFACES ({device.Interfaces.Count})");
                foreach (var item in device.Interfaces)
                {
                    sb.AppendLine($"- {item.NameDisplay} | admin {item.AdminStatusDisplay}/oper {item.OperStatusDisplay} | {item.SpeedTextDisplay} | err {item.Errors} disc {item.Discards} | {item.VerdictDisplay}");
                }
            }

            if (device.PortsNeedingAttention.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine($"NEEDS ATTENTION ({device.PortsNeedingAttention.Count})");
                foreach (var item in device.PortsNeedingAttention)
                {
                    sb.AppendLine($"- {item.NameDisplay}: {item.AttentionReasonDisplay} - {item.RecommendedNextCheckDisplay}");
                }
            }
        }

        if (scan is not null)
        {
            sb.AppendLine();
            sb.AppendLine($"LAN SCAN - {scan.VerdictDisplay} ({scan.FoundDevices} found / {scan.ScannedHosts} scanned)");
            foreach (var deviceResult in scan.Devices)
            {
                sb.AppendLine($"- {deviceResult.AddressDisplay} | {deviceResult.DeviceTypeDisplay} | {deviceResult.IdentityNameDisplay} | {deviceResult.ConfirmationStatusDisplay}");
            }
        }

        return sb.ToString();
    }

    private static void AppendTextSection(System.Text.StringBuilder sb, string title, IReadOnlyList<string> items)
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
        string.IsNullOrWhiteSpace(value) ? "Unknown" : value;

    private Action<NetworkDeviceScanResult> CreateNetworkDeviceScanProgressHandler()
    {
        return scan =>
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher is null || dispatcher.CheckAccess())
            {
                ApplyNetworkDeviceScanProgress(scan);
                return;
            }

            dispatcher.BeginInvoke(new Action(() => ApplyNetworkDeviceScanProgress(scan)));
        };
    }

    private void ApplyNetworkDeviceScanProgress(NetworkDeviceScanResult scan)
    {
        LastNetworkDeviceScan = scan;
        _host.NotifyStatus(scan.ProgressText);
    }

    private void MarkNetworkDeviceScanStopped(string verdict, string severity)
    {
        if (LastNetworkDeviceScan is null)
        {
            return;
        }

        LastNetworkDeviceScan.IsRunning = false;
        LastNetworkDeviceScan.Verdict = verdict;
        LastNetworkDeviceScan.Severity = severity;
        OnPropertyChanged(nameof(LastNetworkDeviceScan));
        NotifyNetworkDeviceScanStateChanged();
    }

    public SnmpSessionOptions BuildNetworkDeviceSnmpOptions(string target)
    {
        var options = _snmpCredentialStore.BuildSessionOptions(target);
        options.TimeoutMs = 2500;
        return options;
    }

    private bool TryBuildValidatedNetworkDeviceSnmpOptions(
        string target,
        out SnmpSessionOptions options,
        out NetworkDeviceResult validationResult)
    {
        options = new SnmpSessionOptions();
        validationResult = new NetworkDeviceResult
        {
            Target = target.Trim(),
            SnmpProtocol = NetworkDeviceSelectedSnmpProtocol,
            SnmpCommunityMasked = MaskCommunity(NetworkDeviceCommunity),
            SnmpStatus = "Not tested"
        };

        if (!DiagnosticTargetValidator.TryNormalizeHost(target, out var normalizedTarget, out var validationReason))
        {
            validationResult.Verdict = "Network device target is invalid.";
            validationResult.Severity = "Warning";
            validationResult.DnsStatus = "Invalid target";
            validationResult.Evidence.Add(validationReason);
            validationResult.Limitations.Add(NetworkDeviceValidationScope);
            return false;
        }

        if (System.Net.IPAddress.TryParse(normalizedTarget, out var ipAddress) &&
            (ipAddress.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork ||
             !NetworkSafetyPolicy.IsPrivateIpv4(ipAddress)))
        {
            validationResult.Target = normalizedTarget;
            validationResult.Verdict = "Target rejected by safety policy.";
            validationResult.Severity = "Critical";
            validationResult.DnsStatus = "Rejected";
            validationResult.Evidence.Add(NetworkDevicePrivateTargetMessage);
            validationResult.Limitations.Add(NetworkDeviceValidationScope);
            return false;
        }

        options = BuildNetworkDeviceSnmpOptions(normalizedTarget);
        options.Target = normalizedTarget;
        validationResult.Target = normalizedTarget;

        if (options.Protocol == SnmpProtocolVersion.V3AuthPriv &&
            (string.IsNullOrWhiteSpace(options.UserName) ||
             string.IsNullOrWhiteSpace(options.AuthPassword) ||
             string.IsNullOrWhiteSpace(options.PrivacyPassword)))
        {
            validationResult.Verdict = "SNMPv3 credentials are incomplete.";
            validationResult.Severity = "Warning";
            validationResult.SnmpStatus = "Credentials missing";
            validationResult.Evidence.Add(NetworkDeviceSnmpV3RequiredMessage);
            validationResult.Limitations.Add(NetworkDeviceValidationScope);
            return false;
        }

        return true;
    }

    private bool TryBuildValidatedNetworkDeviceScanOptions(
        out SnmpSessionOptions options,
        out NetworkDeviceScanResult validationResult)
    {
        options = BuildNetworkDeviceSnmpOptions(string.Empty);
        validationResult = new NetworkDeviceScanResult
        {
            Verdict = "Ready",
            Severity = "Unknown",
            SnmpProtocol = options.Protocol,
            SnmpCommunityMasked = MaskCommunity(NetworkDeviceCommunity),
            MaxHosts = DiagnosticConstants.MaxNetworkDeviceScanHosts
        };

        if (options.Protocol == SnmpProtocolVersion.V3AuthPriv &&
            (string.IsNullOrWhiteSpace(options.UserName) ||
             string.IsNullOrWhiteSpace(options.AuthPassword) ||
             string.IsNullOrWhiteSpace(options.PrivacyPassword)))
        {
            validationResult.Verdict = "SNMPv3 credentials are incomplete.";
            validationResult.Severity = "Warning";
            validationResult.SkippedReason = NetworkDeviceSnmpV3RequiredMessage;
            validationResult.Evidence.Add(NetworkDeviceSnmpV3RequiredMessage);
            validationResult.Limitations.Add(NetworkDeviceValidationScope);
            return false;
        }

        return true;
    }

    private static string MaskCommunity(string community) =>
        string.IsNullOrWhiteSpace(community) ? "(empty)" : "********";

    private void SaveNetworkDeviceSnmpCredentialsIfRequested()
    {
        if (!SaveNetworkDeviceSnmpCredentials)
        {
            return;
        }

        _snmpCredentialStore.Save();
        OnPropertyChanged(nameof(HasSavedNetworkDeviceSnmpCredentials));
    }

    private void ForgetNetworkDeviceSnmpCredential()
    {
        _snmpCredentialStore.Forget();
        SaveNetworkDeviceSnmpCredentials = false;
        OnPropertyChanged(nameof(HasSavedNetworkDeviceSnmpCredentials));
        _host.NotifyStatus("Saved Network Devices SNMP credentials were removed.");
    }

    private void EnsureInterfaceFilterShowsResult(NetworkDeviceResult? result)
    {
        if (result?.Interfaces.Count > 0 &&
            !string.Equals(_networkDeviceInterfaceFilter, "All", StringComparison.Ordinal) &&
            FilterNetworkDeviceInterfaces(result.Interfaces, _networkDeviceInterfaceFilter).Count == 0)
        {
            SetProperty(ref _networkDeviceInterfaceFilter, "All", nameof(NetworkDeviceInterfaceFilter));
        }
    }

    private static IReadOnlyList<NetworkDeviceInterfaceInfo> FilterNetworkDeviceInterfaces(
        IReadOnlyList<NetworkDeviceInterfaceInfo> interfaces,
        string filter)
    {
        return filter switch
        {
            "Needs Attention" => interfaces.Where(item => item.NeedsAttention).ToList(),
            "Down" => interfaces.Where(item => item.OperStatus.Equals("Down", StringComparison.OrdinalIgnoreCase)).ToList(),
            "100 Mbps" => interfaces.Where(item => item.SpeedMbps == 100).ToList(),
            "Errors / Discards" => interfaces
                .Where(item => item.Errors > 0 || item.Discards > 0 || item.ErrorsPerSecond > 0 || item.DiscardsPerSecond > 0)
                .ToList(),
            "High Traffic" => interfaces.Where(item => item.UtilizationPercent is >= 80).ToList(),
            "Unknown Speed" => interfaces.Where(item => !item.SpeedMbps.HasValue).ToList(),
            _ => interfaces.ToList()
        };
    }

    private static NetworkDeviceResult BuildInterfaceReadFailureResult(SnmpSessionOptions options, string details)
    {
        return new NetworkDeviceResult
        {
            Target = options.Target,
            Address = options.Target,
            Verdict = "Interface read failed.",
            Severity = "Critical",
            SnmpProtocol = options.Protocol,
            SnmpCommunityMasked = options.Protocol == SnmpProtocolVersion.V3AuthPriv
                ? "SNMPv3 credentials masked"
                : MaskCommunity(options.Community),
            SnmpStatus = "Failed",
            ConfirmationStatus = "Unknown / unsupported",
            Evidence = [details],
            Limitations = [NetworkDeviceValidationScope],
            Recommendations =
            [
                "Confirm the device is reachable, SNMP is enabled, credentials are correct and UDP 161 is allowed."
            ]
        };
    }

    private static string SanitizeNetworkDeviceError(string message, SnmpSessionOptions options)
    {
        var sanitized = string.IsNullOrWhiteSpace(message)
            ? "Unknown interface read error."
            : message;
        RedactNetworkDeviceSecret(ref sanitized, options.Community);
        RedactNetworkDeviceSecret(ref sanitized, options.UserName);
        RedactNetworkDeviceSecret(ref sanitized, options.AuthPassword);
        RedactNetworkDeviceSecret(ref sanitized, options.PrivacyPassword);
        return sanitized;
    }

    private static void RedactNetworkDeviceSecret(ref string message, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            message = message.Replace(value, "[redacted]", StringComparison.OrdinalIgnoreCase);
        }
    }

    private const string NetworkDevicePrivateTargetMessage = "SNMP target must resolve to an authorized private IPv4 address.";
    private const string NetworkDeviceSnmpV3RequiredMessage =
        "SNMPv3 authPriv requires username, authentication password and privacy password.";
    private const string NetworkDeviceValidationScope =
        "Network Devices SNMP is read-only and limited to authorized private IPv4 targets; public IP targets and incomplete SNMPv3 credentials are stopped before network I/O.";

    private void NotifySnmpCredentialChanged(string? propertyName)
    {
        switch (propertyName)
        {
            case nameof(SnmpCredentialStore.Protocol):
                OnPropertyChanged(nameof(NetworkDeviceSelectedSnmpProtocol));
                OnPropertyChanged(nameof(IsNetworkDeviceSnmpV2Selected));
                OnPropertyChanged(nameof(IsNetworkDeviceSnmpV3Selected));
                break;
            case nameof(SnmpCredentialStore.IsV3):
                OnPropertyChanged(nameof(IsNetworkDeviceSnmpV2Selected));
                OnPropertyChanged(nameof(IsNetworkDeviceSnmpV3Selected));
                break;
            case nameof(SnmpCredentialStore.Community):
                OnPropertyChanged(nameof(NetworkDeviceCommunity));
                break;
            case nameof(SnmpCredentialStore.UserName):
                OnPropertyChanged(nameof(NetworkDeviceSnmpV3UserName));
                break;
            case nameof(SnmpCredentialStore.AuthProtocol):
                OnPropertyChanged(nameof(NetworkDeviceSnmpV3AuthProtocol));
                break;
            case nameof(SnmpCredentialStore.AuthPassword):
                OnPropertyChanged(nameof(NetworkDeviceSnmpV3AuthPassword));
                break;
            case nameof(SnmpCredentialStore.PrivacyProtocol):
                OnPropertyChanged(nameof(NetworkDeviceSnmpV3PrivacyProtocol));
                break;
            case nameof(SnmpCredentialStore.PrivacyPassword):
                OnPropertyChanged(nameof(NetworkDeviceSnmpV3PrivacyPassword));
                break;
            case nameof(SnmpCredentialStore.HasSavedCredentials):
                OnPropertyChanged(nameof(HasSavedNetworkDeviceSnmpCredentials));
                break;
        }
    }
}
