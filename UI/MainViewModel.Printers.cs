using System.Windows.Input;
using NetScopeDiagnosticCenter.Core.Models;
using NetScopeDiagnosticCenter.UI.ViewModels;

namespace NetScopeDiagnosticCenter.UI;

/// <summary>
/// Phase 2c migration shim: real printer logic lives in <see cref="PrintersViewModel"/>.
/// MainWindow.xaml binds to MainViewModel; every member here delegates to <c>Printers.X</c>.
/// </summary>
public sealed partial class MainViewModel : PrintersViewModel.IHost
{
    /// <summary>Dedicated printers VM. Constructed in MainViewModel.cs ctor.</summary>
    public PrintersViewModel Printers { get; private set; } = null!;

    // === Lookup lists (collection sources for ComboBox bindings) ===
    public IReadOnlyList<string> SnmpProtocols => Printers.SnmpProtocols;
    public IReadOnlyList<string> SnmpAuthProtocols => Printers.SnmpAuthProtocols;
    public IReadOnlyList<string> SnmpPrivacyProtocols => Printers.SnmpPrivacyProtocols;
    public List<PrinterNetworkAdapterInfo> PrinterNetworkAdapters => Printers.PrinterNetworkAdapters;

    // === Printer state delegations ===
    public string PrinterTarget { get => Printers.PrinterTarget; set => Printers.PrinterTarget = value; }
    public string SelectedPrinterTarget { get => Printers.SelectedPrinterTarget; set => Printers.SelectedPrinterTarget = value; }
    public string SelectedPrinterTargetSource { get => Printers.SelectedPrinterTargetSource; set => Printers.SelectedPrinterTargetSource = value; }
    public string PrintServerName { get => Printers.PrintServerName; set => Printers.PrintServerName = value; }
    public string PrinterScanRange { get => Printers.PrinterScanRange; set => Printers.PrinterScanRange = value; }

    public PrinterInfo? SelectedPrintServerPrinter { get => Printers.SelectedPrintServerPrinter; set => Printers.SelectedPrintServerPrinter = value; }
    public PrinterScanResult? SelectedPrinterScanResult { get => Printers.SelectedPrinterScanResult; set => Printers.SelectedPrinterScanResult = value; }
    public PrinterNetworkAdapterInfo? SelectedPrinterNetworkAdapter { get => Printers.SelectedPrinterNetworkAdapter; set => Printers.SelectedPrinterNetworkAdapter = value; }
    public PrinterInfo? SelectedLocalPrinter { get => Printers.SelectedLocalPrinter; set => Printers.SelectedLocalPrinter = value; }

    public PrinterDiscoveryResult? LastPrinterDiscovery
    {
        get => Printers.LastPrinterDiscovery;
        set => Printers.LastPrinterDiscovery = value;
    }

    // === SNMP credential delegations ===
    public string SnmpCommunity { get => Printers.SnmpCommunity; set => Printers.SnmpCommunity = value; }
    public string SelectedSnmpProtocol { get => Printers.SelectedSnmpProtocol; set => Printers.SelectedSnmpProtocol = value; }
    public bool IsSnmpV3Selected => Printers.IsSnmpV3Selected;
    public bool IsSnmpV2Selected => Printers.IsSnmpV2Selected;
    public string SnmpV3UserName { get => Printers.SnmpV3UserName; set => Printers.SnmpV3UserName = value; }
    public string SnmpV3AuthProtocol { get => Printers.SnmpV3AuthProtocol; set => Printers.SnmpV3AuthProtocol = value; }
    public string SnmpV3AuthPassword { get => Printers.SnmpV3AuthPassword; set => Printers.SnmpV3AuthPassword = value; }
    public string SnmpV3PrivacyProtocol { get => Printers.SnmpV3PrivacyProtocol; set => Printers.SnmpV3PrivacyProtocol = value; }
    public string SnmpV3PrivacyPassword { get => Printers.SnmpV3PrivacyPassword; set => Printers.SnmpV3PrivacyPassword = value; }
    public bool SaveSnmpCredentials { get => Printers.SaveSnmpCredentials; set => Printers.SaveSnmpCredentials = value; }
    public bool HasSavedSnmpCredentials => Printers.HasSavedSnmpCredentials;
    public bool ConfirmPrinterInstall { get => Printers.ConfirmPrinterInstall; set => Printers.ConfirmPrinterInstall = value; }
    public bool SetDefaultAfterInstall { get => Printers.SetDefaultAfterInstall; set => Printers.SetDefaultAfterInstall = value; }
    public bool IsPrinterScanRunning => Printers.IsPrinterScanRunning;
    public bool IsPrinterSnmpIdentifyRunning => Printers.IsPrinterSnmpIdentifyRunning;

    // === Printers-only scan filter (hides SNMP-confirmed non-printers) ===
    public bool ShowOnlyPrinters { get => Printers.ShowOnlyPrinters; set => Printers.ShowOnlyPrinters = value; }
    public IReadOnlyList<PrinterScanResult> FilteredScanResults => Printers.FilteredScanResults;
    public string HiddenScanResultCountText => Printers.HiddenScanResultCountText;
    public bool HasHiddenScanResults => Printers.HasHiddenScanResults;

    // === Command delegations (expression-bodied) ===
    public ICommand DetectPrinterIpCommand => Printers.DetectPrinterIpCommand;
    public ICommand LoadLocalPrintersCommand => Printers.LoadLocalPrintersCommand;
    public ICommand DetectPrintServerCommand => Printers.DetectPrintServerCommand;
    public ICommand DiscoverPrintServerCommand => Printers.DiscoverPrintServerCommand;
    public ICommand ScanPrintersCommand => Printers.ScanPrintersCommand;
    public ICommand ScanPrinterRangeCommand => Printers.ScanPrinterRangeCommand;
    public ICommand IdentifySelectedPrinterSnmpCommand => Printers.IdentifySelectedPrinterSnmpCommand;
    public ICommand AutoIdentifyScannedPrintersSnmpCommand => Printers.AutoIdentifyScannedPrintersSnmpCommand;
    public ICommand InstallSelectedPrinterQueueCommand => Printers.InstallSelectedPrinterQueueCommand;
    public ICommand ForgetSnmpCredentialCommand => Printers.ForgetSnmpCredentialCommand;
    public ICommand CancelPrinterScanCommand => Printers.CancelPrinterScanCommand;
    public ICommand CancelPrinterSnmpIdentifyCommand => Printers.CancelPrinterSnmpIdentifyCommand;
    public ICommand CopyPrinterSummaryCommand => Printers.CopyPrinterSummaryCommand;
    public ICommand SetDefaultPrinterCommand => Printers.SetDefaultPrinterCommand;
    public ICommand RemoveSelectedPrinterScanResultCommand => Printers.RemoveSelectedPrinterScanResultCommand;

    // === PrintersViewModel.IHost ===
    void PrintersViewModel.IHost.NotifyStatus(string message) => PublishStatus(ActivitySourceModule.Printers, message);
    void PrintersViewModel.IHost.NotifyBusy(bool busy) => SetBusyState(busy);

    void PrintersViewModel.IHost.AttachPrinterDiscovery(PrinterDiscoveryResult discovery)
    {
        // Printer actions are independent: they must not create a synthetic Quick Diagnosis
        // result just so reports have a parent object. If a real diagnosis already exists,
        // enrich it with the latest printer evidence for future exports.
        if (LastDiagnosis is not null)
        {
            LastDiagnosis.LastPrinterDiscovery = discovery;
            NotifyDiagnosisSurfaceChanged();
            Reports?.NotifyDiagnosisChanged();
        }

        RefreshTechnicianHomeStatus();
    }

    void PrintersViewModel.IHost.RememberPrinterTarget(string target)
    {
        RememberValue(_recentTargets.PrinterTargets, target);
        _appStorage.SaveRecentTargets(_recentTargets);
        OnPropertyChanged(nameof(RecentPrinterTargets));
    }

    void PrintersViewModel.IHost.RememberPrintServer(string server)
    {
        _recentTargets.LastPrintServer = server;
        _appStorage.SaveRecentTargets(_recentTargets);
    }
}
