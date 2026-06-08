using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Input;
using NetScopeDiagnosticCenter.Collectors;
using NetScopeDiagnosticCenter.Core.Models;
using NetScopeDiagnosticCenter.Infrastructure;
using NetScopeDiagnosticCenter.Reports;

namespace NetScopeDiagnosticCenter.UI.ViewModels;

/// <summary>
/// Reports module ViewModel: report generation (HTML/TXT/JSON), Windows WLAN report,
/// summary copy, and file open helpers.
/// </summary>
/// <remarks>
/// Decoupled from <see cref="MainViewModel"/> via the <see cref="IHost"/> callback
/// interface so the reports pipeline can be tested independently. During Phase 2c
/// MainViewModel constructs and owns this VM and implements <see cref="IHost"/>;
/// in Phase 2d the Shell will host this directly and the host interface can be
/// replaced by direct DI references to a future <c>DiagnosticState</c> service.
/// </remarks>
public sealed class ReportsViewModel : ObservableObject
{
    /// <summary>
    /// Read-only access to the diagnostic state owned by another part of the app
    /// (for now: MainViewModel; later: a shared DiagnosticState service).
    /// </summary>
    public interface IHost
    {
        NetworkDiagnosisResult? LastDiagnosis { get; }
        LinkQualityResult? LastLinkQualityResult { get; }
        NetworkProfile NetworkProfile { get; }
        void NotifyStatus(string message);
        void NotifyBusy(bool busy);
        void NotifyReportPathsChanged();
    }

    private readonly IHost _host;
    private readonly ReportStorageService _reportStorage;
    private readonly WifiCollector _wifiCollector;
    private readonly ILoggingService _logger;

    private CancellationTokenSource? _wlanReportCancellation;
    private bool _isWlanReportGenerating;
    private string _lastReportPath = string.Empty;
    private string _lastTextSummaryPath = string.Empty;
    private string _lastRawJsonPath = string.Empty;
    private string _lastWlanReportPath = string.Empty;
    private string _lastExportSummary = string.Empty;

    public ReportsViewModel(
        IHost host,
        ReportStorageService reportStorage,
        WifiCollector wifiCollector,
        ILoggingService logger)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _reportStorage = reportStorage ?? throw new ArgumentNullException(nameof(reportStorage));
        _wifiCollector = wifiCollector ?? throw new ArgumentNullException(nameof(wifiCollector));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        // Export + copy only make sense once a diagnosis exists — gate them so the buttons
        // visibly disable instead of firing a "run a diagnosis first" error toast.
        GenerateReportCommand = new AsyncRelayCommand(_ => GenerateReportAsync(), _ => HasDiagnosis);
        GenerateWlanReportCommand = new AsyncRelayCommand(_ => GenerateWlanReportAsync(), _ => !IsWlanReportGenerating);
        CopySummaryCommand = new RelayCommand(_ => CopySummary(), _ => HasDiagnosis);
        OpenReportsFolderCommand = new RelayCommand(_ => OpenReportsFolder());
    }

    public string LastReportPath
    {
        get => _lastReportPath;
        private set
        {
            if (SetProperty(ref _lastReportPath, value))
            {
                OnPropertyChanged(nameof(HasHtmlReport));
                OnPropertyChanged(nameof(HasAnyReport));
                OnPropertyChanged(nameof(HtmlReportPathText));
                _host.NotifyReportPathsChanged();
            }
        }
    }

    public string LastTextSummaryPath
    {
        get => _lastTextSummaryPath;
        private set
        {
            if (SetProperty(ref _lastTextSummaryPath, value))
            {
                OnPropertyChanged(nameof(HasTextReport));
                OnPropertyChanged(nameof(TextReportPathText));
                _host.NotifyReportPathsChanged();
            }
        }
    }

    public string LastRawJsonPath
    {
        get => _lastRawJsonPath;
        private set
        {
            if (SetProperty(ref _lastRawJsonPath, value))
            {
                OnPropertyChanged(nameof(HasJsonReport));
                OnPropertyChanged(nameof(JsonReportPathText));
                _host.NotifyReportPathsChanged();
            }
        }
    }

    public string LastWlanReportPath
    {
        get => _lastWlanReportPath;
        private set
        {
            if (SetProperty(ref _lastWlanReportPath, value))
            {
                OnPropertyChanged(nameof(HasWlanReport));
                OnPropertyChanged(nameof(WlanReportPathText));
                _host.NotifyReportPathsChanged();
            }
        }
    }

    /// <summary>One-line "Last export: …" readout shown on the Reports page after an export.</summary>
    public string LastExportSummary
    {
        get => _lastExportSummary;
        private set => SetProperty(ref _lastExportSummary, value);
    }

    /// <summary>Plain-text ticket-friendly summary computed from the latest diagnosis.</summary>
    public string SummaryText => BuildSummary(_host.LastDiagnosis);

    // ----- Diagnosis-state surface (drives the verdict card vs. empty state) -----

    /// <summary>True once a Quick Diagnosis result exists — a report can be generated.</summary>
    public bool HasDiagnosis => _host.LastDiagnosis is not null;

    /// <summary>Verdict title from the latest diagnosis, or an empty-state caption.</summary>
    public string VerdictTitle => _host.LastDiagnosis?.Verdict.Title ?? "No diagnosis run yet";

    /// <summary>Verdict severity ("OK" / "Warning" / "Critical" / "Unknown") — keyed by StatusBrush.</summary>
    public string VerdictSeverity => _host.LastDiagnosis?.Verdict.Severity ?? "Unknown";

    public string VerdictConfidence => _host.LastDiagnosis?.Verdict.Confidence ?? "—";
    public string VerdictLayer => _host.LastDiagnosis?.Verdict.AffectedLayer ?? "—";

    /// <summary>Health score as "82/100", or an em dash when no diagnosis has run.</summary>
    public string HealthScoreText => _host.LastDiagnosis is { } d ? $"{d.HealthScore.Score}/100" : "—";

    public string HealthStatus => _host.LastDiagnosis?.HealthScore.Status ?? "No data";

    /// <summary>When the underlying diagnosis ran, or a call-to-action when none has.</summary>
    public string DiagnosisRanAtText => _host.LastDiagnosis is { } d
        ? $"Diagnosis ran {d.CreatedAt:yyyy-MM-dd HH:mm:ss}"
        : "Run Quick Diagnosis first — the report is built from its results.";

    // ----- Generated-file presence (drives per-file Open buttons) -----

    public bool HasHtmlReport => !string.IsNullOrEmpty(_lastReportPath);
    public bool HasTextReport => !string.IsNullOrEmpty(_lastTextSummaryPath);
    public bool HasJsonReport => !string.IsNullOrEmpty(_lastRawJsonPath);
    public bool HasWlanReport => !string.IsNullOrEmpty(_lastWlanReportPath);

    /// <summary>True once at least one report set has been exported this session.</summary>
    public bool HasAnyReport => HasHtmlReport;

    /// <summary>Display text for each generated file row — the path, or "Not generated yet".</summary>
    public string HtmlReportPathText => HasHtmlReport ? _lastReportPath : "Not generated yet";
    public string TextReportPathText => HasTextReport ? _lastTextSummaryPath : "Not generated yet";
    public string JsonReportPathText => HasJsonReport ? _lastRawJsonPath : "Not generated yet";
    public string WlanReportPathText => HasWlanReport ? _lastWlanReportPath : "Not generated yet";

    public bool IsWlanReportGenerating
    {
        get => _isWlanReportGenerating;
        private set
        {
            if (SetProperty(ref _isWlanReportGenerating, value))
            {
                (GenerateWlanReportCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            }
        }
    }

    public ICommand GenerateReportCommand { get; }
    public ICommand GenerateWlanReportCommand { get; }
    public ICommand CopySummaryCommand { get; }
    public ICommand OpenReportsFolderCommand { get; }

    /// <summary>
    /// Notifies bindings that the underlying diagnosis changed (call from host). Refreshes the
    /// summary, the verdict card surface, and the enabled state of the export/copy commands.
    /// </summary>
    public void NotifyDiagnosisChanged()
    {
        OnPropertyChanged(nameof(SummaryText));
        OnPropertyChanged(nameof(HasDiagnosis));
        OnPropertyChanged(nameof(VerdictTitle));
        OnPropertyChanged(nameof(VerdictSeverity));
        OnPropertyChanged(nameof(VerdictConfidence));
        OnPropertyChanged(nameof(VerdictLayer));
        OnPropertyChanged(nameof(HealthScoreText));
        OnPropertyChanged(nameof(HealthStatus));
        OnPropertyChanged(nameof(DiagnosisRanAtText));
        (GenerateReportCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (CopySummaryCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    public async Task GenerateReportAsync()
    {
        var diagnosis = _host.LastDiagnosis;
        if (diagnosis is null)
        {
            _host.NotifyStatus("Run a diagnosis before generating a report.");
            return;
        }

        _host.NotifyBusy(true);
        _host.NotifyStatus("Generating report...");
        try
        {
            diagnosis.Profile = _host.NetworkProfile;
            var lastLink = _host.LastLinkQualityResult;
            if (lastLink is not null && IsReportableLinkQuality(lastLink))
            {
                diagnosis.LastLinkQuality = lastLink;
            }

            var result = await Task.Run(() => _reportStorage.SaveReportSet(diagnosis));
            LastReportPath = result.HtmlPath;
            LastTextSummaryPath = result.TextPath;
            LastRawJsonPath = result.JsonPath;
            LastExportSummary = $"Last export {DateTime.Now:yyyy-MM-dd HH:mm:ss} — HTML, TXT and JSON written to the Reports folder.";
            _host.NotifyStatus($"Report saved: {result.HtmlPath}");
        }
        catch (Exception ex)
        {
            _logger.Error("Report generation failed.", ex);
            _host.NotifyStatus($"Report generation failed: {ex.Message}");
        }
        finally
        {
            _host.NotifyBusy(false);
        }
    }

    public async Task GenerateWlanReportAsync()
    {
        if (IsWlanReportGenerating)
        {
            return;
        }

        CancelQuietly(_wlanReportCancellation);
        var wlanReportCancellation = new CancellationTokenSource();
        _wlanReportCancellation = wlanReportCancellation;
        IsWlanReportGenerating = true;

        _host.NotifyBusy(true);
        _host.NotifyStatus("Generating Windows WLAN report...");
        try
        {
            var path = await _wifiCollector.GenerateWlanReportAsync(wlanReportCancellation.Token);
            if (string.IsNullOrWhiteSpace(path))
            {
                _host.NotifyStatus("WLAN report was not generated. Ensure Wi-Fi adapter is present and try running as administrator.");
                return;
            }

            LastWlanReportPath = path;
            _host.NotifyStatus($"WLAN report saved: {path}");
        }
        catch (OperationCanceledException)
        {
            _host.NotifyStatus("WLAN report generation cancelled.");
        }
        catch (Exception ex)
        {
            _logger.Error("WLAN report generation failed.", ex);
            _host.NotifyStatus($"WLAN report generation failed: {ex.Message}");
        }
        finally
        {
            wlanReportCancellation.Dispose();
            if (ReferenceEquals(_wlanReportCancellation, wlanReportCancellation))
            {
                _wlanReportCancellation = null;
                IsWlanReportGenerating = false;
            }
            _host.NotifyBusy(false);
        }
    }

    public void CancelActiveReportOperation()
    {
        CancelQuietly(_wlanReportCancellation);
    }

    public void CopySummary()
    {
        var text = SummaryText;
        if (string.IsNullOrWhiteSpace(text))
        {
            _host.NotifyStatus("Nothing to copy — run a diagnosis first.");
            return;
        }

        try
        {
            Clipboard.SetText(text);
            _host.NotifyStatus("Summary copied to clipboard.");
        }
        catch (Exception ex)
        {
            _logger.Error("Clipboard copy failed.", ex);
            _host.NotifyStatus($"Copy failed: {ex.Message}");
        }
    }

    public void OpenPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            _host.NotifyStatus("No file path available to open.");
            return;
        }

        try
        {
            var fullPath = Path.GetFullPath(path);
            if (!File.Exists(fullPath))
            {
                _host.NotifyStatus($"File does not exist: {fullPath}");
                return;
            }

            if (!IsOpenableReportFile(fullPath))
            {
                _host.NotifyStatus("Unsupported file type. Only HTML, text and JSON reports can be opened from ISG Desk.");
                return;
            }

            if (!IsKnownReportPath(fullPath))
            {
                _host.NotifyStatus("For safety, ISG Desk only opens generated reports from its Reports folder or the last WLAN report.");
                return;
            }

            Process.Start(new ProcessStartInfo(fullPath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _logger.Error("Failed to open file.", ex);
            _host.NotifyStatus($"Failed to open file: {ex.Message}");
        }
    }

    /// <summary>
    /// Opens the folder where report sets are written (created on demand). Lets a tech browse
    /// every report exported across sessions, not just the most recent one.
    /// </summary>
    public void OpenReportsFolder()
    {
        try
        {
            var folder = _reportStorage.ReportFolder;
            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
            _host.NotifyStatus($"Reports folder: {folder}");
        }
        catch (Exception ex)
        {
            _logger.Error("Failed to open reports folder.", ex);
            _host.NotifyStatus($"Failed to open reports folder: {ex.Message}");
        }
    }

    /// <summary>
    /// Determines whether a Link Quality result is rich enough to attach to a report.
    /// </summary>
    public static bool IsReportableLinkQuality(LinkQualityResult result) =>
        result.PingResults.Count > 0 ||
        result.DnsResults.Count > 0 ||
        result.Evidence.Count > 0;

    private static bool IsOpenableReportFile(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".html", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".htm", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".txt", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".json", StringComparison.OrdinalIgnoreCase);
    }

    private bool IsKnownReportPath(string fullPath) =>
        IsSamePath(fullPath, LastReportPath) ||
        IsSamePath(fullPath, LastTextSummaryPath) ||
        IsSamePath(fullPath, LastRawJsonPath) ||
        IsSamePath(fullPath, LastWlanReportPath) ||
        IsUnderDirectory(fullPath, _reportStorage.ReportFolder);

    private static bool IsSamePath(string fullPath, string candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        try
        {
            return fullPath.Equals(Path.GetFullPath(candidate), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsUnderDirectory(string fullPath, string directory)
    {
        var fullDirectory = Path.GetFullPath(directory);
        if (!fullDirectory.EndsWith(Path.DirectorySeparatorChar))
        {
            fullDirectory += Path.DirectorySeparatorChar;
        }

        return fullPath.StartsWith(fullDirectory, StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildSummary(NetworkDiagnosisResult? result)
    {
        if (result is null)
            return "No diagnosis has been run yet. Click Quick Diagnosis to start.";

        var sb = new StringBuilder();
        sb.AppendLine($"Computer: {result.ComputerName}  |  User: {result.UserName}  |  {result.CreatedAt:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine();

        if (result.Verdict is not null)
        {
            sb.AppendLine($"Issue:      {result.Verdict.Title}");
            sb.AppendLine($"Severity:   {result.Verdict.Severity}");
            sb.AppendLine($"Confidence: {result.Verdict.Confidence}");
            sb.AppendLine($"Layer:      {result.Verdict.AffectedLayer}");
        }

        if (result.HealthScore is not null)
            sb.AppendLine($"Health:     {result.HealthScore.Score}/100  ({result.HealthScore.Status})");

        if (result.Verdict?.Evidence.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Evidence:");
            foreach (var line in result.Verdict.Evidence)
                sb.AppendLine($"  - {line}");
        }

        if (result.Verdict?.Recommendations.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Recommended actions:");
            foreach (var line in result.Verdict.Recommendations)
                sb.AppendLine($"  - {line}");
        }

        if (result.LastScenario is not null)
        {
            sb.AppendLine();
            sb.AppendLine($"Targeted test:  {result.LastScenario.WorkflowName}");
            sb.AppendLine($"Result:    {result.LastScenario.Title}");
            sb.AppendLine($"Layer:     {result.LastScenario.AffectedLayer}");
            sb.AppendLine($"Owner:     {result.LastScenario.OwnerSuggestion}");
        }

        if (result.LastLinkQuality is not null)
        {
            sb.AppendLine();
            sb.AppendLine($"Link quality:  {result.LastLinkQuality.Summary}");
            sb.AppendLine($"  Severity: {result.LastLinkQuality.Severity}; Layer: {result.LastLinkQuality.AffectedLayer}");
            if (result.LastLinkQuality.PingResults.Count > 0)
                sb.AppendLine($"  Ping tests: {result.LastLinkQuality.PingResults.Count}");
            if (result.LastLinkQuality.DnsResults.Count > 0)
                sb.AppendLine($"  DNS tests: {result.LastLinkQuality.DnsResults.Count}");
        }

        if (result.LastPrinterDiscovery is not null)
        {
            sb.AppendLine();
            sb.AppendLine($"Printers:  {result.LastPrinterDiscovery.Verdict}");
        }

        if (result.LastNetworkDevice is not null)
        {
            sb.AppendLine();
            sb.AppendLine($"Network device:  {result.LastNetworkDevice.Verdict}");
            if (result.LastNetworkDevice.PortsNeedingAttention.Count > 0)
                sb.AppendLine($"  Ports needing attention: {result.LastNetworkDevice.PortsNeedingAttention.Count}");
            if (result.LastNetworkDevice.Identity is not null)
                sb.AppendLine($"  Name: {FirstNonEmpty(result.LastNetworkDevice.Identity.SysName, result.LastNetworkDevice.Address)}");
        }

        if (result.LastNetworkDeviceScan is not null)
        {
            sb.AppendLine();
            sb.AppendLine($"LAN scan:  {result.LastNetworkDeviceScan.Verdict}");
        }

        return sb.ToString();
    }

    private static string FirstNonEmpty(params string[] values) =>
        Core.DiagnosticHelpers.FirstNonEmpty(values);

    private static void CancelQuietly(CancellationTokenSource? cancellation)
    {
        try { cancellation?.Cancel(); } catch (ObjectDisposedException) { }
    }
}
