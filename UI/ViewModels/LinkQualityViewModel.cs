using System.Windows.Input;
using NetScopeDiagnosticCenter.Core;
using NetScopeDiagnosticCenter.Core.Models;
using NetScopeDiagnosticCenter.Infrastructure;

namespace NetScopeDiagnosticCenter.UI.ViewModels;

/// <summary>
/// Link Quality module ViewModel: bounded ping tests and gateway/DNS/internet path
/// diagnostics layered on top of the latest Quick Diagnosis snapshot.
/// </summary>
/// <remarks>
/// Phase 2d migration. The product direction is to keep this fused with Diagnosis (Quick
/// Diagnosis + Deep Ping + Port Test) into a single page; the fusion is intentionally
/// out of scope for 2d to keep behavior identical to the legacy partial.
/// </remarks>
public sealed class LinkQualityViewModel : ObservableObject
{
    private static readonly IReadOnlyList<LinkQualitySampleOption> DefaultSampleOptions =
    [
        new(5, "5 samples", "Quick signal check."),
        new(10, "10 samples", "Balanced default."),
        new(20, "20 samples", "Better jitter confidence."),
        new(50, "50 samples", "Longer evidence window.")
    ];

    public interface IHost
    {
        void NotifyStatus(string message);
        void NotifyBusy(bool busy);

        NetworkDiagnosisResult? LastDiagnosis { get; set; }
        /// <summary>Re-fires LastDiagnosis-derived bindings after in-place mutation.</summary>
        void NotifyDiagnosisChanged();
    }

    private readonly LinkQualityAnalyzer _linkQualityAnalyzer;
    private readonly LinkQualityPingService _linkQualityPingService;
    private readonly LinkQualityDnsService _linkQualityDnsService;
    private readonly LinkQualityThresholds _linkQualityThresholds;
    private readonly ILoggingService _logger;
    private readonly IHost _host;

    private LinkQualityResult _lastLinkQualityResult;
    private CancellationTokenSource? _linkQualityCancellation;
    private string _linkQualityPingTarget = string.Empty;
    private string _linkQualityOperationStatus = "Idle. Run a ping test or path diagnostics manually.";
    private int _linkQualityPingSamples = 10;
    private bool _isLinkQualityRunning;

    public LinkQualityViewModel(ILoggingService logger, IHost host)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _host = host ?? throw new ArgumentNullException(nameof(host));

        _linkQualityAnalyzer = new LinkQualityAnalyzer();
        _linkQualityPingService = new LinkQualityPingService();
        _linkQualityDnsService = new LinkQualityDnsService();
        _linkQualityThresholds = LinkQualityThresholds.Default;
        _lastLinkQualityResult = _linkQualityAnalyzer.BuildFromQuickDiagnosis(null);

        RefreshLinkQualityCommand = new RelayCommand(_ => RefreshLinkQualitySnapshot(), _ => !IsLinkQualityRunning);
        RunLinkQualityPingCommand = new AsyncRelayCommand(_ => RunLinkQualityPingAsync(), _ => !IsLinkQualityRunning);
        RunLinkQualityPathDiagnosticsCommand = new AsyncRelayCommand(_ => RunLinkQualityPathDiagnosticsAsync(), _ => !IsLinkQualityRunning);
        CancelLinkQualityCommand = new RelayCommand(_ => CancelLinkQuality());
        ApplyLinkQualityTargetPresetCommand = new RelayCommand(ApplyLinkQualityTargetPreset, _ => !IsLinkQualityRunning);
        ApplyLinkQualitySamplePresetCommand = new RelayCommand(ApplyLinkQualitySamplePreset, _ => !IsLinkQualityRunning);
        CopyLinkQualitySummaryCommand = new RelayCommand(_ => CopyLinkQualitySummary(), _ => CanCopyLinkQualitySummary);
    }

    public ICommand RefreshLinkQualityCommand { get; }
    public ICommand RunLinkQualityPingCommand { get; }
    public ICommand RunLinkQualityPathDiagnosticsCommand { get; }
    public ICommand CancelLinkQualityCommand { get; }
    public ICommand ApplyLinkQualityTargetPresetCommand { get; }
    public ICommand ApplyLinkQualitySamplePresetCommand { get; }
    public ICommand CopyLinkQualitySummaryCommand { get; }

    public string LinkQualityPingTarget { get => _linkQualityPingTarget; set => SetProperty(ref _linkQualityPingTarget, value); }

    public int LinkQualityPingSamples
    {
        get => _linkQualityPingSamples;
        set => SetProperty(ref _linkQualityPingSamples, Math.Clamp(value, 1, 50));
    }

    public bool IsLinkQualityRunning
    {
        get => _isLinkQualityRunning;
        private set
        {
            if (SetProperty(ref _isLinkQualityRunning, value))
            {
                OnPropertyChanged(nameof(LinkQualityRunStateText));
                OnPropertyChanged(nameof(LinkQualityPingButtonText));
                OnPropertyChanged(nameof(LinkQualityPingButtonIcon));
                OnPropertyChanged(nameof(LinkQualityPathButtonText));
                OnPropertyChanged(nameof(LinkQualityPathButtonIcon));
                (RefreshLinkQualityCommand as RelayCommand)?.RaiseCanExecuteChanged();
                (RunLinkQualityPingCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                (RunLinkQualityPathDiagnosticsCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                (ApplyLinkQualityTargetPresetCommand as RelayCommand)?.RaiseCanExecuteChanged();
                (ApplyLinkQualitySamplePresetCommand as RelayCommand)?.RaiseCanExecuteChanged();
            }
        }
    }

    public string LinkQualityOperationStatus
    {
        get => _linkQualityOperationStatus;
        private set
        {
            if (SetProperty(ref _linkQualityOperationStatus, value))
            {
                OnPropertyChanged(nameof(LinkQualityRunStateText));
            }
        }
    }

    public string LinkQualityRunStateText =>
        IsLinkQualityRunning
            ? $"Running. {LinkQualityOperationStatus}"
            : LinkQualityOperationStatus;

    public string LinkQualityPingButtonText => IsLinkQualityRunning ? "Running..." : "Run Ping Test";
    public string LinkQualityPingButtonIcon => IsLinkQualityRunning ? "\uE895" : "\uE701";
    public string LinkQualityPathButtonText => IsLinkQualityRunning ? "Running..." : "Run Gateway / DNS / Internet";
    public string LinkQualityPathButtonIcon => IsLinkQualityRunning ? "\uE895" : "\uE968";

    public bool HasLinkQualityPingResults => LastLinkQualityResult.PingResults.Count > 0;
    public bool HasNoLinkQualityPingResults => !HasLinkQualityPingResults;
    public bool HasLinkQualityDnsResults => LastLinkQualityResult.DnsResults.Count > 0;
    public bool HasNoLinkQualityDnsResults => !HasLinkQualityDnsResults;
    public bool HasLinkQualityEvidence => LastLinkQualityResult.Evidence.Count > 0;
    public bool HasNoLinkQualityEvidence => !HasLinkQualityEvidence;
    public bool HasLinkQualityRecommendations => LastLinkQualityResult.Recommendations.Count > 0;
    public bool HasNoLinkQualityRecommendations => !HasLinkQualityRecommendations;
    public bool HasLinkQualityLimitations => LastLinkQualityResult.Limitations.Count > 0;
    public bool HasNoLinkQualityLimitations => !HasLinkQualityLimitations;

    public string LinkQualityPingResultCountText => FormatResultCount(LastLinkQualityResult.PingResults.Count, "ping result", "ping results");
    public string LinkQualityDnsResultCountText => FormatResultCount(LastLinkQualityResult.DnsResults.Count, "DNS result", "DNS results");
    public string LinkQualityEvidenceCountText => FormatResultCount(LastLinkQualityResult.Evidence.Count, "evidence item", "evidence items");
    public string LinkQualityRecommendationCountText => FormatResultCount(LastLinkQualityResult.Recommendations.Count, "recommendation", "recommendations");
    public string LinkQualityLimitationCountText => FormatResultCount(LastLinkQualityResult.Limitations.Count, "limitation", "limitations");
    public string LinkQualitySnapshotTimeText => $"Snapshot: {LastLinkQualityResult.CreatedAt:HH:mm:ss}";
    public IReadOnlyList<LinkQualityTargetOption> LinkQualityTargetOptions => BuildTargetOptions();
    public IReadOnlyList<LinkQualitySampleOption> LinkQualitySampleOptions => DefaultSampleOptions;
    public bool CanCopyLinkQualitySummary =>
        _host.LastDiagnosis is not null ||
        LastLinkQualityResult.PingResults.Count > 0 ||
        LastLinkQualityResult.DnsResults.Count > 0 ||
        LastLinkQualityResult.Evidence.Count > 0;
    public string LinkQualityTicketSummaryText => BuildLinkQualityTicketText(LastLinkQualityResult, DateTimeOffset.Now);

    public string LinkQualityPingEmptyText => _host.LastDiagnosis is null
        ? "No ping result yet. Enter an authorized gateway, host name or IP, then run a bounded ping test."
        : "No ping result yet. The target can default to the latest detected gateway, or you can enter an authorized host manually.";

    public string LinkQualityDnsEmptyText =>
        "No DNS lookup result yet. Run Gateway / DNS / Internet to test external DNS path quality.";

    public LinkQualityResult LastLinkQualityResult
    {
        get => _lastLinkQualityResult;
        private set
        {
            if (SetProperty(ref _lastLinkQualityResult, value))
            {
                AttachLinkQualityToDiagnosis(value);
                RefreshLinkQualityUiState();
            }
        }
    }

    /// <summary>
    /// Rebuilds the snapshot from the host's current LastDiagnosis. Called after Quick
    /// Diagnosis or targeted-test runs replace LastDiagnosis on the host.
    /// </summary>
    public void RefreshFromLastDiagnosis()
    {
        LastLinkQualityResult = _linkQualityAnalyzer.BuildFromQuickDiagnosis(_host.LastDiagnosis);
        ApplyDefaultLinkQualityTarget();
        OnPropertyChanged(nameof(LastLinkQualityResult));
        RefreshLinkQualityUiState();
    }

    public void RefreshLinkQualitySnapshot()
    {
        RefreshFromLastDiagnosis();
        LinkQualityOperationStatus = _host.LastDiagnosis is null
            ? "Idle. Run Quick Diagnosis first or enter a target for a standalone ping test."
            : "Idle. Snapshot refreshed from the latest Quick Diagnosis.";
        _host.NotifyStatus(_host.LastDiagnosis is null
            ? "Run Quick Diagnosis before refreshing Link Quality."
            : "Link Quality snapshot refreshed from the latest Quick Diagnosis.");
    }

    public async Task RunLinkQualityPingAsync()
    {
        var target = LinkQualityPingTarget.Trim();
        if (string.IsNullOrWhiteSpace(target))
        {
            ApplyDefaultLinkQualityTarget(force: true);
            target = LinkQualityPingTarget.Trim();
        }

        if (string.IsNullOrWhiteSpace(target))
        {
            _host.NotifyStatus("Enter a gateway, host name or IP before running Link Quality ping.");
            return;
        }

        CancelQuietly(_linkQualityCancellation);
        var linkQualityCancellation = new CancellationTokenSource();
        _linkQualityCancellation = linkQualityCancellation;
        var token = linkQualityCancellation.Token;
        IsLinkQualityRunning = true;
        _host.NotifyBusy(true);
        LinkQualityOperationStatus = $"Pinging {target} with {LinkQualityPingSamples} samples.";
        _host.NotifyStatus($"Running Link Quality ping to {target}...");
        _logger.Info($"Link Quality ping started. Target={target}; Samples={LinkQualityPingSamples}; Category={LinkQualityPingCategory.Target}.");

        try
        {
            var ping = await _linkQualityPingService.RunAsync(
                target,
                LinkQualityPingSamples,
                LinkQualityPingCategory.Target,
                _linkQualityThresholds,
                token);
            if (!ReferenceEquals(_linkQualityCancellation, linkQualityCancellation))
            {
                return;
            }

            var previousPingResults = LastLinkQualityResult.PingResults.ToList();
            var previousDnsResults = LastLinkQualityResult.DnsResults.ToList();
            var current = _linkQualityAnalyzer.BuildFromQuickDiagnosis(_host.LastDiagnosis);
            if (_host.LastDiagnosis is null)
            {
                current.Source = "Link Quality Ping Test";
                current.AffectedLayer = "Latency / Packet Loss";
            }

            current.PingResults.AddRange(previousPingResults);
            current.DnsResults.AddRange(previousDnsResults);
            current.PingResults.Add(ping);
            current.Evidence.Add($"Ping target {ping.Target}: {ping.Details}");
            current.Summary = ping.Status == "OK" && _host.LastDiagnosis is null
                ? $"Ping quality to {ping.Target} looks OK."
                : ping.Status == "OK"
                ? current.Summary
                : $"Ping quality issue detected for {ping.Target}.";
            current.Severity = WorstStatus(current.Severity, ping.Status);
            current.Confidence = ping.Status == "Critical" ? "High" : current.Confidence;
            current.AffectedLayer = ping.Status == "OK" ? current.AffectedLayer : "Latency / Packet Loss";
            if (ping.Status != "OK")
            {
                var recommendation = "Compare gateway ping with internet/application target ping to separate local link issues from upstream issues.";
                if (!current.Recommendations.Contains(recommendation, StringComparer.OrdinalIgnoreCase))
                {
                    current.Recommendations.Insert(0, recommendation);
                }
            }

            LastLinkQualityResult = current;
            LinkQualityOperationStatus = $"Last ping test: {ping.Status}; {ping.Details}";
            _logger.Info($"Link Quality ping completed. Target={ping.Target}; Status={ping.Status}; Sent={ping.Sent}; Received={ping.Received}; Loss={ping.LossText}; Avg={ping.AverageLatencyText}; Jitter={ping.JitterText}.");
            _host.NotifyStatus($"Link Quality ping completed: {ping.Status}; {ping.Details}");
        }
        catch (OperationCanceledException)
        {
            if (ReferenceEquals(_linkQualityCancellation, linkQualityCancellation))
            {
                LinkQualityOperationStatus = "Ping test cancelled by user.";
                _host.NotifyStatus("Link Quality ping cancelled.");
            }

            _logger.Info($"Link Quality ping cancelled. Target={target}; Samples={LinkQualityPingSamples}.");
        }
        catch (Exception ex)
        {
            _logger.Error("Link Quality ping failed.", ex);
            if (ReferenceEquals(_linkQualityCancellation, linkQualityCancellation))
            {
                LinkQualityOperationStatus = $"Ping test failed: {ex.Message}";
                _host.NotifyStatus($"Link Quality ping failed: {ex.Message}");
            }
        }
        finally
        {
            linkQualityCancellation.Dispose();
            if (ReferenceEquals(_linkQualityCancellation, linkQualityCancellation))
            {
                _linkQualityCancellation = null;
                IsLinkQualityRunning = false;
            }

            _host.NotifyBusy(false);
        }
    }

    public async Task RunLinkQualityPathDiagnosticsAsync()
    {
        CancelQuietly(_linkQualityCancellation);
        var linkQualityCancellation = new CancellationTokenSource();
        _linkQualityCancellation = linkQualityCancellation;
        var token = linkQualityCancellation.Token;
        IsLinkQualityRunning = true;
        _host.NotifyBusy(true);
        LinkQualityOperationStatus = $"Testing gateway, internet ping and DNS with {LinkQualityPingSamples} samples per ping target.";
        _host.NotifyStatus("Running Link Quality gateway, DNS and internet diagnostics...");
        _logger.Info($"Link Quality path diagnostics started. Samples={LinkQualityPingSamples}; Gateway={_host.LastDiagnosis?.IpConfiguration.Gateway ?? "Unknown"}; InternetTargets={string.Join(",", DiagnosticConstants.InternetPingTargets)}; DnsTargets={string.Join(",", DiagnosticConstants.DnsTestHostnames)}.");

        try
        {
            var previousPingResults = LastLinkQualityResult.PingResults.ToList();
            var previousDnsResults = LastLinkQualityResult.DnsResults.ToList();
            var current = _linkQualityAnalyzer.BuildFromQuickDiagnosis(_host.LastDiagnosis);
            var pathPingResults = new List<LinkQualityPingResult>();
            var pathDnsResults = new List<LinkQualityDnsResult>();
            current.Source = _host.LastDiagnosis is null
                ? "Link Quality Path Diagnostics"
                : "Latest Quick Diagnosis + Link Quality Path Diagnostics";
            current.PingResults.AddRange(previousPingResults);
            current.DnsResults.AddRange(previousDnsResults);

            var gateway = _host.LastDiagnosis?.IpConfiguration.Gateway;
            if (!string.IsNullOrWhiteSpace(gateway) &&
                !gateway.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
            {
                var gatewayPing = await _linkQualityPingService.RunAsync(
                    gateway,
                    LinkQualityPingSamples,
                    LinkQualityPingCategory.Gateway,
                    _linkQualityThresholds,
                    token);
                pathPingResults.Add(gatewayPing);
                current.PingResults.Add(gatewayPing);
            }
            else
            {
                current.Limitations.Add("Gateway ping was skipped because no gateway is available from the latest Quick Diagnosis.");
            }

            foreach (var pingTarget in DiagnosticConstants.InternetPingTargets)
            {
                var ping = await _linkQualityPingService.RunAsync(
                    pingTarget,
                    LinkQualityPingSamples,
                    LinkQualityPingCategory.Internet,
                    _linkQualityThresholds,
                    token);
                pathPingResults.Add(ping);
                current.PingResults.Add(ping);
            }

            foreach (var dnsHostname in DiagnosticConstants.DnsTestHostnames)
            {
                var lookup = await _linkQualityDnsService.ResolveAsync(dnsHostname, _linkQualityThresholds, token);
                pathDnsResults.Add(lookup);
                current.DnsResults.Add(lookup);
            }

            if (!ReferenceEquals(_linkQualityCancellation, linkQualityCancellation))
            {
                return;
            }

            ApplyPathDiagnosticsVerdict(current, pathPingResults, pathDnsResults);
            LastLinkQualityResult = current;
            LinkQualityOperationStatus = $"Last path diagnostics: {current.Severity}; {current.Summary}";
            _logger.Info($"Link Quality path diagnostics completed. Severity={current.Severity}; Summary={current.Summary}; PingResults={pathPingResults.Count}; DnsResults={pathDnsResults.Count}.");
            _host.NotifyStatus($"Link Quality path diagnostics completed: {current.Severity}.");
        }
        catch (OperationCanceledException)
        {
            if (ReferenceEquals(_linkQualityCancellation, linkQualityCancellation))
            {
                LinkQualityOperationStatus = "Path diagnostics cancelled by user.";
                _host.NotifyStatus("Link Quality path diagnostics cancelled.");
            }

            _logger.Info("Link Quality path diagnostics cancelled.");
        }
        catch (Exception ex)
        {
            _logger.Error("Link Quality path diagnostics failed.", ex);
            if (ReferenceEquals(_linkQualityCancellation, linkQualityCancellation))
            {
                LinkQualityOperationStatus = $"Path diagnostics failed: {ex.Message}";
                _host.NotifyStatus($"Link Quality path diagnostics failed: {ex.Message}");
            }
        }
        finally
        {
            linkQualityCancellation.Dispose();
            if (ReferenceEquals(_linkQualityCancellation, linkQualityCancellation))
            {
                _linkQualityCancellation = null;
                IsLinkQualityRunning = false;
            }

            _host.NotifyBusy(false);
        }
    }

    public void CancelLinkQuality()
    {
        CancelQuietly(_linkQualityCancellation);
        LinkQualityOperationStatus = "Stopping current Link Quality test...";
        _host.NotifyStatus("Stopping Link Quality test...");
    }

    private static void CancelQuietly(CancellationTokenSource? cancellation)
    {
        try { cancellation?.Cancel(); } catch (ObjectDisposedException) { }
    }

    private void RefreshLinkQualityUiState()
    {
        OnPropertyChanged(nameof(HasLinkQualityPingResults));
        OnPropertyChanged(nameof(HasNoLinkQualityPingResults));
        OnPropertyChanged(nameof(HasLinkQualityDnsResults));
        OnPropertyChanged(nameof(HasNoLinkQualityDnsResults));
        OnPropertyChanged(nameof(HasLinkQualityEvidence));
        OnPropertyChanged(nameof(HasNoLinkQualityEvidence));
        OnPropertyChanged(nameof(HasLinkQualityRecommendations));
        OnPropertyChanged(nameof(HasNoLinkQualityRecommendations));
        OnPropertyChanged(nameof(HasLinkQualityLimitations));
        OnPropertyChanged(nameof(HasNoLinkQualityLimitations));
        OnPropertyChanged(nameof(LinkQualityPingResultCountText));
        OnPropertyChanged(nameof(LinkQualityDnsResultCountText));
        OnPropertyChanged(nameof(LinkQualityEvidenceCountText));
        OnPropertyChanged(nameof(LinkQualityRecommendationCountText));
        OnPropertyChanged(nameof(LinkQualityLimitationCountText));
        OnPropertyChanged(nameof(LinkQualitySnapshotTimeText));
        OnPropertyChanged(nameof(LinkQualityTargetOptions));
        OnPropertyChanged(nameof(LinkQualitySampleOptions));
        OnPropertyChanged(nameof(CanCopyLinkQualitySummary));
        OnPropertyChanged(nameof(LinkQualityTicketSummaryText));
        OnPropertyChanged(nameof(LinkQualityPingEmptyText));
        OnPropertyChanged(nameof(LinkQualityDnsEmptyText));
        OnPropertyChanged(nameof(LinkQualityRunStateText));
        (CopyLinkQualitySummaryCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    private void AttachLinkQualityToDiagnosis(LinkQualityResult result)
    {
        if (_host.LastDiagnosis is null || !ReportsViewModel.IsReportableLinkQuality(result))
        {
            return;
        }

        _host.LastDiagnosis.LastLinkQuality = result;
        _host.NotifyDiagnosisChanged();
    }

    private void ApplyDefaultLinkQualityTarget(bool force = false)
    {
        if (!force && !string.IsNullOrWhiteSpace(LinkQualityPingTarget))
        {
            return;
        }

        var gateway = _host.LastDiagnosis?.IpConfiguration.Gateway;
        if (!string.IsNullOrWhiteSpace(gateway) &&
            !gateway.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
        {
            LinkQualityPingTarget = gateway;
        }
    }

    private void ApplyLinkQualityTargetPreset(object? parameter)
    {
        if (parameter is not LinkQualityTargetOption option)
        {
            return;
        }

        if (option.IsCustom)
        {
            LinkQualityPingTarget = string.Empty;
            LinkQualityOperationStatus = "Idle. Enter a custom authorized host or IP, then run a bounded ping test.";
            _host.NotifyStatus("Link Quality target cleared for custom input.");
            return;
        }

        if (string.IsNullOrWhiteSpace(option.Target))
        {
            return;
        }

        LinkQualityPingTarget = option.Target;
        LinkQualityOperationStatus = $"Idle. Target preset selected: {option.Label} ({option.Target}).";
        _host.NotifyStatus($"Link Quality target set to {option.Target}.");
    }

    private void ApplyLinkQualitySamplePreset(object? parameter)
    {
        var samples = parameter switch
        {
            LinkQualitySampleOption option => option.Samples,
            int value => value,
            string text when int.TryParse(text, out var value) => value,
            _ => 0
        };

        if (samples <= 0)
        {
            return;
        }

        LinkQualityPingSamples = samples;
        LinkQualityOperationStatus = $"Idle. Sample preset selected: {LinkQualityPingSamples} samples.";
        _host.NotifyStatus($"Link Quality samples set to {LinkQualityPingSamples}.");
    }

    private IReadOnlyList<LinkQualityTargetOption> BuildTargetOptions()
    {
        var options = new List<LinkQualityTargetOption>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var diagnosis = _host.LastDiagnosis;

        AddTargetOption(
            options,
            seen,
            "Gateway",
            diagnosis?.IpConfiguration.HasGateway == true ? diagnosis.IpConfiguration.Gateway : null,
            "Default gateway from the latest Quick Diagnosis.",
            "\uE968");

        AddTargetOption(
            options,
            seen,
            "DNS",
            diagnosis?.IpConfiguration.DnsServers.FirstOrDefault(),
            "First configured DNS server from the latest Quick Diagnosis.",
            "\uE774");

        AddTargetOption(
            options,
            seen,
            "Internet",
            DiagnosticConstants.InternetPingTargets.FirstOrDefault(),
            "Known internet ping target used by path diagnostics.",
            "\uE774");

        options.Add(new LinkQualityTargetOption(
            "Custom",
            string.Empty,
            "Clear the target field and enter an authorized host or IP manually.",
            "\uE8A7",
            IsCustom: true));

        return options;
    }

    private static void AddTargetOption(
        List<LinkQualityTargetOption> options,
        HashSet<string> seen,
        string label,
        string? target,
        string description,
        string icon)
    {
        if (string.IsNullOrWhiteSpace(target) ||
            target.Equals("Unknown", StringComparison.OrdinalIgnoreCase) ||
            !seen.Add(target))
        {
            return;
        }

        options.Add(new LinkQualityTargetOption(label, target.Trim(), description, icon));
    }

    private void CopyLinkQualitySummary()
    {
        if (!CanCopyLinkQualitySummary)
        {
            _host.NotifyStatus("Nothing to copy yet - run Quick Diagnosis or a Link Quality test first.");
            return;
        }

        try
        {
            System.Windows.Clipboard.SetText(LinkQualityTicketSummaryText);
            _host.NotifyStatus("Link Quality summary copied to clipboard.");
        }
        catch (Exception ex)
        {
            _logger.Error("Link Quality clipboard copy failed.", ex);
            _host.NotifyStatus($"Copy failed: {ex.Message}");
        }
    }

    private static string BuildLinkQualityTicketText(LinkQualityResult result, DateTimeOffset now)
    {
        var sb = new System.Text.StringBuilder(2048);
        sb.AppendLine($"ISG Desk - Link Quality Summary ({now:yyyy-MM-dd HH:mm:ss})");
        sb.AppendLine(new string('-', 60));
        sb.AppendLine($"Status        : {result.Severity}");
        sb.AppendLine($"Summary       : {result.Summary}");
        sb.AppendLine($"Source        : {result.Source}");
        sb.AppendLine($"Confidence    : {result.Confidence}");
        sb.AppendLine($"Affected layer: {result.AffectedLayer}");
        sb.AppendLine($"Adapter       : {result.AdapterName} ({result.ConnectionType})");
        sb.AppendLine($"Interface     : {result.InterfaceDescription}");
        sb.AppendLine($"Link speed    : {result.LinkSpeed}; duplex {result.SpeedDuplex}");
        sb.AppendLine($"Counters      : errors {result.Errors}; discards {result.Discards}; sample {result.SamplingStatus}");

        if (result.PingResults.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"PING RESULTS ({result.PingResults.Count})");
            foreach (var ping in result.PingResults)
            {
                sb.AppendLine($"  - {ping.Target} [{ping.Category}] {ping.Status}; sent {ping.Sent}; received {ping.Received}; loss {ping.LossText}; avg {ping.AverageLatencyText}; jitter {ping.JitterText}");
            }
        }

        if (result.DnsResults.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"DNS RESULTS ({result.DnsResults.Count})");
            foreach (var dns in result.DnsResults)
            {
                sb.AppendLine($"  - {dns.Name} {dns.Status}; latency {dns.LatencyText}; addresses {dns.AddressSummary}");
            }
        }

        AppendSection(sb, "EVIDENCE", result.Evidence);
        AppendSection(sb, "RECOMMENDATIONS", result.Recommendations);
        AppendSection(sb, "LIMITATIONS", result.Limitations);

        return sb.ToString();
    }

    private static void AppendSection(System.Text.StringBuilder sb, string title, IReadOnlyCollection<string> values)
    {
        if (values.Count == 0)
        {
            return;
        }

        sb.AppendLine();
        sb.AppendLine($"{title} ({values.Count})");
        foreach (var value in values)
        {
            sb.AppendLine($"  - {value}");
        }
    }

    private static string WorstStatus(string first, string second)
    {
        static int Rank(string value) => value switch
        {
            "Critical" => 3,
            "Warning" => 2,
            "OK" => 1,
            _ => 0
        };

        return Rank(second) > Rank(first) ? second : first;
    }

    private static void ApplyPathDiagnosticsVerdict(
        LinkQualityResult result,
        IReadOnlyCollection<LinkQualityPingResult> pathPingResults,
        IReadOnlyCollection<LinkQualityDnsResult> pathDnsResults)
    {
        var worst = pathPingResults
            .Select(item => item.Status)
            .Concat(pathDnsResults.Select(item => item.Status))
            .Aggregate(result.Severity, WorstStatus);

        result.Severity = worst;
        result.Confidence = worst == "Critical" ? "High" : worst == "Warning" ? "Medium" : result.Confidence;

        var gatewayPing = pathPingResults.FirstOrDefault(item =>
            item.Category.Equals(LinkQualityPingCategory.Gateway.ToString(), StringComparison.OrdinalIgnoreCase));
        var internetPings = pathPingResults
            .Where(item => item.Category.Equals(LinkQualityPingCategory.Internet.ToString(), StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (gatewayPing is not null && gatewayPing.Status != "OK")
        {
            result.Summary = $"Gateway path quality issue detected ({gatewayPing.Status}): loss {gatewayPing.LossText}, avg {gatewayPing.AverageLatencyText}, jitter {gatewayPing.JitterText}.";
            result.AffectedLayer = "Gateway / Local Network";
            AddUnique(result.Recommendations, "Check local link, gateway reachability, Wi-Fi/AP path, VLAN and switch port before focusing on internet.");
        }
        else if (internetPings.Any(item => item.Status == "Critical"))
        {
            result.Summary = "Critical internet path latency or packet loss detected.";
            result.AffectedLayer = "WAN / Firewall / Internet";
            AddUnique(result.Recommendations, "Compare gateway ping with internet ping. If gateway is clean but internet is not, check firewall/WAN/upstream path.");
        }
        else if (internetPings.Any(item => item.Status == "Warning"))
        {
            result.Summary = "Internet path latency, jitter or minor packet loss warning detected.";
            result.AffectedLayer = "WAN / Firewall / Internet";
            AddUnique(result.Recommendations, "If gateway latency is clean, compare with another internet target and check WAN/firewall utilization.");
        }
        else if (pathDnsResults.Any(item => item.Status == "Critical"))
        {
            result.Summary = "Critical DNS lookup failure or latency detected.";
            result.AffectedLayer = "DNS";
            AddUnique(result.Recommendations, "Check configured DNS servers, DNS filtering and resolver latency.");
        }
        else if (pathDnsResults.Any(item => item.Status == "Warning"))
        {
            result.Summary = "DNS lookup latency warning detected.";
            result.AffectedLayer = "DNS";
            AddUnique(result.Recommendations, "Check configured DNS servers, DNS filtering and resolver latency.");
        }
        else
        {
            result.Summary = "Gateway, internet ping and DNS lookup look usable in the latest path test.";
            AddUnique(result.Recommendations, "If users still report slowness, compare with an internal application or server target.");
        }

        foreach (var ping in pathPingResults)
        {
            result.Evidence.Add($"Path ping {ping.Target}: {ping.Details}");
        }

        foreach (var dns in pathDnsResults)
        {
            result.Evidence.Add($"DNS lookup {dns.Name}: {dns.Status}; {dns.LatencyText}; {dns.Details}");
        }
    }

    private static void AddUnique(List<string> values, string value)
    {
        if (!values.Contains(value, StringComparer.OrdinalIgnoreCase))
        {
            values.Add(value);
        }
    }

    private static string FormatResultCount(int count, string singular, string plural) =>
        count == 1 ? $"1 {singular}" : $"{count} {plural}";
}

public sealed record LinkQualityTargetOption(
    string Label,
    string Target,
    string Description,
    string Icon,
    bool IsCustom = false)
{
    public string TargetDisplayText => IsCustom ? "Manual" : Target;
}

public sealed record LinkQualitySampleOption(int Samples, string DisplayText, string Description);
