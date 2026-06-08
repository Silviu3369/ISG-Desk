using System.Windows.Input;
using NetScopeDiagnosticCenter.Core.Models;
using NetScopeDiagnosticCenter.UI.ViewModels;

namespace NetScopeDiagnosticCenter.UI;

/// <summary>
/// Phase 2c migration shim: MainViewModel still exposes the Reports surface that
/// MainWindow.xaml binds to, but every member now delegates to <see cref="ReportsViewModel"/>.
/// All real logic lives in <see cref="ReportsViewModel"/>; this file is removed in Phase 2d
/// once XAML bindings are switched to <c>Reports.X</c> against the dedicated ContentControl.
/// </summary>
public sealed partial class MainViewModel : ReportsViewModel.IHost
{
    /// <summary>The dedicated reports VM. Constructed in MainViewModel.cs ctor.</summary>
    public ReportsViewModel Reports { get; private set; } = null!;

    public string LastReportPath
    {
        get => Reports.LastReportPath;
    }

    public string LastTextSummaryPath
    {
        get => Reports.LastTextSummaryPath;
    }

    public string LastRawJsonPath
    {
        get => Reports.LastRawJsonPath;
    }

    public string LastWlanReportPath
    {
        get => Reports.LastWlanReportPath;
    }

    public string SummaryText => Reports.SummaryText;

    // Commands delegated to ReportsViewModel.
    public ICommand GenerateReportCommand => Reports.GenerateReportCommand;
    public ICommand CopySummaryCommand => Reports.CopySummaryCommand;
    public ICommand GenerateWlanReportCommand => Reports.GenerateWlanReportCommand;

    /// <summary>Public for click-handler legacy use (MainWindow.xaml.cs).</summary>
    public void OpenPath(string path) => Reports.OpenPath(path);

    // === ReportsViewModel.IHost implementation ===
    // Note: explicit interface only for the read-side; the Notify* methods stay
    // implicit so subclasses/partials can override if needed.

    NetworkDiagnosisResult? ReportsViewModel.IHost.LastDiagnosis => LastDiagnosis;
    LinkQualityResult? ReportsViewModel.IHost.LastLinkQualityResult => LastLinkQualityResult;
    NetworkProfile ReportsViewModel.IHost.NetworkProfile => NetworkProfile;

    void ReportsViewModel.IHost.NotifyStatus(string message) => PublishStatus(ActivitySourceModule.Reports, message);
    void ReportsViewModel.IHost.NotifyBusy(bool busy) => SetBusyState(busy);

    void ReportsViewModel.IHost.NotifyReportPathsChanged()
    {
        // Forward to Technician Home refresh + propagate property-changed
        // notifications for the binding shim properties.
        OnPropertyChanged(nameof(LastReportPath));
        OnPropertyChanged(nameof(LastTextSummaryPath));
        OnPropertyChanged(nameof(LastRawJsonPath));
        OnPropertyChanged(nameof(LastWlanReportPath));
        RefreshTechnicianHomeStatus();
    }
}
