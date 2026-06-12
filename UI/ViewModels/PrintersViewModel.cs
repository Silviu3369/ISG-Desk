using System.Net;
using System.Windows.Input;
using NetScopeDiagnosticCenter.Collectors;
using NetScopeDiagnosticCenter.Collectors.Shared;
using NetScopeDiagnosticCenter.Core;
using NetScopeDiagnosticCenter.Core.Models;
using NetScopeDiagnosticCenter.Infrastructure;

namespace NetScopeDiagnosticCenter.UI.ViewModels;

/// <summary>
/// Printers module ViewModel: local printer inventory, print server discovery,
/// LAN printer scan (auto + range), SNMP identification, and queue installation.
/// </summary>
/// <remarks>
/// SNMP credential fields are backed by <see cref="SnmpCredentialStore"/> so Printers and
/// Network Devices use the same read-only credential context. Persisting secrets remains
/// opt-in and encrypted for the current Windows user.
/// </remarks>
public sealed class PrintersViewModel : ObservableObject
{
    public interface IHost
    {
        void NotifyStatus(string message);
        void NotifyBusy(bool busy);
        /// <summary>Attach the latest discovery to the cross-VM diagnosis state.</summary>
        void AttachPrinterDiscovery(PrinterDiscoveryResult discovery);
        /// <summary>Remember a printer target for the current session.</summary>
        void RememberPrinterTarget(string target);
        /// <summary>Remember the last print server name used in the current session.</summary>
        void RememberPrintServer(string server);
    }

    private readonly PrinterDiscoveryCollector _printerDiscoveryCollector;
    private readonly LocalPrinterCollector _localPrinterCollector;
    private readonly PrintServerCollector _printServerCollector;
    private readonly SnmpPrinterCollector _snmpPrinterCollector;
    private readonly PrinterQueueInstaller _printerQueueInstaller;
    private readonly SnmpCredentialStore _snmpCredentialStore;
    private readonly ILoggingService _logger;
    private readonly IHost _host;
    private readonly Func<IReadOnlyList<AdPrintServerInfo>> _adPrintServerLocator;

    private PrinterDiscoveryResult? _lastPrinterDiscovery;
    private PrinterInfo? _selectedPrintServerPrinter;
    private PrinterScanResult? _selectedPrinterScanResult;
    private PrinterNetworkAdapterInfo? _selectedPrinterNetworkAdapter;
    private PrinterInfo? _selectedLocalPrinter;
    private CancellationTokenSource? _printerScanCancellation;
    private CancellationTokenSource? _printerSnmpIdentifyCancellation;
    private string _printerTarget = string.Empty;
    private string _selectedPrinterTarget = string.Empty;
    private string _selectedPrinterTargetSource = "Manual";
    private string _printServerName = string.Empty;
    private string _printerScanRange = string.Empty;
    private bool _saveSnmpCredentials;
    private bool _confirmPrinterInstall;
    private bool _setDefaultAfterInstall;
    private bool _showOnlyPrinters = true;
    private bool _isPrinterScanRunning;
    private bool _isPrinterSnmpIdentifyRunning;

    public PrintersViewModel(
        PrinterDiscoveryCollector printerDiscoveryCollector,
        LocalPrinterCollector localPrinterCollector,
        PrintServerCollector printServerCollector,
        SnmpPrinterCollector snmpPrinterCollector,
        PrinterQueueInstaller printerQueueInstaller,
        SnmpCredentialStore snmpCredentialStore,
        ILoggingService logger,
        IHost host,
        string initialPrinterTarget = "",
        string initialPrintServer = "",
        Func<IReadOnlyList<AdPrintServerInfo>>? adPrintServerLocator = null)
    {
        _adPrintServerLocator = adPrintServerLocator ?? (() => AdPrintServerLocator.FindPublishedPrintServers());
        _printerDiscoveryCollector = printerDiscoveryCollector ?? throw new ArgumentNullException(nameof(printerDiscoveryCollector));
        _localPrinterCollector = localPrinterCollector ?? throw new ArgumentNullException(nameof(localPrinterCollector));
        _printServerCollector = printServerCollector ?? throw new ArgumentNullException(nameof(printServerCollector));
        _snmpPrinterCollector = snmpPrinterCollector ?? throw new ArgumentNullException(nameof(snmpPrinterCollector));
        _printerQueueInstaller = printerQueueInstaller ?? throw new ArgumentNullException(nameof(printerQueueInstaller));
        _snmpCredentialStore = snmpCredentialStore ?? throw new ArgumentNullException(nameof(snmpCredentialStore));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _snmpCredentialStore.PropertyChanged += (_, e) => NotifySnmpCredentialChanged(e.PropertyName);

        _printerTarget = initialPrinterTarget;
        _selectedPrinterTarget = initialPrinterTarget;
        _printServerName = initialPrintServer;
        LoadSavedSnmpV3Credential();

        DetectPrinterIpCommand = new AsyncRelayCommand(_ => DetectPrinterIpAsync());
        LoadLocalPrintersCommand = new AsyncRelayCommand(_ => LoadLocalPrintersAsync());
        DetectPrintServerCommand = new AsyncRelayCommand(_ => DetectPrintServerAsync());
        DiscoverPrintServerCommand = new AsyncRelayCommand(_ => DiscoverPrintServerAsync());
        ScanPrintersCommand = new AsyncRelayCommand(_ => ScanPrintersAsync());
        ScanPrinterRangeCommand = new AsyncRelayCommand(_ => ScanPrinterRangeAsync());
        IdentifySelectedPrinterSnmpCommand = new AsyncRelayCommand(_ => IdentifySelectedPrinterSnmpAsync());
        AutoIdentifyScannedPrintersSnmpCommand = new AsyncRelayCommand(_ => AutoIdentifyScannedPrintersSnmpAsync());
        InstallSelectedPrinterQueueCommand = new AsyncRelayCommand(_ => InstallSelectedPrinterQueueAsync());
        ForgetSnmpCredentialCommand = new RelayCommand(_ => ForgetSnmpCredential());
        CancelPrinterScanCommand = new RelayCommand(_ => CancelPrinterScan());
        CancelPrinterSnmpIdentifyCommand = new RelayCommand(_ => CancelPrinterSnmpIdentify());
        CopyPrinterSummaryCommand = new RelayCommand(_ => CopyPrinterSummary());
        SetDefaultPrinterCommand = new AsyncRelayCommand(_ => SetDefaultPrinterAsync());
        RemoveSelectedPrinterScanResultCommand = new RelayCommand(_ => RemoveSelectedPrinterScanResult());
    }

    public IReadOnlyList<string> SnmpProtocols { get; } = [SnmpProtocolVersion.V2C, SnmpProtocolVersion.V3AuthPriv];
    public IReadOnlyList<string> SnmpAuthProtocols { get; } = ["SHA256", "SHA384", "SHA512", "SHA1", "MD5"];
    public IReadOnlyList<string> SnmpPrivacyProtocols { get; } = ["AES128", "AES192", "AES256", "DES"];
    public List<PrinterNetworkAdapterInfo> PrinterNetworkAdapters { get; } = [];

    public ICommand DetectPrinterIpCommand { get; }
    public ICommand LoadLocalPrintersCommand { get; }
    public ICommand DetectPrintServerCommand { get; }
    public ICommand DiscoverPrintServerCommand { get; }
    public ICommand ScanPrintersCommand { get; }
    public ICommand ScanPrinterRangeCommand { get; }
    public ICommand IdentifySelectedPrinterSnmpCommand { get; }
    public ICommand AutoIdentifyScannedPrintersSnmpCommand { get; }
    public ICommand InstallSelectedPrinterQueueCommand { get; }
    public ICommand ForgetSnmpCredentialCommand { get; }
    public ICommand CancelPrinterScanCommand { get; }
    public ICommand CancelPrinterSnmpIdentifyCommand { get; }
    public ICommand CopyPrinterSummaryCommand { get; }
    public ICommand SetDefaultPrinterCommand { get; }
    public ICommand RemoveSelectedPrinterScanResultCommand { get; }
    public bool IsPrinterScanRunning { get => _isPrinterScanRunning; private set => SetProperty(ref _isPrinterScanRunning, value); }
    public bool IsPrinterSnmpIdentifyRunning { get => _isPrinterSnmpIdentifyRunning; private set => SetProperty(ref _isPrinterSnmpIdentifyRunning, value); }

    public string PrinterTarget
    {
        get => _printerTarget;
        set
        {
            if (SetProperty(ref _printerTarget, value) && !string.IsNullOrWhiteSpace(value))
            {
                SelectedPrinterTarget = value.Trim();
                SelectedPrinterTargetSource = "Manual";
            }
        }
    }

    public string SelectedPrinterTarget { get => _selectedPrinterTarget; set => SetProperty(ref _selectedPrinterTarget, value); }
    public string SelectedPrinterTargetSource { get => _selectedPrinterTargetSource; set => SetProperty(ref _selectedPrinterTargetSource, value); }
    public string PrintServerName { get => _printServerName; set => SetProperty(ref _printServerName, value); }
    public string PrinterScanRange { get => _printerScanRange; set => SetProperty(ref _printerScanRange, value); }
    public string SnmpCommunity
    {
        get => _snmpCredentialStore.Community;
        set => _snmpCredentialStore.Community = value;
    }

    public string SelectedSnmpProtocol
    {
        get => _snmpCredentialStore.Protocol;
        set => _snmpCredentialStore.Protocol = value;
    }

    public bool IsSnmpV3Selected => _snmpCredentialStore.IsV3;
    public bool IsSnmpV2Selected => !IsSnmpV3Selected;

    public string SnmpV3UserName { get => _snmpCredentialStore.UserName; set => _snmpCredentialStore.UserName = value; }
    public string SnmpV3AuthProtocol { get => _snmpCredentialStore.AuthProtocol; set => _snmpCredentialStore.AuthProtocol = value; }
    public string SnmpV3AuthPassword { get => _snmpCredentialStore.AuthPassword; set => _snmpCredentialStore.AuthPassword = value; }
    public string SnmpV3PrivacyProtocol { get => _snmpCredentialStore.PrivacyProtocol; set => _snmpCredentialStore.PrivacyProtocol = value; }
    public string SnmpV3PrivacyPassword { get => _snmpCredentialStore.PrivacyPassword; set => _snmpCredentialStore.PrivacyPassword = value; }
    public bool SaveSnmpCredentials { get => _saveSnmpCredentials; set => SetProperty(ref _saveSnmpCredentials, value); }
    public bool HasSavedSnmpCredentials => _snmpCredentialStore.HasSavedCredentials;
    public bool ConfirmPrinterInstall { get => _confirmPrinterInstall; set => SetProperty(ref _confirmPrinterInstall, value); }

    /// <summary>When ticked, a successfully installed queue is also made the Windows default.</summary>
    public bool SetDefaultAfterInstall { get => _setDefaultAfterInstall; set => SetProperty(ref _setDefaultAfterInstall, value); }

    public PrinterInfo? SelectedPrintServerPrinter
    {
        get => _selectedPrintServerPrinter;
        set
        {
            if (SetProperty(ref _selectedPrintServerPrinter, value) &&
                value is not null &&
                !string.IsNullOrWhiteSpace(value.PortHostAddress))
            {
                SelectedPrinterTarget = value.PortHostAddress;
                SelectedPrinterTargetSource = $"Print server queue: {value.Name}";
            }
        }
    }

    public PrinterScanResult? SelectedPrinterScanResult
    {
        get => _selectedPrinterScanResult;
        set
        {
            if (SetProperty(ref _selectedPrinterScanResult, value) &&
                value is not null &&
                !string.IsNullOrWhiteSpace(value.Address))
            {
                SelectedPrinterTarget = value.Address;
                SelectedPrinterTargetSource = PrinterScanTargetSource;
            }
        }
    }

    public PrinterNetworkAdapterInfo? SelectedPrinterNetworkAdapter
    {
        get => _selectedPrinterNetworkAdapter;
        set
        {
            if (SetProperty(ref _selectedPrinterNetworkAdapter, value))
            {
                ApplySelectedPrinterNetworkAdapterToDiscovery(value);
            }
        }
    }

    /// <summary>The installed local queue selected in the Installed tab (for "Set as default").</summary>
    public PrinterInfo? SelectedLocalPrinter
    {
        get => _selectedLocalPrinter;
        set => SetProperty(ref _selectedLocalPrinter, value);
    }

    public PrinterDiscoveryResult? LastPrinterDiscovery
    {
        get => _lastPrinterDiscovery;
        set
        {
            if (SetProperty(ref _lastPrinterDiscovery, value))
            {
                SelectedPrintServerPrinter = value?.Printers.FirstOrDefault();
                SelectedPrinterScanResult = value?.ScanResults.FirstOrDefault();
                NotifyPrinterDiscoveryViewChanged();
            }
        }
    }

    /// <summary>Hides rows SNMP identified as non-printers (switch/NAS/UPS). Default on.</summary>
    public bool ShowOnlyPrinters
    {
        get => _showOnlyPrinters;
        set
        {
            if (SetProperty(ref _showOnlyPrinters, value))
            {
                NotifyPrinterDiscoveryViewChanged();
            }
        }
    }

    /// <summary>The IP Scan grid binds here so the printers-only filter applies.</summary>
    public IReadOnlyList<PrinterScanResult> FilteredScanResults
    {
        get
        {
            var rows = LastPrinterDiscovery?.ScanResults;
            if (rows is null) return [];
            return ShowOnlyPrinters
                ? rows.Where(row => !IsNonPrinterRow(row)).ToList()
                : rows.ToList();
        }
    }

    public string HiddenScanResultCountText
    {
        get
        {
            var total = LastPrinterDiscovery?.ScanResults.Count ?? 0;
            var hidden = total - FilteredScanResults.Count;
            return hidden > 0
                ? $"{hidden} non-printer device(s) hidden — untick 'Only printers' to show them."
                : string.Empty;
        }
    }

    public bool HasHiddenScanResults => !string.IsNullOrEmpty(HiddenScanResultCountText);

    private static bool IsNonPrinterRow(PrinterScanResult row) =>
        row.Classification.StartsWith("Not a printer", StringComparison.OrdinalIgnoreCase);

    /// <summary>Re-raises every scan-grid-derived binding after rows/filter change.</summary>
    private void NotifyPrinterDiscoveryViewChanged()
    {
        OnPropertyChanged(nameof(LastPrinterDiscovery));
        OnPropertyChanged(nameof(FilteredScanResults));
        OnPropertyChanged(nameof(HiddenScanResultCountText));
        OnPropertyChanged(nameof(HasHiddenScanResults));
    }

    private async Task DetectPrinterIpAsync()
    {
        _host.NotifyBusy(true);
        _host.NotifyStatus("Detecting default-route IPv4 adapter for printer scan...");
        try
        {
            var adapters = await _printerDiscoveryCollector.DetectLocalAdaptersAsync();
            PrinterNetworkAdapters.Clear();
            PrinterNetworkAdapters.AddRange(adapters);
            var selectedBeforeRefresh = SelectedPrinterNetworkAdapter;
            SelectedPrinterNetworkAdapter = PrinterNetworkAdapters.FirstOrDefault();
            if (ReferenceEquals(selectedBeforeRefresh, SelectedPrinterNetworkAdapter))
            {
                ApplySelectedPrinterNetworkAdapterToDiscovery(SelectedPrinterNetworkAdapter);
            }

            OnPropertyChanged(nameof(PrinterNetworkAdapters));
            if (SelectedPrinterNetworkAdapter is null)
            {
                _host.NotifyStatus("No usable IPv4 default-route adapter was detected.");
                return;
            }

            _host.NotifyStatus($"Detected {SelectedPrinterNetworkAdapter.LocalIpAddress} on {SelectedPrinterNetworkAdapter.AdapterName}.");
        }
        catch (Exception ex)
        {
            _logger.Error("Printer IP detection failed.", ex);
            _host.NotifyStatus($"Printer IP detection failed: {ex.Message}");
        }
        finally
        {
            _host.NotifyBusy(false);
        }
    }

    private async Task LoadLocalPrintersAsync()
    {
        _host.NotifyBusy(true);
        _host.NotifyStatus("Reading local installed printers and spooler status...");
        try
        {
            var result = await _localPrinterCollector.CollectAsync();
            MergePrinterDiscovery(result, replaceLocal: true);
            _host.NotifyStatus(result.Verdict);
        }
        catch (Exception ex)
        {
            _logger.Error("Local printer inventory failed.", ex);
            _host.NotifyStatus($"Local printer inventory failed: {ex.Message}");
        }
        finally
        {
            _host.NotifyBusy(false);
        }
    }

    /// <summary>
    /// Sets the printer selected in the Installed tab as the Windows default, then
    /// reloads the local inventory so the "Default" column reflects the change.
    /// </summary>
    private async Task SetDefaultPrinterAsync()
    {
        var printer = SelectedLocalPrinter;
        if (printer is null || string.IsNullOrWhiteSpace(printer.Name))
        {
            _host.NotifyStatus("Select an installed printer first, then press 'Set as Default'.");
            return;
        }

        _host.NotifyBusy(true);
        _host.NotifyStatus($"Setting '{printer.Name}' as the default printer...");
        try
        {
            var result = await _localPrinterCollector.SetDefaultPrinterAsync(printer.Name);
            _host.NotifyStatus(result.Message);
            if (result.Success)
            {
                // Reload so IsDefault is accurate across all rows.
                var refreshed = await _localPrinterCollector.CollectAsync();
                MergePrinterDiscovery(refreshed, replaceLocal: true);
                SelectedLocalPrinter = FindPrinterByName(LastPrinterDiscovery?.LocalPrinters, printer.Name)
                    ?? LastPrinterDiscovery?.LocalPrinters.FirstOrDefault(item => item.IsDefault)
                    ?? LastPrinterDiscovery?.LocalPrinters.FirstOrDefault();
            }
        }
        catch (Exception ex)
        {
            _logger.Error("Set default printer failed.", ex);
            _host.NotifyStatus($"Set default printer failed: {ex.Message}");
        }
        finally
        {
            _host.NotifyBusy(false);
        }
    }

    private async Task DetectPrintServerAsync()
    {
        _host.NotifyBusy(true);
        _host.NotifyStatus("Auto-detecting print server from installed printer connections...");
        try
        {
            var localPrinters = LastPrinterDiscovery?.LocalPrinters ?? [];
            if (localPrinters.Count == 0)
            {
                var local = await _localPrinterCollector.CollectAsync();
                MergePrinterDiscovery(local, replaceLocal: true);
                localPrinters = LastPrinterDiscovery?.LocalPrinters ?? [];
            }

            var candidates = DetectPrintServerCandidates(localPrinters);
            if (candidates.Count == 0)
            {
                // Nothing installed locally — ask Active Directory which servers publish
                // shared queues (printQueue objects). Zero-config on domain PCs.
                _host.NotifyStatus("No local print connection found — querying Active Directory for published print servers...");
                var adServers = await Task.Run(() => _adPrintServerLocator());
                if (adServers.Count == 0)
                {
                    _host.NotifyStatus("No print server found from local connections or Active Directory. Enter the print server manually.");
                    return;
                }

                var adNames = string.Join(", ", adServers.Select(server => $"{server.Server} ({server.QueueCount} queue(s))"));
                candidates = adServers
                    .Select(server => new PrintServerCandidate(server.Server, $"Active Directory ({server.QueueCount} published queue(s))", server.QueueCount))
                    .ToList();
                _host.NotifyStatus(adServers.Count == 1
                    ? $"Active Directory publishes print server {adNames}."
                    : $"Active Directory publishes {adServers.Count} print servers: {adNames}. Using the busiest one.");
            }

            var candidate = candidates[0];
            PrintServerName = candidate.Server;
            _host.RememberPrintServer(candidate.Server);
            _host.NotifyStatus($"Detected print server {candidate.Server} from {candidate.Source}. Discovering shared queues...");

            var result = await _printServerCollector.DiscoverFromPrintServerAsync(candidate.Server);
            MergePrinterDiscovery(result, replacePrintServer: true);
            _host.NotifyStatus(result.Verdict);
        }
        catch (Exception ex)
        {
            _logger.Error("Print server auto-detect failed.", ex);
            _host.NotifyStatus($"Print server auto-detect failed: {ex.Message}");
        }
        finally
        {
            _host.NotifyBusy(false);
        }
    }

    private async Task DiscoverPrintServerAsync()
    {
        if (string.IsNullOrWhiteSpace(PrintServerName))
        {
            _host.NotifyStatus("Enter a print server name before discovery.");
            return;
        }

        if (!PrinterTargetValidator.TryNormalizePrintServer(PrintServerName, out var normalizedPrintServer, out var validationReason))
        {
            var invalidResult = new PrinterDiscoveryResult
            {
                Mode = "PrintServer",
                Source = PrintServerName.Trim(),
                Verdict = validationReason,
                Severity = "Critical",
                MaxHosts = 254,
                Evidence = { validationReason },
                Limitations = { PrintServerValidationScope }
            };

            MergePrinterDiscovery(invalidResult, replacePrintServer: true);
            _host.NotifyStatus(validationReason);
            return;
        }

        PrintServerName = normalizedPrintServer;
        _host.NotifyBusy(true);
        _host.NotifyStatus($"Discovering printers from {normalizedPrintServer}...");
        try
        {
            _host.RememberPrintServer(normalizedPrintServer);
            var result = await _printServerCollector.DiscoverFromPrintServerAsync(normalizedPrintServer);
            MergePrinterDiscovery(result, replacePrintServer: true);
            _host.NotifyStatus(result.Verdict);
        }
        catch (Exception ex)
        {
            _logger.Error("Print server discovery failed.", ex);
            _host.NotifyStatus($"Print server discovery failed: {ex.Message}");
        }
        finally
        {
            _host.NotifyBusy(false);
        }
    }

    private async Task ScanPrintersAsync()
    {
        // Cancel the previous run but DO NOT dispose it here — a previous scan may still
        // be inside an await on the old token, and Cancel+Dispose+reassign would race the
        // observer into ObjectDisposedException. The previous run's finally disposes its
        // own CTS; the reference we replace is just a stale field.
        CancelQuietly(_printerScanCancellation);
        _host.NotifyBusy(true);
        _host.NotifyStatus("Running printer auto safe scan...");
        var printerScanCancellation = new CancellationTokenSource();
        _printerScanCancellation = printerScanCancellation;
        IsPrinterScanRunning = true;
        var token = printerScanCancellation.Token;
        try
        {
            if (SelectedPrinterNetworkAdapter is null)
            {
                var adapters = await _printerDiscoveryCollector.DetectLocalAdaptersAsync(token);
                PrinterNetworkAdapters.Clear();
                PrinterNetworkAdapters.AddRange(adapters);
                SelectedPrinterNetworkAdapter = PrinterNetworkAdapters.FirstOrDefault();
                OnPropertyChanged(nameof(PrinterNetworkAdapters));
            }

            var result = await _printerDiscoveryCollector.ScanLocalSubnetAsync(
                SelectedPrinterNetworkAdapter,
                token);
            if (!ReferenceEquals(_printerScanCancellation, printerScanCancellation))
            {
                return;
            }

            MergePrinterDiscovery(result, replaceScan: true);
            _host.NotifyStatus(result.Verdict);
        }
        catch (OperationCanceledException)
        {
            if (ReferenceEquals(_printerScanCancellation, printerScanCancellation))
            {
                _host.NotifyStatus("Printer scan cancelled.");
            }
        }
        catch (Exception ex)
        {
            _logger.Error("Printer subnet scan failed.", ex);
            if (ReferenceEquals(_printerScanCancellation, printerScanCancellation))
            {
                _host.NotifyStatus($"Printer scan failed: {ex.Message}");
            }
        }
        finally
        {
            printerScanCancellation.Dispose();
            if (ReferenceEquals(_printerScanCancellation, printerScanCancellation))
            {
                _printerScanCancellation = null;
                IsPrinterScanRunning = false;
            }

            _host.NotifyBusy(false);
        }
    }

    private async Task ScanPrinterRangeAsync()
    {
        if (string.IsNullOrWhiteSpace(PrinterScanRange))
        {
            _host.NotifyStatus("Enter a scan range: a single private IPv4, a private CIDR up to /24, or a start–end range. Only RFC-1918 ranges are permitted.");
            return;
        }

        // Same pattern as ScanPrintersAsync — cancel only, do not dispose here.
        CancelQuietly(_printerScanCancellation);
        _host.NotifyBusy(true);
        _host.NotifyStatus($"Scanning printer range {PrinterScanRange.Trim()}...");
        var printerScanCancellation = new CancellationTokenSource();
        _printerScanCancellation = printerScanCancellation;
        IsPrinterScanRunning = true;
        var token = printerScanCancellation.Token;
        try
        {
            var result = await _printerDiscoveryCollector.ScanRangeAsync(
                new PrinterScanRequest { Input = PrinterScanRange.Trim(), MaxHosts = 254 },
                token);
            if (!ReferenceEquals(_printerScanCancellation, printerScanCancellation))
            {
                return;
            }

            MergePrinterDiscovery(result, replaceScan: true);
            _host.NotifyStatus(result.Verdict);
        }
        catch (OperationCanceledException)
        {
            if (ReferenceEquals(_printerScanCancellation, printerScanCancellation))
            {
                _host.NotifyStatus("Printer range scan cancelled.");
            }
        }
        catch (Exception ex)
        {
            _logger.Error("Printer range scan failed.", ex);
            if (ReferenceEquals(_printerScanCancellation, printerScanCancellation))
            {
                _host.NotifyStatus($"Printer range scan failed: {ex.Message}");
            }
        }
        finally
        {
            printerScanCancellation.Dispose();
            if (ReferenceEquals(_printerScanCancellation, printerScanCancellation))
            {
                _printerScanCancellation = null;
                IsPrinterScanRunning = false;
            }

            _host.NotifyBusy(false);
        }
    }

    private async Task IdentifySelectedPrinterSnmpAsync()
    {
        var target = FirstNonEmpty(
            SelectedPrinterTarget,
            SelectedPrinterScanResult?.Address ?? string.Empty,
            SelectedPrintServerPrinter?.PortHostAddress ?? string.Empty,
            PrinterTarget.Trim());
        if (string.IsNullOrWhiteSpace(target))
        {
            _host.NotifyStatus("Select a scanned printer row, select a print server queue with an IP, or enter a printer target before SNMP identification.");
            return;
        }

        if (!TryBuildValidatedSnmpOptions(target, out var options, out var validationReason))
        {
            StoreSnmpValidationFailure(target, validationReason);
            _host.NotifyStatus(validationReason);
            return;
        }

        _host.NotifyBusy(true);
        _host.NotifyStatus($"Reading SNMP identity from {options.Target}...");
        try
        {
            if (options.Protocol == SnmpProtocolVersion.V3AuthPriv && SaveSnmpCredentials)
            {
                _snmpCredentialStore.Save();
                OnPropertyChanged(nameof(HasSavedSnmpCredentials));
            }

            _host.RememberPrinterTarget(options.Target);
            var snmpInfo = await _snmpPrinterCollector.IdentifyAsync(options);
            LastPrinterDiscovery ??= CreatePrinterSession();
            LastPrinterDiscovery.SnmpProtocol = options.Protocol;
            LastPrinterDiscovery.SelectedTarget = options.Target;
            LastPrinterDiscovery.SelectedTargetSource = SelectedPrinterTargetSource;
            LastPrinterDiscovery.SnmpCommunityMasked = options.Protocol == SnmpProtocolVersion.V2C
                ? MaskCommunity(SnmpCommunity)
                : "SNMPv3 credentials saved/entered";
            LastPrinterDiscovery.Evidence.Add(snmpInfo.Success
                ? $"{options.Protocol} identity succeeded for {options.Target}: {FirstNonEmpty(snmpInfo.PrinterName, snmpInfo.SysName, snmpInfo.SysDescr, "identity returned")}."
                : $"SNMP identity failed for {options.Target}: {snmpInfo.Error}");

            var row = SelectedPrinterScanResult;
            if (row is null || !row.Address.Equals(snmpInfo.Address, StringComparison.OrdinalIgnoreCase))
                row = LastPrinterDiscovery.ScanResults.FirstOrDefault(item => item.Address.Equals(options.Target, StringComparison.OrdinalIgnoreCase));

            if (row is null)
            {
                row = new PrinterScanResult
                {
                    Address = FirstNonEmpty(snmpInfo.Address, options.Target),
                    Classification = snmpInfo.Success && snmpInfo.PrinterConfirmed ? "Printer confirmed by SNMP" : "Unknown device",
                    Confidence = snmpInfo.Success && snmpInfo.PrinterConfirmed ? "High" : "Low"
                };
                LastPrinterDiscovery.ScanResults.Add(row);
            }

            ApplySnmpIdentityToRow(row, snmpInfo);
            LastPrinterDiscovery.Verdict = snmpInfo.Success
                ? snmpInfo.PrinterConfirmed
                    ? "Printer confirmed by SNMP."
                    : "SNMP responded, but printer identity is not confirmed."
                : "SNMP printer identity failed or timed out.";
            LastPrinterDiscovery.Severity = snmpInfo.Success && snmpInfo.PrinterConfirmed ? "OK" : "Warning";

            SelectedPrinterScanResult = row;
            _host.AttachPrinterDiscovery(LastPrinterDiscovery);
            NotifyPrinterDiscoveryViewChanged();
            _host.NotifyStatus(snmpInfo.Success
                ? $"SNMP identity completed: {FirstNonEmpty(snmpInfo.PrinterName, snmpInfo.SysName, snmpInfo.SysDescr, options.Target)}"
                : $"SNMP identity failed: {snmpInfo.Error}");
        }
        catch (OperationCanceledException)
        {
            _host.NotifyStatus("SNMP identification cancelled.");
        }
        catch (Exception ex)
        {
            _logger.Error("SNMP printer identification failed.", ex);
            _host.NotifyStatus($"SNMP identification failed: {ex.Message}");
        }
        finally
        {
            _host.NotifyBusy(false);
        }
    }

    private async Task AutoIdentifyScannedPrintersSnmpAsync()
    {
        var discovery = LastPrinterDiscovery;
        var candidates = discovery?.ScanResults
            .Where(row => !string.IsNullOrWhiteSpace(row.Address))
            .DistinctBy(row => row.Address.Trim(), StringComparer.OrdinalIgnoreCase)
            .Take(DiagnosticConstants.MaxPrinterAutoSnmpTargets)
            .ToList() ?? [];

        if (discovery is null || candidates.Count == 0)
        {
            _host.NotifyStatus("Run IP Scan first or add a SNMP target before auto-identifying printers.");
            return;
        }

        if (!TryValidateSnmpCredentialOptions(BuildSnmpOptions(candidates[0].Address.Trim()), out var validationReason))
        {
            StoreSnmpValidationFailure(candidates[0].Address, validationReason);
            _host.NotifyStatus(validationReason);
            return;
        }

        CancelQuietly(_printerSnmpIdentifyCancellation);
        var snmpIdentifyCancellation = new CancellationTokenSource();
        _printerSnmpIdentifyCancellation = snmpIdentifyCancellation;
        var token = snmpIdentifyCancellation.Token;

        _host.NotifyBusy(true);
        IsPrinterSnmpIdentifyRunning = true;
        _host.NotifyStatus($"Running SNMP auto-identify on {candidates.Count} scanned candidate(s)...");

        try
        {
            if (SelectedSnmpProtocol == SnmpProtocolVersion.V3AuthPriv && SaveSnmpCredentials)
            {
                _snmpCredentialStore.Save();
                OnPropertyChanged(nameof(HasSavedSnmpCredentials));
            }

            var confirmed = 0;
            var responded = 0;
            var failed = 0;
            foreach (var row in candidates)
            {
                token.ThrowIfCancellationRequested();
                var target = row.Address.Trim();
                if (!TryBuildValidatedSnmpOptions(target, out var options, out var targetValidationReason))
                {
                    ApplySnmpIdentityToRow(row, new SnmpDeviceInfo
                    {
                        Address = target,
                        Protocol = SelectedSnmpProtocol,
                        Error = targetValidationReason
                    });
                    failed++;
                    discovery.Evidence.Add($"SNMP auto-identify skipped {target}: {targetValidationReason}");
                    continue;
                }

                var snmpInfo = await _snmpPrinterCollector.IdentifyAsync(options, token);
                ApplySnmpIdentityToRow(row, snmpInfo);

                if (snmpInfo.Success && snmpInfo.PrinterConfirmed)
                {
                    confirmed++;
                }
                else if (snmpInfo.Success)
                {
                    responded++;
                }
                else
                {
                    failed++;
                }

                discovery.Evidence.Add(snmpInfo.Success
                    ? $"{options.Protocol} auto-identify succeeded for {target}: {FirstNonEmpty(snmpInfo.PrinterName, snmpInfo.SysName, snmpInfo.SysDescr, "identity returned")}."
                    : $"SNMP auto-identify failed for {target}: {snmpInfo.Error}");
            }

            var skipped = discovery.ScanResults.Count - candidates.Count;
            discovery.SnmpProtocol = SelectedSnmpProtocol;
            discovery.SnmpCommunityMasked = SelectedSnmpProtocol == SnmpProtocolVersion.V2C
                ? MaskCommunity(SnmpCommunity)
                : "SNMPv3 credentials saved/entered";
            discovery.Verdict = BuildAutoSnmpVerdict(confirmed, responded, failed, skipped);
            discovery.Severity = confirmed > 0 ? "OK" : "Warning";
            TrimRolling(discovery.Evidence, MaxEvidenceLines);

            SelectedPrinterScanResult =
                discovery.ScanResults.FirstOrDefault(row => row.SnmpInfo?.PrinterConfirmed == true)
                ?? discovery.ScanResults.FirstOrDefault(row => row.SnmpInfo?.Success == true)
                ?? SelectedPrinterScanResult;

            _host.AttachPrinterDiscovery(discovery);
            NotifyPrinterDiscoveryViewChanged();
            _host.NotifyStatus(discovery.Verdict);
        }
        catch (OperationCanceledException)
        {
            if (ReferenceEquals(_printerSnmpIdentifyCancellation, snmpIdentifyCancellation))
            {
                _host.NotifyStatus("SNMP auto-identify cancelled.");
            }
        }
        catch (Exception ex)
        {
            _logger.Error("SNMP auto-identify failed.", ex);
            if (ReferenceEquals(_printerSnmpIdentifyCancellation, snmpIdentifyCancellation))
            {
                _host.NotifyStatus($"SNMP auto-identify failed: {ex.Message}");
            }
        }
        finally
        {
            snmpIdentifyCancellation.Dispose();
            if (ReferenceEquals(_printerSnmpIdentifyCancellation, snmpIdentifyCancellation))
            {
                _printerSnmpIdentifyCancellation = null;
                IsPrinterSnmpIdentifyRunning = false;
            }

            _host.NotifyBusy(false);
        }
    }

    private void CancelPrinterSnmpIdentify()
    {
        CancelQuietly(_printerSnmpIdentifyCancellation);
        _host.NotifyStatus("Stopping SNMP auto-identify...");
    }

    private async Task InstallSelectedPrinterQueueAsync()
    {
        var selectedQueue = SelectedPrintServerPrinter;
        if (selectedQueue is null)
        {
            _host.NotifyStatus("Select a shared print server queue before installation.");
            return;
        }

        if (!selectedQueue.Installable ||
            string.IsNullOrWhiteSpace(selectedQueue.ConnectionName))
        {
            _host.NotifyStatus("Selected queue is not installable because it has no valid share name.");
            return;
        }

        if (!PrinterTargetValidator.TryNormalizeSharedQueueConnection(
                selectedQueue.ConnectionName,
                out var normalizedConnection,
                out var validationReason))
        {
            _host.NotifyStatus($"Selected queue connection is invalid: {validationReason}");
            return;
        }

        if (!ConfirmPrinterInstall)
        {
            _host.NotifyStatus("Confirm printer queue installation before installing the selected queue.");
            return;
        }

        _host.NotifyBusy(true);
        _host.NotifyStatus($"Installing printer queue {normalizedConnection}...");
        try
        {
            var install = await _printerQueueInstaller.InstallSharedQueueAsync(normalizedConnection);
            LastPrinterDiscovery ??= CreatePrinterSession();
            LastPrinterDiscovery.LastInstall = install;
            LastPrinterDiscovery.LastInstallResult = install.Verdict;
            LastPrinterDiscovery.Verdict = install.Verdict;
            LastPrinterDiscovery.Severity = install.Severity;
            AddDistinct(LastPrinterDiscovery.Evidence, install.Evidence);
            TrimRolling(LastPrinterDiscovery.Evidence, MaxEvidenceLines);

            selectedQueue.InstallStatus = install.Success ? "Installed" : install.Category;
            selectedQueue.InstallError = install.Error;

            if (install.Success)
            {
                // "Set as default" at the tech's choice: the shared connection's local
                // queue name IS the connection UNC, so it can be defaulted right away.
                if (SetDefaultAfterInstall)
                {
                    var defaultResult = await _localPrinterCollector.SetDefaultPrinterAsync(normalizedConnection);
                    LastPrinterDiscovery.Evidence.Add(defaultResult.Success
                        ? $"Installed queue {normalizedConnection} was set as the Windows default printer."
                        : $"Set-as-default after install failed: {defaultResult.Message}");
                    _host.NotifyStatus(defaultResult.Message);
                }

                var local = await _localPrinterCollector.CollectAsync();
                MergePrinterDiscovery(local, replaceLocal: true, attach: false);
                LastPrinterDiscovery.LastInstall = install;
                LastPrinterDiscovery.LastInstallResult = install.Verdict;
                LastPrinterDiscovery.Verdict = install.Verdict;
                LastPrinterDiscovery.Severity = install.Severity;
                SelectedPrintServerPrinter = FindPrinterByConnectionName(LastPrinterDiscovery.Printers, normalizedConnection)
                    ?? selectedQueue;
            }
            ConfirmPrinterInstall = false;

            _host.AttachPrinterDiscovery(LastPrinterDiscovery);
            NotifyPrinterDiscoveryViewChanged();
            _host.NotifyStatus(install.Success ? install.Verdict : $"{install.Verdict} {install.Error}");
        }
        catch (Exception ex)
        {
            _logger.Error("Printer queue installation failed.", ex);
            _host.NotifyStatus($"Printer queue installation failed: {ex.Message}");
        }
        finally
        {
            _host.NotifyBusy(false);
        }
    }

    private void CancelPrinterScan()
    {
        // Cancel() throws ObjectDisposedException if the scan already finished and its
        // finally disposed the CTS — swallow that benign race so a fast Stop-after-done
        // never bubbles an exception into the UI thread.
        CancelQuietly(_printerScanCancellation);
        _host.NotifyStatus("Stopping printer scan...");
    }

    private void RemoveSelectedPrinterScanResult()
    {
        var discovery = LastPrinterDiscovery;
        var selected = SelectedPrinterScanResult;
        if (discovery is null || selected is null)
        {
            _host.NotifyStatus("Select a scanned/SNMP printer row before removing it.");
            return;
        }

        var removedAddress = selected.Address.Trim();
        var removed = discovery.ScanResults.Remove(selected);
        if (!removed)
        {
            removed = discovery.ScanResults.RemoveAll(item =>
                item.Address.Equals(removedAddress, StringComparison.OrdinalIgnoreCase)) > 0;
        }

        if (!removed)
        {
            _host.NotifyStatus("The selected printer row was not found in the current scan results.");
            return;
        }

        SelectedPrinterScanResult = discovery.ScanResults.FirstOrDefault();
        ClearPrinterScanTargetIfStale(removedAddress);

        discovery.SelectedTarget = SelectedPrinterTarget;
        discovery.SelectedTargetSource = SelectedPrinterTargetSource;
        discovery.Verdict = discovery.ScanResults.Count > 0
            ? $"Removed {removedAddress}. {discovery.ScanResults.Count} scanned/SNMP row(s) remain."
            : $"Removed {removedAddress}. No scanned/SNMP printer rows remain.";
        discovery.Severity = discovery.ScanResults.Count > 0 ? "OK" : "Unknown";

        _host.AttachPrinterDiscovery(discovery);
        NotifyPrinterDiscoveryViewChanged();
        _host.NotifyStatus(discovery.Verdict);
    }

    private static void CancelQuietly(CancellationTokenSource? cancellation)
    {
        try { cancellation?.Cancel(); } catch (ObjectDisposedException) { }
    }

    /// <summary>
    /// "Copy for ticket" — render a plain-text summary of the currently-known printer
    /// state (local queues, print-server queues, scan results, last install) to the
    /// clipboard. Helpdesk gold: one click → paste into the ticket.
    /// </summary>
    private void CopyPrinterSummary()
    {
        var d = LastPrinterDiscovery;
        if (d is null)
        {
            _host.NotifyStatus("Nothing to copy yet — run a printer action first.");
            return;
        }

        var sb = new System.Text.StringBuilder(2048);
        sb.AppendLine($"ISG Desk — Printer Summary  ({DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss})");
        sb.AppendLine(new string('-', 60));
        if (!string.IsNullOrWhiteSpace(d.Verdict)) sb.AppendLine($"Verdict : {d.Verdict}  [{d.Severity}]");
        if (!string.IsNullOrWhiteSpace(d.Source))  sb.AppendLine($"Source  : {d.Source}");
        if (!string.IsNullOrWhiteSpace(d.LocalIpAddress)) sb.AppendLine($"PC IP   : {d.LocalIpAddress} on {d.LocalAdapterName}");
        if (!string.IsNullOrWhiteSpace(d.DetectedSubnet)) sb.AppendLine($"Subnet  : {d.DetectedSubnet}");

        if (d.LocalPrinters.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"LOCAL QUEUES ({d.LocalPrinters.Count})");
            foreach (var p in d.LocalPrinters)
                sb.AppendLine($"  • {p.Name}  |  {p.PortName}  |  {p.DriverName}  |  {p.PrinterStatus}");
        }

        if (d.Printers.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"PRINT SERVER QUEUES ({d.Printers.Count})");
            foreach (var p in d.Printers)
                sb.AppendLine($"  • {p.Name}  |  {p.ConnectionName}  |  {p.DriverName}  |  installable={p.Installable}");
        }

        if (d.ScanResults.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"NETWORK SCAN ({d.ScanResults.Count})");
            foreach (var r in d.ScanResults)
                sb.AppendLine($"  • {r.Address}  |  {r.HostName}  |  {r.Classification}  |  ports {string.Join(",", r.OpenPorts)}");
        }

        if (d.LastInstall is { } install)
        {
            sb.AppendLine();
            sb.AppendLine("LAST INSTALL");
            sb.AppendLine($"  Queue       : {install.ConnectionName}");
            sb.AppendLine($"  Result      : {install.Verdict}  [{install.Category}]");
            if (!string.IsNullOrWhiteSpace(install.Error)) sb.AppendLine($"  Error       : {install.Error}");
            sb.AppendLine($"  Next step   : {install.Remediation}");
        }

        if (!string.IsNullOrWhiteSpace(d.LocalSpoolerStatus) || !string.IsNullOrWhiteSpace(d.PrintServerSpoolerStatus))
        {
            sb.AppendLine();
            sb.AppendLine("SPOOLER");
            if (!string.IsNullOrWhiteSpace(d.LocalSpoolerStatus))       sb.AppendLine($"  Local        : {d.LocalSpoolerStatus}");
            if (!string.IsNullOrWhiteSpace(d.PrintServerSpoolerStatus)) sb.AppendLine($"  Print server : {d.PrintServerSpoolerStatus}");
        }

        try
        {
            System.Windows.Clipboard.SetText(sb.ToString());
            _host.NotifyStatus("Printer summary copied to clipboard.");
        }
        catch (Exception ex)
        {
            _host.NotifyStatus($"Copy failed: {ex.Message}");
        }
    }

    private static void ApplySnmpIdentityToRow(PrinterScanResult row, SnmpDeviceInfo snmpInfo)
    {
        row.SnmpInfo = snmpInfo;
        row.SnmpStatus = snmpInfo.Success
            ? snmpInfo.PrinterConfirmed ? "Printer confirmed by SNMP" : "SNMP responded"
            : "SNMP unavailable";

        if (snmpInfo.Success)
        {
            // A host with a print-protocol port open stays a printer even when its MIB is
            // too limited to confirm; "Not a printer" is reserved for SNMP-only targets
            // (switch/NAS/UPS the tech typed in by hand) so the printers-only filter can
            // hide exactly those.
            var hasPrintPort = row.OpenPorts.Any(port => port is 9100 or 515 or 631);
            row.Classification = snmpInfo.PrinterConfirmed
                ? "Printer confirmed by SNMP"
                : hasPrintPort
                    ? "Likely printer (SNMP identity unconfirmed)"
                    : "Not a printer (SNMP)";
            row.Confidence = snmpInfo.PrinterConfirmed ? "High" : hasPrintPort ? "Medium" : "High";
            row.Reason = snmpInfo.PrinterConfirmed
                ? "Printer-specific SNMP fields or printer identity were returned."
                : hasPrintPort
                    ? "Printer port is open but Printer-MIB did not confirm identity (limited MIB support)."
                    : "SNMP identity returned, but no printer protocol port or Printer-MIB evidence exists.";
            row.Details = FirstNonEmpty(snmpInfo.PrinterName, snmpInfo.SysDescr, row.Details, "SNMP identity returned.");
            return;
        }

        row.Reason = "SNMP did not return a printer identity.";
        if (string.IsNullOrWhiteSpace(row.Details))
        {
            row.Details = snmpInfo.Error;
        }
    }

    private static string BuildAutoSnmpVerdict(int confirmed, int responded, int failed, int skipped)
    {
        var skippedText = skipped > 0
            ? $" {skipped} additional candidate(s) were skipped by the {DiagnosticConstants.MaxPrinterAutoSnmpTargets}-target safety cap."
            : string.Empty;

        return confirmed > 0
            ? $"SNMP auto-identify confirmed {confirmed} printer(s). {responded} other device(s) responded; {failed} unavailable.{skippedText}"
            : $"SNMP auto-identify did not confirm a printer. {responded} device(s) responded; {failed} unavailable.{skippedText}";
    }

    public SnmpSessionOptions BuildSnmpOptions(string target) => _snmpCredentialStore.BuildSessionOptions(target);

    private bool TryBuildValidatedSnmpOptions(
        string target,
        out SnmpSessionOptions options,
        out string validationReason)
    {
        options = new SnmpSessionOptions();
        if (!DiagnosticTargetValidator.TryNormalizeHost(target, out var normalizedTarget, out validationReason))
        {
            return false;
        }

        if (IPAddress.TryParse(normalizedTarget, out var ipAddress) &&
            (ipAddress.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork ||
             !NetworkSafetyPolicy.IsPrivateIpv4(ipAddress)))
        {
            validationReason = SnmpPrivateTargetMessage;
            return false;
        }

        options = BuildSnmpOptions(normalizedTarget);
        options.Target = normalizedTarget;
        return TryValidateSnmpCredentialOptions(options, out validationReason);
    }

    private bool TryValidateSnmpCredentialOptions(SnmpSessionOptions options, out string validationReason)
    {
        validationReason = string.Empty;
        if (options.Protocol is not SnmpProtocolVersion.V2C and not SnmpProtocolVersion.V3AuthPriv)
        {
            validationReason = "Select a supported SNMP protocol before identifying a printer.";
            return false;
        }

        if (options.Protocol == SnmpProtocolVersion.V3AuthPriv &&
            (string.IsNullOrWhiteSpace(options.UserName) ||
             string.IsNullOrWhiteSpace(options.AuthPassword) ||
             string.IsNullOrWhiteSpace(options.PrivacyPassword)))
        {
            validationReason = SnmpV3RequiredMessage;
            return false;
        }

        return true;
    }

    private void StoreSnmpValidationFailure(string target, string validationReason)
    {
        var current = LastPrinterDiscovery ?? CreatePrinterSession();
        var normalizedTarget = target.Trim();
        current.Mode = "SNMP";
        current.Source = normalizedTarget;
        current.SelectedTarget = normalizedTarget;
        current.SelectedTargetSource = SelectedPrinterTargetSource;
        current.SnmpProtocol = SelectedSnmpProtocol;
        current.SnmpCommunityMasked = SelectedSnmpProtocol == SnmpProtocolVersion.V2C
            ? MaskCommunity(SnmpCommunity)
            : "SNMPv3 credentials required";
        current.Verdict = validationReason;
        current.Severity = "Warning";
        AddDistinct(current.Evidence, [$"SNMP validation stopped before network I/O: {validationReason}"]);
        AddDistinct(current.Limitations, [SnmpValidationScope]);
        TrimRolling(current.Evidence, MaxEvidenceLines);
        TrimRolling(current.Limitations, MaxEvidenceLines);
        LastPrinterDiscovery = current;
        _host.AttachPrinterDiscovery(current);
        NotifyPrinterDiscoveryViewChanged();
    }

    private void LoadSavedSnmpV3Credential()
    {
        _snmpCredentialStore.Load();
        OnPropertyChanged(nameof(HasSavedSnmpCredentials));
    }

    private void ForgetSnmpCredential()
    {
        _snmpCredentialStore.Forget();
        SaveSnmpCredentials = false;
        OnPropertyChanged(nameof(HasSavedSnmpCredentials));
        _host.NotifyStatus("Saved SNMP credentials were removed.");
    }

    private void NotifySnmpCredentialChanged(string? propertyName)
    {
        switch (propertyName)
        {
            case nameof(SnmpCredentialStore.Protocol):
                OnPropertyChanged(nameof(SelectedSnmpProtocol));
                OnPropertyChanged(nameof(IsSnmpV3Selected));
                OnPropertyChanged(nameof(IsSnmpV2Selected));
                break;
            case nameof(SnmpCredentialStore.IsV3):
                OnPropertyChanged(nameof(IsSnmpV3Selected));
                OnPropertyChanged(nameof(IsSnmpV2Selected));
                break;
            case nameof(SnmpCredentialStore.Community):
                OnPropertyChanged(nameof(SnmpCommunity));
                break;
            case nameof(SnmpCredentialStore.UserName):
                OnPropertyChanged(nameof(SnmpV3UserName));
                break;
            case nameof(SnmpCredentialStore.AuthProtocol):
                OnPropertyChanged(nameof(SnmpV3AuthProtocol));
                break;
            case nameof(SnmpCredentialStore.AuthPassword):
                OnPropertyChanged(nameof(SnmpV3AuthPassword));
                break;
            case nameof(SnmpCredentialStore.PrivacyProtocol):
                OnPropertyChanged(nameof(SnmpV3PrivacyProtocol));
                break;
            case nameof(SnmpCredentialStore.PrivacyPassword):
                OnPropertyChanged(nameof(SnmpV3PrivacyPassword));
                break;
            case nameof(SnmpCredentialStore.HasSavedCredentials):
                OnPropertyChanged(nameof(HasSavedSnmpCredentials));
                break;
        }
    }

    private void MergePrinterDiscovery(
        PrinterDiscoveryResult update,
        bool replaceLocal = false,
        bool replacePrintServer = false,
        bool replaceScan = false,
        bool attach = true)
    {
        var current = LastPrinterDiscovery ?? CreatePrinterSession();
        var selectedLocalPrinterName = SelectedLocalPrinter?.Name;
        var selectedScanAddress = SelectedPrinterScanResult?.Address;
        current.Mode = update.Mode;
        current.Source = FirstNonEmpty(update.Source, current.Source);
        current.Verdict = update.Verdict;
        current.Severity = update.Severity;
        current.SkippedReason = update.SkippedReason;
        current.SnmpCommunityMasked = FirstNonEmpty(update.SnmpCommunityMasked, current.SnmpCommunityMasked);
        current.LocalIpAddress = FirstNonEmpty(update.LocalIpAddress, current.LocalIpAddress);
        current.LocalAdapterName = FirstNonEmpty(update.LocalAdapterName, current.LocalAdapterName);
        current.LocalPrefixLength = update.LocalPrefixLength ?? current.LocalPrefixLength;
        current.Gateway = FirstNonEmpty(update.Gateway, current.Gateway);
        current.RouteMetric = update.RouteMetric > 0 ? update.RouteMetric : current.RouteMetric;
        current.InterfaceMetric = update.InterfaceMetric > 0 ? update.InterfaceMetric : current.InterfaceMetric;
        current.DetectedSubnet = FirstNonEmpty(update.DetectedSubnet, current.DetectedSubnet);
        current.DetectedSubnetHosts = update.DetectedSubnetHosts > 0 ? update.DetectedSubnetHosts : current.DetectedSubnetHosts;
        current.SafeScanRange = FirstNonEmpty(update.SafeScanRange, current.SafeScanRange);
        current.MaxHosts = update.MaxHosts > 0 ? update.MaxHosts : current.MaxHosts;
        current.LocalSpoolerStatus = FirstNonEmpty(update.LocalSpoolerStatus, current.LocalSpoolerStatus);
        current.PrintServerSpoolerStatus = FirstNonEmpty(update.PrintServerSpoolerStatus, current.PrintServerSpoolerStatus);
        current.LastInstallResult = FirstNonEmpty(update.LastInstallResult, current.LastInstallResult);
        current.SelectedTarget = FirstNonEmpty(update.SelectedTarget, current.SelectedTarget);
        current.SelectedTargetSource = FirstNonEmpty(update.SelectedTargetSource, current.SelectedTargetSource);
        current.SnmpProtocol = FirstNonEmpty(update.SnmpProtocol, current.SnmpProtocol);
        current.LastInstall = update.LastInstall ?? current.LastInstall;

        var localPrintersChanged = replaceLocal || update.LocalPrinters.Count > 0;
        if (localPrintersChanged)
            current.LocalPrinters = update.LocalPrinters;
        if (replacePrintServer || update.Printers.Count > 0)
            current.Printers = update.Printers;
        if (replaceScan || update.ScanResults.Count > 0)
            current.ScanResults = update.ScanResults;

        AddDistinct(current.Evidence, update.Evidence);
        AddDistinct(current.Limitations, update.Limitations);
        // Cap rolling history so a long support session can't bloat UI/memory by
        // appending hundreds of lines into one ObservableCollection.
        TrimRolling(current.Evidence, MaxEvidenceLines);
        TrimRolling(current.Limitations, MaxEvidenceLines);

        LastPrinterDiscovery = current;
        if (localPrintersChanged)
        {
            SelectedLocalPrinter = FindPrinterByName(current.LocalPrinters, selectedLocalPrinterName)
                ?? current.LocalPrinters.FirstOrDefault(printer => printer.IsDefault)
                ?? current.LocalPrinters.FirstOrDefault();
        }

        if (replacePrintServer || update.Printers.Count > 0)
            SelectedPrintServerPrinter = current.Printers.FirstOrDefault();
        if (replaceScan || update.ScanResults.Count > 0)
        {
            SelectedPrinterScanResult = current.ScanResults.FirstOrDefault();
            if (SelectedPrinterScanResult is null)
            {
                ClearPrinterScanTargetIfStale(selectedScanAddress);
            }

            current.SelectedTarget = SelectedPrinterTarget;
            current.SelectedTargetSource = SelectedPrinterTargetSource;
        }

        if (attach)
            _host.AttachPrinterDiscovery(current);
        NotifyPrinterDiscoveryViewChanged();
    }

    private void ApplySelectedPrinterNetworkAdapterToDiscovery(PrinterNetworkAdapterInfo? adapter)
    {
        var current = LastPrinterDiscovery ?? CreatePrinterSession();
        current.Mode = "AdapterOverview";
        current.Source = Environment.MachineName;
        current.MaxHosts = DiagnosticConstants.MaxPrinterScanHosts;

        if (adapter is null)
        {
            current.Verdict = "No usable IPv4 default-route adapter was detected.";
            current.Severity = "Warning";
            current.LocalIpAddress = string.Empty;
            current.LocalAdapterName = string.Empty;
            current.LocalPrefixLength = null;
            current.Gateway = string.Empty;
            current.RouteMetric = 0;
            current.InterfaceMetric = 0;
            current.DetectedSubnet = string.Empty;
            current.DetectedSubnetHosts = 0;
            current.SafeScanRange = string.Empty;
            AddDistinct(current.Evidence, ["No usable private IPv4 default-route adapter was detected for printer scan context."]);
            AddDistinct(current.Limitations, ["Printer scan needs a private IPv4 adapter with a usable local route."]);
        }
        else
        {
            current.LocalIpAddress = adapter.LocalIpAddress;
            current.LocalAdapterName = adapter.AdapterName;
            current.LocalPrefixLength = adapter.PrefixLength;
            current.Gateway = adapter.Gateway;
            current.RouteMetric = adapter.RouteMetric;
            current.InterfaceMetric = adapter.InterfaceMetric;
            current.DetectedSubnet = adapter.DetectedSubnet;
            current.DetectedSubnetHosts = adapter.DetectedSubnetHosts;
            current.SafeScanRange = adapter.SafeScanRange;
            current.Verdict = $"Printer overview refreshed for {adapter.LocalIpAddress} on {adapter.AdapterName}.";
            current.Severity = "OK";
            AddDistinct(current.Evidence,
            [
                $"Selected adapter {adapter.AdapterName} with IPv4 {adapter.LocalIpAddress}.",
                $"Gateway {adapter.Gateway}; route metric {adapter.RouteMetric}; interface metric {adapter.InterfaceMetric}.",
                $"Safe printer scan range {adapter.SafeScanRange}."
            ]);
            if (!string.IsNullOrWhiteSpace(adapter.Warning))
            {
                AddDistinct(current.Limitations, [adapter.Warning]);
            }
        }

        TrimRolling(current.Evidence, MaxEvidenceLines);
        TrimRolling(current.Limitations, MaxEvidenceLines);
        LastPrinterDiscovery = current;
        _host.AttachPrinterDiscovery(current);
        NotifyPrinterDiscoveryViewChanged();
    }

    private static PrinterDiscoveryResult CreatePrinterSession() => new()
    {
        Mode = "Printers",
        Verdict = "Printer tools ready. Choose an action below.",
        Severity = "Unknown",
        MaxHosts = 254
    };

    public static string MaskCommunity(string community) =>
        string.IsNullOrWhiteSpace(community) ? "(empty)" : "********";

    /// <summary>Rolling-history cap so Evidence/Limitations can't grow unbounded across a long session.</summary>
    private const int MaxEvidenceLines = 200;
    private const string PrinterScanTargetSource = "Printer IP scan";
    private const string PrintServerValidationScope =
        "Print Server discovery accepts only a LAN host: a single-label NetBIOS name (no dots) or a private IPv4 (10/8, 172.16/12, 192.168/16).";
    private const string SnmpPrivateTargetMessage = "SNMP target must resolve to an authorized private IPv4 address.";
    private const string SnmpV3RequiredMessage =
        "SNMPv3 authPriv requires username, authentication password and privacy password before running.";
    private const string SnmpValidationScope =
        "Printer SNMP is read-only and limited to authorized private IPv4 printer targets; SNMPv3 credentials are saved only after required fields are present.";

    private void ClearPrinterScanTargetIfStale(string? scanAddress)
    {
        if ((SelectedPrinterTargetSource == PrinterScanTargetSource &&
             (string.IsNullOrWhiteSpace(scanAddress) ||
              SelectedPrinterTarget.Equals(scanAddress, StringComparison.OrdinalIgnoreCase))) ||
            (!string.IsNullOrWhiteSpace(scanAddress) &&
             SelectedPrinterTarget.Equals(scanAddress, StringComparison.OrdinalIgnoreCase)))
        {
            SelectedPrinterTarget = string.Empty;
            SelectedPrinterTargetSource = "Manual";
        }

        if (!string.IsNullOrWhiteSpace(scanAddress) &&
            PrinterTarget.Equals(scanAddress, StringComparison.OrdinalIgnoreCase))
        {
            PrinterTarget = string.Empty;
        }
    }

    private static void AddDistinct(List<string> target, IEnumerable<string> values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value) &&
                !target.Any(existing => existing.Equals(value, StringComparison.OrdinalIgnoreCase)))
            {
                target.Add(value);
            }
        }
    }

    /// <summary>Drop the oldest entries when the list exceeds <paramref name="max"/>.</summary>
    private static void TrimRolling(List<string> list, int max)
    {
        if (list.Count <= max) return;
        list.RemoveRange(0, list.Count - max);
    }

    private static IReadOnlyList<PrintServerCandidate> DetectPrintServerCandidates(IEnumerable<PrinterInfo> printers)
    {
        var results = new Dictionary<string, PrintServerCandidateBuilder>(StringComparer.OrdinalIgnoreCase);
        foreach (var printer in printers)
        {
            AddPrintServerCandidate(results, printer, printer.ConnectionName);
            AddPrintServerCandidate(results, printer, printer.Name);
        }

        return results.Values
            .Select(item => new PrintServerCandidate(item.Server, item.Source, item.Count))
            .OrderByDescending(item => item.Count)
            .ThenBy(item => item.Server, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void AddPrintServerCandidate(
        Dictionary<string, PrintServerCandidateBuilder> results,
        PrinterInfo printer,
        string value)
    {
        if (!PrinterTargetValidator.TryNormalizeSharedQueueConnection(value, out var normalized, out _))
        {
            return;
        }

        var server = normalized[2..].Split('\\', 2)[0];
        if (server.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (!results.TryGetValue(server, out var existing))
        {
            results[server] = new PrintServerCandidateBuilder(
                server,
                FirstNonEmpty(printer.Name, printer.ConnectionName, normalized),
                1);
            return;
        }

        existing.Count++;
    }

    private static string FirstNonEmpty(params string[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? string.Empty;

    private static PrinterInfo? FindPrinterByName(IEnumerable<PrinterInfo>? printers, string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        return printers?.FirstOrDefault(printer =>
            printer.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    private static PrinterInfo? FindPrinterByConnectionName(IEnumerable<PrinterInfo>? printers, string? connectionName)
    {
        if (string.IsNullOrWhiteSpace(connectionName))
        {
            return null;
        }

        return printers?.FirstOrDefault(printer =>
            printer.ConnectionName.Equals(connectionName, StringComparison.OrdinalIgnoreCase));
    }

    private sealed record PrintServerCandidate(string Server, string Source, int Count);

    private sealed class PrintServerCandidateBuilder(string server, string source, int count)
    {
        public string Server { get; } = server;
        public string Source { get; } = source;
        public int Count { get; set; } = count;
    }
}
