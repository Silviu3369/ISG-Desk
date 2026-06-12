using System.Windows.Input;
using NetScopeDiagnosticCenter.Core.Models;
using NetScopeDiagnosticCenter.UI.ViewModels;

namespace NetScopeDiagnosticCenter.UI;

/// <summary>
/// Phase 2c migration shim: real network-device logic lives in <see cref="NetworkDevicesViewModel"/>.
/// MainWindow.xaml binds to MainViewModel; every member here delegates to <c>NetworkDevices.X</c>.
/// </summary>
public sealed partial class MainViewModel : NetworkDevicesViewModel.IHost
{
    /// <summary>Dedicated network-devices VM. Constructed in MainViewModel.cs ctor.</summary>
    public NetworkDevicesViewModel NetworkDevices { get; private set; } = null!;

    // === Lookup lists ===
    public IReadOnlyList<string> NetworkDeviceSnmpProtocols => NetworkDevices.NetworkDeviceSnmpProtocols;
    public IReadOnlyList<string> NetworkDeviceSnmpAuthProtocols => NetworkDevices.NetworkDeviceSnmpAuthProtocols;
    public IReadOnlyList<string> NetworkDeviceSnmpPrivacyProtocols => NetworkDevices.NetworkDeviceSnmpPrivacyProtocols;
    public IReadOnlyList<string> NetworkDeviceInterfaceFilters => NetworkDevices.NetworkDeviceInterfaceFilters;

    // === Target / range / detection ===
    public string NetworkDeviceTarget { get => NetworkDevices.NetworkDeviceTarget; set => NetworkDevices.NetworkDeviceTarget = value; }
    public string NetworkDeviceScanRange { get => NetworkDevices.NetworkDeviceScanRange; set => NetworkDevices.NetworkDeviceScanRange = value; }
    public string NetworkDeviceDetectedIp { get => NetworkDevices.NetworkDeviceDetectedIp; set => NetworkDevices.NetworkDeviceDetectedIp = value; }
    public string NetworkDeviceDetectedRange { get => NetworkDevices.NetworkDeviceDetectedRange; set => NetworkDevices.NetworkDeviceDetectedRange = value; }
    public string NetworkDeviceDetectedIpDisplay => NetworkDevices.NetworkDeviceDetectedIpDisplay;
    public string NetworkDeviceDetectedRangeDisplay => NetworkDevices.NetworkDeviceDetectedRangeDisplay;

    // === SNMP ===
    public string NetworkDeviceCommunity { get => NetworkDevices.NetworkDeviceCommunity; set => NetworkDevices.NetworkDeviceCommunity = value; }
    public string NetworkDeviceSelectedSnmpProtocol { get => NetworkDevices.NetworkDeviceSelectedSnmpProtocol; set => NetworkDevices.NetworkDeviceSelectedSnmpProtocol = value; }
    public bool IsNetworkDeviceSnmpV2Selected => NetworkDevices.IsNetworkDeviceSnmpV2Selected;
    public bool IsNetworkDeviceSnmpV3Selected => NetworkDevices.IsNetworkDeviceSnmpV3Selected;
    public string NetworkDeviceSnmpV3UserName { get => NetworkDevices.NetworkDeviceSnmpV3UserName; set => NetworkDevices.NetworkDeviceSnmpV3UserName = value; }
    public string NetworkDeviceSnmpV3AuthProtocol { get => NetworkDevices.NetworkDeviceSnmpV3AuthProtocol; set => NetworkDevices.NetworkDeviceSnmpV3AuthProtocol = value; }
    public string NetworkDeviceSnmpV3AuthPassword { get => NetworkDevices.NetworkDeviceSnmpV3AuthPassword; set => NetworkDevices.NetworkDeviceSnmpV3AuthPassword = value; }
    public string NetworkDeviceSnmpV3PrivacyProtocol { get => NetworkDevices.NetworkDeviceSnmpV3PrivacyProtocol; set => NetworkDevices.NetworkDeviceSnmpV3PrivacyProtocol = value; }
    public string NetworkDeviceSnmpV3PrivacyPassword { get => NetworkDevices.NetworkDeviceSnmpV3PrivacyPassword; set => NetworkDevices.NetworkDeviceSnmpV3PrivacyPassword = value; }
    public bool SaveNetworkDeviceSnmpCredentials { get => NetworkDevices.SaveNetworkDeviceSnmpCredentials; set => NetworkDevices.SaveNetworkDeviceSnmpCredentials = value; }
    public bool HasSavedNetworkDeviceSnmpCredentials => NetworkDevices.HasSavedNetworkDeviceSnmpCredentials;

    // === Interface filtering ===
    public string NetworkDeviceInterfaceFilter { get => NetworkDevices.NetworkDeviceInterfaceFilter; set => NetworkDevices.NetworkDeviceInterfaceFilter = value; }
    public IReadOnlyList<NetworkDeviceInterfaceInfo> FilteredNetworkDeviceInterfaces => NetworkDevices.FilteredNetworkDeviceInterfaces;
    public string NetworkDeviceInterfaceFilterSummary => NetworkDevices.NetworkDeviceInterfaceFilterSummary;
    public bool HasNetworkDeviceConfirmedFindings => NetworkDevices.HasNetworkDeviceConfirmedFindings;
    public bool HasNoNetworkDeviceConfirmedFindings => NetworkDevices.HasNoNetworkDeviceConfirmedFindings;
    public bool HasNetworkDeviceProbableFindings => NetworkDevices.HasNetworkDeviceProbableFindings;
    public bool HasNoNetworkDeviceProbableFindings => NetworkDevices.HasNoNetworkDeviceProbableFindings;
    public bool HasNetworkDeviceUnknownFindings => NetworkDevices.HasNetworkDeviceUnknownFindings;
    public bool HasNoNetworkDeviceUnknownFindings => NetworkDevices.HasNoNetworkDeviceUnknownFindings;
    public bool HasNetworkDeviceEvidence => NetworkDevices.HasNetworkDeviceEvidence;
    public bool HasNoNetworkDeviceEvidence => NetworkDevices.HasNoNetworkDeviceEvidence;
    public bool HasNetworkDeviceLimitations => NetworkDevices.HasNetworkDeviceLimitations;
    public bool HasNoNetworkDeviceLimitations => NetworkDevices.HasNoNetworkDeviceLimitations;
    public bool HasNetworkDeviceRecommendations => NetworkDevices.HasNetworkDeviceRecommendations;
    public bool HasNoNetworkDeviceRecommendations => NetworkDevices.HasNoNetworkDeviceRecommendations;
    public bool HasNetworkDevicePortsNeedingAttention => NetworkDevices.HasNetworkDevicePortsNeedingAttention;
    public bool HasNoNetworkDevicePortsNeedingAttention => NetworkDevices.HasNoNetworkDevicePortsNeedingAttention;
    public bool HasFilteredNetworkDeviceInterfaces => NetworkDevices.HasFilteredNetworkDeviceInterfaces;
    public bool HasNoFilteredNetworkDeviceInterfaces => NetworkDevices.HasNoFilteredNetworkDeviceInterfaces;
    public string FilteredNetworkDeviceInterfacesEmptyText => NetworkDevices.FilteredNetworkDeviceInterfacesEmptyText;
    public bool HasNetworkDeviceScanResults => NetworkDevices.HasNetworkDeviceScanResults;
    public bool HasNoNetworkDeviceScanResults => NetworkDevices.HasNoNetworkDeviceScanResults;
    public bool HasSelectedNetworkDeviceScanResult => NetworkDevices.HasSelectedNetworkDeviceScanResult;
    public string NetworkDeviceScanResultsEmptyText => NetworkDevices.NetworkDeviceScanResultsEmptyText;
    public string SelectedNetworkDeviceScanResultSummary => NetworkDevices.SelectedNetworkDeviceScanResultSummary;

    // === State ===
    public NetworkDeviceResult? LastNetworkDevice { get => NetworkDevices.LastNetworkDevice; set => NetworkDevices.LastNetworkDevice = value; }
    public NetworkDeviceScanResult? LastNetworkDeviceScan { get => NetworkDevices.LastNetworkDeviceScan; set => NetworkDevices.LastNetworkDeviceScan = value; }
    public NetworkDeviceResult? SelectedNetworkDeviceScanResult { get => NetworkDevices.SelectedNetworkDeviceScanResult; set => NetworkDevices.SelectedNetworkDeviceScanResult = value; }
    public bool IsNetworkDeviceScanRunning => NetworkDevices.IsNetworkDeviceScanRunning;
    public string HeadlineVerdict => NetworkDevices.HeadlineVerdict;
    public string HeadlineSeverity => NetworkDevices.HeadlineSeverity;

    // === Commands ===
    public ICommand IdentifyNetworkDeviceCommand => NetworkDevices.IdentifyNetworkDeviceCommand;
    public ICommand ReadNetworkDeviceInterfacesCommand => NetworkDevices.ReadNetworkDeviceInterfacesCommand;
    public ICommand ReadSelectedNetworkDeviceInterfacesCommand => NetworkDevices.ReadSelectedNetworkDeviceInterfacesCommand;
    public ICommand DetectNetworkDeviceScanRangeCommand => NetworkDevices.DetectNetworkDeviceScanRangeCommand;
    public ICommand ScanLocalNetworkDevicesCommand => NetworkDevices.ScanLocalNetworkDevicesCommand;
    public ICommand ScanNetworkDeviceRangeCommand => NetworkDevices.ScanNetworkDeviceRangeCommand;
    public ICommand CancelNetworkDeviceScanCommand => NetworkDevices.CancelNetworkDeviceScanCommand;
    public ICommand CopyNetworkDeviceSummaryCommand => NetworkDevices.CopyNetworkDeviceSummaryCommand;
    public ICommand ForgetNetworkDeviceSnmpCredentialCommand => NetworkDevices.ForgetNetworkDeviceSnmpCredentialCommand;
    public ICommand SaveDeviceLabelCommand => NetworkDevices.SaveDeviceLabelCommand;
    public ICommand ClearDeviceLabelCommand => NetworkDevices.ClearDeviceLabelCommand;

    /// <summary>Editable label for the selected discovered device (persisted per MAC/IP).</summary>
    public string SelectedDeviceLabelText { get => NetworkDevices.SelectedDeviceLabelText; set => NetworkDevices.SelectedDeviceLabelText = value; }

    // === NetworkDevicesViewModel.IHost ===
    void NetworkDevicesViewModel.IHost.NotifyStatus(string message) => PublishStatus(ActivitySourceModule.NetworkDevices, message);
    void NetworkDevicesViewModel.IHost.NotifyBusy(bool busy) => SetBusyState(busy);

    void NetworkDevicesViewModel.IHost.AttachNetworkDevice(NetworkDeviceResult device)
    {
        // Network Devices is independent: device checks should not create a synthetic
        // Quick Diagnosis result. Existing real diagnoses are enriched for reports.
        if (LastDiagnosis is not null)
        {
            LastDiagnosis.LastNetworkDevice = device;
            NotifyDiagnosisSurfaceChanged();
            Reports?.NotifyDiagnosisChanged();
        }

        RefreshTechnicianHomeStatus();
    }

    void NetworkDevicesViewModel.IHost.AttachNetworkDeviceScan(NetworkDeviceScanResult scan)
    {
        // LAN scans follow the same rule: keep module state local unless a real diagnosis
        // already exists and can be enriched for export.
        if (LastDiagnosis is not null)
        {
            LastDiagnosis.LastNetworkDeviceScan = scan;
            NotifyDiagnosisSurfaceChanged();
            Reports?.NotifyDiagnosisChanged();
        }

        RefreshTechnicianHomeStatus();
    }

}
