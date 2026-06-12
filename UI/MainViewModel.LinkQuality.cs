using System.Windows.Input;
using NetScopeDiagnosticCenter.Core.Models;
using NetScopeDiagnosticCenter.UI.ViewModels;

namespace NetScopeDiagnosticCenter.UI;

/// <summary>
/// Phase 2d migration shim: real Link Quality logic lives in <see cref="LinkQualityViewModel"/>.
/// MainWindow.xaml binds to MainViewModel; every member here delegates to <c>LinkQuality.X</c>.
/// </summary>
public sealed partial class MainViewModel : LinkQualityViewModel.IHost
{
    /// <summary>Dedicated link quality VM. Constructed in MainViewModel.cs ctor.</summary>
    public LinkQualityViewModel LinkQuality { get; private set; } = null!;

    public string LinkQualityPingTarget { get => LinkQuality.LinkQualityPingTarget; set => LinkQuality.LinkQualityPingTarget = value; }
    public int LinkQualityPingSamples { get => LinkQuality.LinkQualityPingSamples; set => LinkQuality.LinkQualityPingSamples = value; }
    public bool IsLinkQualityRunning => LinkQuality.IsLinkQualityRunning;
    public string LinkQualityOperationStatus => LinkQuality.LinkQualityOperationStatus;
    public string LinkQualityRunStateText => LinkQuality.LinkQualityRunStateText;
    public string LinkQualityPingButtonText => LinkQuality.LinkQualityPingButtonText;
    public string LinkQualityPingButtonIcon => LinkQuality.LinkQualityPingButtonIcon;
    public string LinkQualityPathButtonText => LinkQuality.LinkQualityPathButtonText;
    public string LinkQualityPathButtonIcon => LinkQuality.LinkQualityPathButtonIcon;

    public bool HasLinkQualityPingResults => LinkQuality.HasLinkQualityPingResults;
    public bool HasNoLinkQualityPingResults => LinkQuality.HasNoLinkQualityPingResults;
    public bool HasLinkQualityDnsResults => LinkQuality.HasLinkQualityDnsResults;
    public bool HasNoLinkQualityDnsResults => LinkQuality.HasNoLinkQualityDnsResults;
    public bool HasLinkQualityEvidence => LinkQuality.HasLinkQualityEvidence;
    public bool HasNoLinkQualityEvidence => LinkQuality.HasNoLinkQualityEvidence;
    public bool HasLinkQualityRecommendations => LinkQuality.HasLinkQualityRecommendations;
    public bool HasNoLinkQualityRecommendations => LinkQuality.HasNoLinkQualityRecommendations;
    public bool HasLinkQualityLimitations => LinkQuality.HasLinkQualityLimitations;
    public bool HasNoLinkQualityLimitations => LinkQuality.HasNoLinkQualityLimitations;
    public string LinkQualityPingResultCountText => LinkQuality.LinkQualityPingResultCountText;
    public string LinkQualityDnsResultCountText => LinkQuality.LinkQualityDnsResultCountText;
    public string LinkQualityEvidenceCountText => LinkQuality.LinkQualityEvidenceCountText;
    public string LinkQualityRecommendationCountText => LinkQuality.LinkQualityRecommendationCountText;
    public string LinkQualityLimitationCountText => LinkQuality.LinkQualityLimitationCountText;
    public string LinkQualitySnapshotTimeText => LinkQuality.LinkQualitySnapshotTimeText;
    public IReadOnlyList<LinkQualityTargetOption> LinkQualityTargetOptions => LinkQuality.LinkQualityTargetOptions;
    public IReadOnlyList<LinkQualitySampleOption> LinkQualitySampleOptions => LinkQuality.LinkQualitySampleOptions;
    public bool CanCopyLinkQualitySummary => LinkQuality.CanCopyLinkQualitySummary;
    public string LinkQualityTicketSummaryText => LinkQuality.LinkQualityTicketSummaryText;
    public string LinkQualityPingEmptyText => LinkQuality.LinkQualityPingEmptyText;
    public string LinkQualityDnsEmptyText => LinkQuality.LinkQualityDnsEmptyText;

    public LinkQualityResult LastLinkQualityResult => LinkQuality.LastLinkQualityResult;

    // Manual traceroute (hop-by-hop path visibility on demand).
    public TraceRouteResult? LastTraceRoute => LinkQuality.LastTraceRoute;
    public bool HasTraceRoute => LinkQuality.HasTraceRoute;
    public bool NoTraceRoute => LinkQuality.NoTraceRoute;
    public string TraceRouteHopCountText => LinkQuality.TraceRouteHopCountText;
    public string TraceRouteButtonText => LinkQuality.TraceRouteButtonText;
    public string TraceRouteButtonIcon => LinkQuality.TraceRouteButtonIcon;

    public ICommand RefreshLinkQualityCommand => LinkQuality.RefreshLinkQualityCommand;
    public ICommand RunLinkQualityPingCommand => LinkQuality.RunLinkQualityPingCommand;
    public ICommand RunLinkQualityPathDiagnosticsCommand => LinkQuality.RunLinkQualityPathDiagnosticsCommand;
    public ICommand RunTraceRouteCommand => LinkQuality.RunTraceRouteCommand;
    public ICommand CancelLinkQualityCommand => LinkQuality.CancelLinkQualityCommand;
    public ICommand ApplyLinkQualityTargetPresetCommand => LinkQuality.ApplyLinkQualityTargetPresetCommand;
    public ICommand ApplyLinkQualitySamplePresetCommand => LinkQuality.ApplyLinkQualitySamplePresetCommand;
    public ICommand CopyLinkQualitySummaryCommand => LinkQuality.CopyLinkQualitySummaryCommand;

    /// <summary>Called by LastDiagnosis setter — re-builds the snapshot from the new diagnosis.</summary>
    private void RefreshLinkQualityFromLastDiagnosis() => LinkQuality?.RefreshFromLastDiagnosis();

    // === LinkQualityViewModel.IHost ===
    void LinkQualityViewModel.IHost.NotifyStatus(string message) => PublishStatus(ActivitySourceModule.LinkQuality, message);
    void LinkQualityViewModel.IHost.NotifyBusy(bool busy) => SetBusyState(busy);

    NetworkDiagnosisResult? LinkQualityViewModel.IHost.LastDiagnosis
    {
        get => LastDiagnosis;
        set => LastDiagnosis = value;
    }

    void LinkQualityViewModel.IHost.NotifyDiagnosisChanged()
    {
        NotifyDiagnosisSurfaceChanged();
        Reports?.NotifyDiagnosisChanged();
        RefreshTechnicianHomeStatus();
    }
}
