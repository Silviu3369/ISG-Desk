using System.Windows.Input;
using NetScopeDiagnosticCenter.Core.Models;
using NetScopeDiagnosticCenter.UI.ViewModels;

namespace NetScopeDiagnosticCenter.UI;

/// <summary>
/// Phase 2d migration shim: real diagnosis logic lives in <see cref="DiagnosisViewModel"/>.
/// MainWindow.xaml binds to MainViewModel; every member here delegates to <c>Diagnosis.X</c>.
/// </summary>
public sealed partial class MainViewModel : DiagnosisViewModel.IHost
{
    /// <summary>Dedicated diagnosis VM. Constructed in MainViewModel.cs ctor.</summary>
    public DiagnosisViewModel Diagnosis { get; private set; } = null!;

    public string TargetHost { get => Diagnosis.TargetHost; set => Diagnosis.TargetHost = value; }
    public int TargetPort { get => Diagnosis.TargetPort; set => Diagnosis.TargetPort = value; }
    public bool IsDiagnosisRunning => Diagnosis.IsDiagnosisRunning;
    public bool IsPortTestRunning => Diagnosis.IsPortTestRunning;
    public string DiagnoseButtonText => Diagnosis.DiagnoseButtonText;
    public string DiagnoseButtonIcon => Diagnosis.DiagnoseButtonIcon;
    public string PortTestButtonText => Diagnosis.PortTestButtonText;
    public string PortTestButtonIcon => Diagnosis.PortTestButtonIcon;
    public string SelectedPortDescription => Diagnosis.SelectedPortDescription;
    public bool CanRunPortTest => Diagnosis.CanRunPortTest;
    public string PortTestInputStatusText => Diagnosis.PortTestInputStatusText;
    public string PortTestInputSeverity => Diagnosis.PortTestInputSeverity;
    public string LastPortTestInterpretation => Diagnosis.LastPortTestInterpretation;
    public IReadOnlyList<PortTestPortOption> PortTestPortOptions => Diagnosis.PortTestPortOptions;
    public IReadOnlyList<PortTestTargetOption> PortTestTargetOptions => Diagnosis.PortTestTargetOptions;

    public ICommand DiagnoseCommand => Diagnosis.DiagnoseCommand;
    public ICommand CancelDiagnoseCommand => Diagnosis.CancelDiagnoseCommand;
    public ICommand RunPortTestCommand => Diagnosis.RunPortTestCommand;
    public ICommand CancelPortTestCommand => Diagnosis.CancelPortTestCommand;
    public ICommand ApplyPortTestTargetPresetCommand => Diagnosis.ApplyPortTestTargetPresetCommand;

    // === DiagnosisViewModel.IHost ===
    void DiagnosisViewModel.IHost.NotifyStatus(string message) => PublishStatus(ActivitySourceModule.QuickDiagnosis, message);
    void DiagnosisViewModel.IHost.NotifyBusy(bool busy) => SetBusyState(busy);

    // The IHost LastDiagnosis getter/setter must read/write through MainViewModel's
    // own LastDiagnosis property (defined in MainViewModel.cs) so the cascade
    // (Reports/TechnicianHome/LinkQuality/TargetedTests refresh) still fires.
    NetworkDiagnosisResult? DiagnosisViewModel.IHost.LastDiagnosis
    {
        get => LastDiagnosis;
        set => LastDiagnosis = value;
    }

    // Read-only views of state owned by the other migrated VMs.
    PrinterDiscoveryResult? DiagnosisViewModel.IHost.LastPrinterDiscovery => LastPrinterDiscovery;
    NetworkDeviceResult? DiagnosisViewModel.IHost.LastNetworkDevice => LastNetworkDevice;
    NetworkDeviceScanResult? DiagnosisViewModel.IHost.LastNetworkDeviceScan => LastNetworkDeviceScan;
    NetworkProfile DiagnosisViewModel.IHost.NetworkProfile => NetworkProfile;

    string DiagnosisViewModel.IHost.CurrentPage
    {
        get => CurrentPage;
        set => CurrentPage = value;
    }

    /// <summary>Re-fires LastDiagnosis-derived bindings after an in-place mutation (port test path).</summary>
    void DiagnosisViewModel.IHost.NotifyDiagnosisChanged()
    {
        NotifyDiagnosisSurfaceChanged();
        Reports?.NotifyDiagnosisChanged();
        RefreshTechnicianHomeStatus();
    }
}
