using NetScopeDiagnosticCenter.Core;
using NetScopeDiagnosticCenter.Core.Models;
using NetScopeDiagnosticCenter.Core.Models.Wifi;
using NetScopeDiagnosticCenter.UI.ViewModels;

namespace NetScopeDiagnosticCenter.UI;

/// <summary>
/// Technician Home host glue. The page binds <c>TechnicianHome.*</c> directly.
/// MainViewModel only supplies the read-only diagnosis state the home page projects.
/// </summary>
public sealed partial class MainViewModel : TechnicianHomeViewModel.IHost
{
    /// <summary>Technician Home VM. Constructed in MainViewModel.cs ctor.</summary>
    public TechnicianHomeViewModel TechnicianHome { get; private set; } = null!;

    /// <summary>Navigation status dot for the fused Diagnosis module.</summary>
    public string NavigationDiagnosisSeverity => SeverityOrUnknown(LastDiagnosis?.Verdict?.Severity);

    /// <summary>Navigation status dot for the Targeted Tests module.</summary>
    public string NavigationTargetedTestsSeverity => SeverityOrUnknown(LastTargetedScenario?.Severity);

    /// <summary>Navigation status dot for the Printers module.</summary>
    public string NavigationPrintersSeverity => SeverityOrUnknown(LastPrinterDiscovery?.Severity);

    /// <summary>Navigation status dot for the Network Devices module.</summary>
    public string NavigationNetworkDevicesSeverity => DiagnosticHelpers.WorstStatus(
        SeverityOrUnknown(LastNetworkDevice?.Severity),
        SeverityOrUnknown(LastNetworkDeviceScan?.Severity));

    /// <summary>Navigation status dot for the Wi-Fi Analyzer module.</summary>
    public string NavigationWifiSeverity => MapWifiSeverity(WifiAnalyzer?.Health);

    /// <summary>Navigation status dot for the Report Center module.</summary>
    public string NavigationReportsSeverity => Reports?.HasAnyReport == true ? "OK" : "Unknown";

    /// <summary>Re-fires the home page's diagnosis projections after upstream state changes.</summary>
    private void RefreshTechnicianHomeStatus()
    {
        TechnicianHome?.NotifyDiagnosisChanged();
        RefreshNavigationSeverityStatus();
    }

    private void RefreshNavigationSeverityStatus()
    {
        OnPropertyChanged(nameof(NavigationDiagnosisSeverity));
        OnPropertyChanged(nameof(NavigationTargetedTestsSeverity));
        OnPropertyChanged(nameof(NavigationPrintersSeverity));
        OnPropertyChanged(nameof(NavigationNetworkDevicesSeverity));
        OnPropertyChanged(nameof(NavigationWifiSeverity));
        OnPropertyChanged(nameof(NavigationReportsSeverity));
    }

    // === TechnicianHomeViewModel.IHost (read-only views of state owned by this VM) ===
    NetworkDiagnosisResult? TechnicianHomeViewModel.IHost.LastDiagnosis => LastDiagnosis;
    string TechnicianHomeViewModel.IHost.LastReportPath => LastReportPath;

    private static string SeverityOrUnknown(string? severity) =>
        string.IsNullOrWhiteSpace(severity) ? "Unknown" : severity;

    private static string MapWifiSeverity(WifiHealthReport? health)
    {
        if (health is null || !health.Score.HasValue)
        {
            return "Unknown";
        }

        return health.Verdict switch
        {
            "Excellent" or "Good" => "OK",
            "Fair" => "Warning",
            "Poor" => "Critical",
            "Not connected" => "Warning",
            _ => "Unknown"
        };
    }
}
