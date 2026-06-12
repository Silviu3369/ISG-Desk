using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using NetScopeDiagnosticCenter.Collectors;
using NetScopeDiagnosticCenter.Core.Models;
using NetScopeDiagnosticCenter.Infrastructure;

namespace NetScopeDiagnosticCenter.UI.ViewModels;

/// <summary>
/// Technician Home — the landing page. A READ-ONLY local system overview (the IT-support
/// equivalent of "Settings &gt; System &gt; About") plus the last-diagnosis summary and
/// quick navigation for the technician's starting surface.
///
/// <para>
/// Loads the local snapshot once on first navigation (and on manual Refresh). It performs
/// NO network activity — no ping/scan/SNMP/port/share/DC lookup — so it is safe to run on
/// page entry. Active troubleshooting stays on the Diagnosis page.
/// </para>
/// </summary>
public sealed class TechnicianHomeViewModel : ObservableObject
{
    public interface IHost
    {
        NetworkDiagnosisResult? LastDiagnosis { get; }
        string LastReportPath { get; }
    }

    private readonly IHost _host;
    private readonly ISystemOverviewCollector _collector;
    private readonly ActivityFeedService _activityFeed;

    private SystemOverview _overview = new();
    private bool _isLoading;
    private bool _loadedOnce;
    private string _lastRefreshText = "Not loaded yet";

    public TechnicianHomeViewModel(IHost host, ISystemOverviewCollector collector, ActivityFeedService activityFeed)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _collector = collector ?? throw new ArgumentNullException(nameof(collector));
        _activityFeed = activityFeed ?? throw new ArgumentNullException(nameof(activityFeed));

        RefreshCommand = new RelayCommand(_ => _ = LoadAsync(force: true), _ => !IsLoading);
        CopySystemSummaryCommand = new RelayCommand(_ => CopySystemSummary(), _ => !IsLoading);
        OpenDeviceManagerCommand = new RelayCommand(_ => OpenWindowsTool("devmgmt.msc", "Device Manager"));
        OpenNetworkConnectionsCommand = new RelayCommand(_ => OpenWindowsTool("ncpa.cpl", "Network Connections"));
        OpenEventViewerCommand = new RelayCommand(_ => OpenWindowsTool("eventvwr.msc", "Event Viewer"));
        OpenWindowsSecurityCommand = new RelayCommand(_ => OpenWindowsTool("windowsdefender:", "Windows Security"));
        OpenSystemAboutCommand = new RelayCommand(_ => OpenWindowsTool("ms-settings:about", "System About"));
        OpenDiskManagementCommand = new RelayCommand(_ => OpenWindowsTool("diskmgmt.msc", "Disk Management"));
        OpenTaskManagerCommand = new RelayCommand(_ => OpenWindowsTool("taskmgr.exe", "Task Manager"));
        RestartAsAdminCommand = new RelayCommand(_ => RestartAsAdministrator());
    }

    public SystemOverview Overview
    {
        get => _overview;
        private set
        {
            if (SetProperty(ref _overview, value))
            {
                OnPropertyChanged(nameof(Hardware));
                OnPropertyChanged(nameof(Device));
                OnPropertyChanged(nameof(User));
                OnPropertyChanged(nameof(Network));
                OnPropertyChanged(nameof(Organization));
                OnPropertyChanged(nameof(Security));
                OnPropertyChanged(nameof(CollectorNote));
                OnPropertyChanged(nameof(HasCollectorNote));
                OnPropertyChanged(nameof(RecentEvents));
                OnPropertyChanged(nameof(HasRecentEvents));
                OnPropertyChanged(nameof(NoRecentEvents));
                OnPropertyChanged(nameof(Disks));
                OnPropertyChanged(nameof(Volumes));
                OnPropertyChanged(nameof(HasDisks));
                OnPropertyChanged(nameof(NoDisks));
                OnPropertyChanged(nameof(HasVolumes));
                OnPropertyChanged(nameof(StorageSummary));
                OnPropertyChanged(nameof(StorageSeverity));
                OnPropertyChanged(nameof(PrivilegeSummary));
                OnPropertyChanged(nameof(PrivilegeSeverity));
                OnPropertyChanged(nameof(ShowRestartAsAdmin));
                OnPropertyChanged(nameof(NetworkSummary));
                OnPropertyChanged(nameof(NetworkPostureSeverity));
                OnPropertyChanged(nameof(SecuritySummary));
                OnPropertyChanged(nameof(SecurityPostureSeverity));
                // Security severity (traffic-light) — derived from the raw status strings.
                OnPropertyChanged(nameof(DefenderSeverity));
                OnPropertyChanged(nameof(FirewallSeverity));
                OnPropertyChanged(nameof(BitLockerSeverity));
                OnPropertyChanged(nameof(UacSeverity));
                OnPropertyChanged(nameof(SecureBootSeverity));
                OnPropertyChanged(nameof(TpmSeverity));
                OnPropertyChanged(nameof(PendingRebootSeverity));
                OnPropertyChanged(nameof(WindowsActivationSeverity));
            }
        }
    }

    // Flattened accessors so the XAML binds Device.X / Network.X etc. directly.
    public SystemOverviewHardware Hardware => _overview.Hardware;
    public SystemOverviewDevice Device => _overview.Device;
    public SystemOverviewUser User => _overview.User;
    public SystemOverviewNetwork Network => _overview.Network;
    public SystemOverviewOrg Organization => _overview.Organization;
    public SystemOverviewSecurity Security => _overview.Security;
    public string? CollectorNote => _overview.CollectorNote;
    public bool HasCollectorNote => !string.IsNullOrEmpty(_overview.CollectorNote);

    /// <summary>Latest Critical / Error events the heavy collector pulled from the Event Log.</summary>
    public IReadOnlyList<SystemOverviewEvent> RecentEvents => _overview.RecentEvents;

    public bool HasRecentEvents => _overview.RecentEvents.Count > 0;
    public bool NoRecentEvents => _overview.RecentEvents.Count == 0;

    /// <summary>Physical disks with S.M.A.R.T. health (heavy tier).</summary>
    public IReadOnlyList<SystemOverviewDisk> Disks => _overview.Disks;

    /// <summary>Fixed volumes with usage (fast tier — always available).</summary>
    public IReadOnlyList<SystemOverviewVolume> Volumes => _overview.Volumes;

    public bool HasDisks => _overview.Disks.Count > 0;
    public bool NoDisks => _overview.Disks.Count == 0;
    public bool HasVolumes => _overview.Volumes.Count > 0;

    /// <summary>Worst severity across all disks and volumes — drives the STORAGE status tile.</summary>
    public string StorageSeverity
    {
        get
        {
            if (!_loadedOnce) return "Unknown";
            var all = _overview.Disks.Select(d => d.Severity)
                .Concat(_overview.Volumes.Select(v => v.Severity))
                .ToArray();
            return all.Length == 0 ? "Unknown" : WorstSeverity(all);
        }
    }

    public string StorageSummary
    {
        get
        {
            if (!_loadedOnce) return "Unknown";
            if (_overview.Disks.Count == 0 && _overview.Volumes.Count == 0) return "No disk data yet";

            var parts = new List<string>(2);
            if (_overview.Disks.Count > 0)
            {
                var attention = _overview.Disks.Count(d => d.Severity is "Critical" or "Warning");
                parts.Add(attention == 0
                    ? $"{_overview.Disks.Count} disk(s) healthy"
                    : $"{attention} of {_overview.Disks.Count} disk(s) need attention");
            }

            var systemVolume = _overview.Volumes.FirstOrDefault(v => v.IsSystemDrive)
                               ?? _overview.Volumes.FirstOrDefault();
            if (systemVolume is not null)
            {
                parts.Add($"{systemVolume.DriveLetter} {systemVolume.FreePercent:0}% free");
            }

            return parts.Count == 0 ? "Unknown" : string.Join(" · ", parts);
        }
    }

    public string PrivilegeSummary => JoinKnown(User.AdminRights, User.Elevated);

    public string PrivilegeSeverity
    {
        get
        {
            if (!_loadedOnce) return "Unknown";
            if (!IsKnown(User.AdminRights) || !IsKnown(User.Elevated)) return "Unknown";
            if (User.Elevated.StartsWith("Yes", StringComparison.OrdinalIgnoreCase)) return "OK";
            if (User.AdminRights.Contains("Administrator", StringComparison.OrdinalIgnoreCase)) return "Warning";
            return "Critical";
        }
    }

    /// <summary>
    /// True once the snapshot shows the process is NOT elevated — surfaces the
    /// "Restart as administrator" action that unlocks BitLocker / TPM / S.M.A.R.T. counters.
    /// </summary>
    public bool ShowRestartAsAdmin =>
        _loadedOnce && User.Elevated.StartsWith("No", StringComparison.OrdinalIgnoreCase);

    public string NetworkSummary => JoinKnown(
        Network.ConnectionType,
        Network.Ipv4Address,
        PrefixIfKnown("GW ", Network.DefaultGateway));

    public string NetworkPostureSeverity
    {
        get
        {
            if (!_loadedOnce) return "Unknown";
            if (!IsKnown(Network.AdapterName) || !IsKnown(Network.Ipv4Address)) return "Critical";
            if (!IsKnown(Network.DefaultGateway) || !IsKnown(Network.DnsServers)) return "Warning";
            return "OK";
        }
    }

    public string SecurityPostureSeverity => WorstSeverity(
        DefenderSeverity,
        FirewallSeverity,
        BitLockerSeverity,
        UacSeverity,
        SecureBootSeverity,
        TpmSeverity,
        PendingRebootSeverity,
        WindowsActivationSeverity);

    public string SecuritySummary
    {
        get
        {
            var issueCount = SecurityIssueCount();
            return SecurityPostureSeverity switch
            {
                "OK" => "Tracked controls look healthy",
                "Critical" => $"{issueCount} security item(s) need action",
                "Warning" => $"{issueCount} security item(s) need review",
                _ => "Some security controls are unknown",
            };
        }
    }

    // ----- Traffic-light severity for each security row (drives StatusBrush colour) -----

    /// <summary>"OK" / "Warning" / "Critical" / "Unknown" — keyed by StatusBrush converter.</summary>
    public string DefenderSeverity
    {
        get
        {
            var s = Security.DefenderStatus;
            if (string.IsNullOrEmpty(s) || s == "Unknown") return "Unknown";
            if (s.Contains("AV OFF", StringComparison.OrdinalIgnoreCase)
                || s.Contains("real-time OFF", StringComparison.OrdinalIgnoreCase)) return "Critical";
            if (s.Contains("AV on", StringComparison.OrdinalIgnoreCase)
                && s.Contains("real-time on", StringComparison.OrdinalIgnoreCase)) return "OK";
            return "Warning";
        }
    }

    public string FirewallSeverity
    {
        get
        {
            var s = Security.FirewallStatus;
            if (string.IsNullOrEmpty(s) || s == "Unknown") return "Unknown";
            if (s.Contains("On (all profiles)", StringComparison.OrdinalIgnoreCase)) return "OK";
            if (s.Contains("OFF", StringComparison.Ordinal)) return "Warning";
            return "OK";
        }
    }

    public string BitLockerSeverity
    {
        get
        {
            var s = Security.BitLockerStatus;
            if (string.IsNullOrEmpty(s) || s.Contains("Requires admin", StringComparison.OrdinalIgnoreCase)) return "Unknown";
            if (s.Contains("FullyEncrypted", StringComparison.OrdinalIgnoreCase)
                && s.Contains("On", StringComparison.OrdinalIgnoreCase)) return "OK";
            if (s.Contains("Decrypted", StringComparison.OrdinalIgnoreCase)
                || s.Contains("Off", StringComparison.OrdinalIgnoreCase)) return "Critical";
            return "Warning";
        }
    }

    public string UacSeverity => Security.UacStatus switch
    {
        "Enabled" => "OK",
        "DISABLED" => "Critical",
        _ => "Unknown",
    };

    public string SecureBootSeverity
    {
        get
        {
            var s = Security.SecureBoot;
            if (string.IsNullOrEmpty(s) || s.StartsWith("Unknown", StringComparison.OrdinalIgnoreCase)) return "Unknown";
            return s.Equals("Enabled", StringComparison.OrdinalIgnoreCase) ? "OK" : "Warning";
        }
    }

    public string TpmSeverity
    {
        get
        {
            var s = Security.TpmStatus;
            if (string.IsNullOrEmpty(s) || s == "Unknown" || s.Contains("Requires admin", StringComparison.OrdinalIgnoreCase)) return "Unknown";
            if (s.Equals("Present, ready", StringComparison.OrdinalIgnoreCase)) return "OK";
            if (s.Equals("Not present", StringComparison.OrdinalIgnoreCase)) return "Critical";
            return "Warning";
        }
    }

    public string PendingRebootSeverity => Security.PendingReboot.StartsWith("Yes", StringComparison.OrdinalIgnoreCase)
        ? "Warning"
        : (Security.PendingReboot == "No" ? "OK" : "Unknown");

    public string WindowsActivationSeverity
    {
        get
        {
            var s = Security.WindowsActivation;
            if (string.IsNullOrEmpty(s) || s == "Unknown") return "Unknown";
            if (s.Equals("Activated", StringComparison.OrdinalIgnoreCase)) return "OK";
            if (s.Contains("Unlicensed", StringComparison.OrdinalIgnoreCase)
                || s.Contains("Notification", StringComparison.OrdinalIgnoreCase)) return "Critical";
            return "Warning";
        }
    }

    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (SetProperty(ref _isLoading, value))
            {
                (RefreshCommand as RelayCommand)?.RaiseCanExecuteChanged();
                (CopySystemSummaryCommand as RelayCommand)?.RaiseCanExecuteChanged();
                OnPropertyChanged(nameof(RefreshButtonText));
                OnPropertyChanged(nameof(RefreshButtonIcon));
            }
        }
    }

    public string RefreshButtonText => IsLoading ? "Refreshing..." : "Refresh";

    public string RefreshButtonIcon => IsLoading ? "\uE895" : "\uE72C";

    public string LastRefreshText
    {
        get => _lastRefreshText;
        private set => SetProperty(ref _lastRefreshText, value);
    }

    public ICommand RefreshCommand { get; }
    public ICommand CopySystemSummaryCommand { get; }
    public ICommand OpenDeviceManagerCommand { get; }
    public ICommand OpenNetworkConnectionsCommand { get; }
    public ICommand OpenEventViewerCommand { get; }
    public ICommand OpenWindowsSecurityCommand { get; }
    public ICommand OpenSystemAboutCommand { get; }
    public ICommand OpenDiskManagementCommand { get; }
    public ICommand OpenTaskManagerCommand { get; }
    public ICommand RestartAsAdminCommand { get; }

    // ---- Last-diagnosis projection (read-only, from the host). ----

    public bool HasDiagnosis => _host.LastDiagnosis is not null;

    public string DiagnosisStatus => _host.LastDiagnosis?.Verdict.Title ?? "Not run yet";

    public string DiagnosisSummary =>
        _host.LastDiagnosis is null
            ? "No baseline yet"
            : $"{DiagnosisScore?.ToString() ?? "No score"}/100 - {DiagnosisStatus}";

    public string DiagnosisDetail =>
        _host.LastDiagnosis is null
            ? "Run Quick Diagnosis for adapter, IP, gateway, DNS and internet checks."
            : $"Health {_host.LastDiagnosis.HealthScore.Score}/100 ({_host.LastDiagnosis.HealthScore.Status}) · "
              + $"layer {_host.LastDiagnosis.Verdict.AffectedLayer} · confidence {_host.LastDiagnosis.Verdict.Confidence}.";

    public string DiagnosisSeverity => _host.LastDiagnosis?.Verdict.Severity ?? "Unknown";

    public int? DiagnosisScore => _host.LastDiagnosis?.HealthScore.Score;

    public string DiagnosisScoreStatus => _host.LastDiagnosis?.HealthScore.Status ?? "No data";

    public string DiagnosisLastRun =>
        _host.LastDiagnosis is null
            ? "Never"
            : _host.LastDiagnosis.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss");

    public string DiagnosisAffectedLayer => _host.LastDiagnosis?.Verdict.AffectedLayer ?? "—";

    public string DiagnosisConfidence => _host.LastDiagnosis?.Verdict.Confidence ?? "—";

    public string RecommendedAction =>
        _host.LastDiagnosis is null
            ? "Run Quick Diagnosis to establish a baseline."
            : _host.LastDiagnosis.Verdict.Severity is "Critical" or "Warning"
                ? $"Investigate: {_host.LastDiagnosis.Verdict.Title}. Open Network Diagnosis for details."
                : "No action needed — last diagnosis was healthy.";

    public string ReportAvailable =>
        string.IsNullOrWhiteSpace(_host.LastReportPath) ? "No report exported yet" : "Report exported";

    /// <summary>Re-fires the diagnosis-derived projections. Called by the host after a new diagnosis.</summary>
    public void NotifyDiagnosisChanged()
    {
        OnPropertyChanged(nameof(HasDiagnosis));
        OnPropertyChanged(nameof(DiagnosisStatus));
        OnPropertyChanged(nameof(DiagnosisSummary));
        OnPropertyChanged(nameof(DiagnosisDetail));
        OnPropertyChanged(nameof(DiagnosisSeverity));
        OnPropertyChanged(nameof(DiagnosisScore));
        OnPropertyChanged(nameof(DiagnosisScoreStatus));
        OnPropertyChanged(nameof(DiagnosisLastRun));
        OnPropertyChanged(nameof(DiagnosisAffectedLayer));
        OnPropertyChanged(nameof(DiagnosisConfidence));
        OnPropertyChanged(nameof(RecommendedAction));
        OnPropertyChanged(nameof(ReportAvailable));
    }

    /// <summary>Load the local snapshot once (first page entry). Manual Refresh forces a re-read.</summary>
    public async Task EnsureLoadedAsync()
    {
        if (_loadedOnce || IsLoading) return;
        await LoadAsync(force: false).ConfigureAwait(true);
    }

    private async Task LoadAsync(bool force)
    {
        if (IsLoading) return;
        if (_loadedOnce && !force) return;

        IsLoading = true;
        LastRefreshText = "Reading local system information…";
        try
        {
            AddActivity("System overview refresh started", ActivityStatus.Running);
            // No ConfigureAwait(false): we set UI-bound properties right after.
            var snapshot = await _collector.GetAsync(CancellationToken.None);
            _loadedOnce = true;
            Overview = snapshot;
            LastRefreshText = $"Local snapshot · {snapshot.CapturedAt:yyyy-MM-dd HH:mm:ss}";
            AddActivity($"System overview refreshed ({snapshot.Device.DeviceName})", ActivityStatus.Success);
        }
        catch (Exception ex)
        {
            LastRefreshText = $"Could not read system info: {ex.Message}";
            AddActivity($"Overview refresh failed: {ex.Message}", ActivityStatus.Error);
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void AddActivity(string line, ActivityStatus status = ActivityStatus.Info)
    {
        _activityFeed.Add(
            ActivitySourceModule.TechnicianHome,
            status,
            "Technician Home",
            line);
    }

    /// <summary>
    /// Relaunches the app elevated (UAC prompt) and closes this instance. A declined
    /// prompt (Win32 error 1223) is reported as a warning, never as a crash.
    /// </summary>
    private void RestartAsAdministrator()
    {
        var exePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exePath))
        {
            AddActivity("Could not determine the application path for elevation", ActivityStatus.Error);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(exePath)
            {
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = AppContext.BaseDirectory
            });
            AddActivity("Restarting with administrator rights…", ActivityStatus.Info);
            Application.Current?.Shutdown();
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // ERROR_CANCELLED — the technician dismissed the UAC prompt.
            AddActivity("Restart as administrator cancelled at the UAC prompt", ActivityStatus.Warning);
        }
        catch (Exception ex)
        {
            AddActivity($"Restart as administrator failed: {ex.Message}", ActivityStatus.Error);
            LastRefreshText = $"Restart as administrator failed: {ex.Message}";
        }
    }

    private void OpenWindowsTool(string target, string displayName)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            AddActivity($"{displayName} opened", ActivityStatus.Success);
        }
        catch (Exception ex)
        {
            AddActivity($"Could not open {displayName}: {ex.Message}", ActivityStatus.Error);
            LastRefreshText = $"Could not open {displayName}: {ex.Message}";
        }
    }

    private int SecurityIssueCount()
    {
        return new[]
        {
            DefenderSeverity,
            FirewallSeverity,
            BitLockerSeverity,
            UacSeverity,
            SecureBootSeverity,
            TpmSeverity,
            PendingRebootSeverity,
            WindowsActivationSeverity
        }.Count(severity => severity is "Critical" or "Warning");
    }

    private static string WorstSeverity(params string[] severities)
    {
        if (severities.Any(s => s == "Critical")) return "Critical";
        if (severities.Any(s => s == "Warning")) return "Warning";
        if (severities.Any(s => s == "Unknown")) return "Unknown";
        return severities.Length == 0 ? "Unknown" : "OK";
    }

    private static string JoinKnown(params string?[] parts)
    {
        var known = parts
            .Select(part => part?.Trim())
            .Where(IsKnown)
            .ToArray();
        return known.Length == 0 ? "Unknown" : string.Join(" - ", known);
    }

    private static string PrefixIfKnown(string prefix, string? value) =>
        IsKnown(value) ? $"{prefix}{value!.Trim()}" : string.Empty;

    private static bool IsKnown(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var trimmed = value.Trim();
        return !trimmed.Equals("Unknown", StringComparison.OrdinalIgnoreCase)
            && trimmed != "-"
            && trimmed.Any(char.IsLetterOrDigit);
    }

    private void CopySystemSummary()
    {
        try
        {
            var hw = _overview.Hardware;
            var d = _overview.Device;
            var u = _overview.User;
            var n = _overview.Network;
            var g = _overview.Organization;
            var s = _overview.Security;
            var diskLines = _overview.Disks.Count == 0
                ? "  (no physical-disk details — refresh or run as administrator)"
                : string.Join(Environment.NewLine, _overview.Disks.Select(disk =>
                    $"  [{disk.Severity}] {disk.Model} — {disk.TypeLine} — {disk.HealthDisplay} ({disk.SmartDetail})"));
            var volumeLines = _overview.Volumes.Count == 0
                ? "  (no fixed volumes found)"
                : string.Join(Environment.NewLine, _overview.Volumes.Select(vol =>
                    $"  [{vol.Severity}] {vol.DriveLetter} {vol.Label} ({vol.FileSystem}) — {vol.UsageText}"));
            var text =
                $"""
                ISG Desk — System Summary ({_overview.CapturedAt:yyyy-MM-dd HH:mm:ss})

                DEVICE
                  Name         : {d.DeviceName}
                  Manufacturer : {d.Manufacturer}
                  Model        : {d.Model}
                  Serial       : {d.SerialNumber}
                  Windows      : {d.WindowsEdition} {d.WindowsVersion} (build {d.OsBuild}, {d.Architecture})
                  Uptime       : {d.Uptime}
                  Last boot    : {d.LastBootTime}

                HARDWARE
                  Chassis      : {hw.Chassis}
                  Processor    : {hw.Processor} ({hw.Cores})
                  Memory       : {hw.InstalledRam}  ·  {hw.MemoryUsage}
                  Graphics     : {hw.Graphics}
                  Storage      : {hw.Storage}
                  Free space   : {hw.StorageFree}
                  Battery      : {hw.Battery}
                  BIOS         : {hw.BiosVersion}

                STORAGE HEALTH (S.M.A.R.T.)
                {diskLines}
                {volumeLines}

                USER
                  User         : {u.CurrentUser}
                  Profile      : {u.ProfilePath}
                  Admin        : {u.AdminRights} · {u.Elevated}

                NETWORK
                  Adapter      : {n.AdapterDisplay}
                  IPv4         : {n.Ipv4Address} {n.SubnetPrefix}
                  Gateway      : {n.DefaultGateway}
                  DNS          : {n.DnsServers}
                  DHCP         : {n.DhcpEnabled} (server {n.DhcpServer})
                  MAC          : {n.MacAddress} · link {n.LinkSpeed}
                  Wi-Fi SSID   : {n.WifiSsid}

                ORGANIZATION
                  Workgroup/Domain : {g.WorkgroupOrDomain}
                  Domain joined    : {g.DomainJoined}
                  Entra joined     : {g.EntraJoined} · Hybrid {g.HybridJoined}
                  MDM              : {g.MdmEnrollment}
                  Logon server     : {g.LogonServer}

                SECURITY
                  Defender         : {s.DefenderStatus}
                  Firewall         : {s.FirewallStatus}
                  BitLocker        : {s.BitLockerStatus}
                  UAC              : {s.UacStatus}
                  Secure Boot      : {s.SecureBoot}
                  TPM              : {s.TpmStatus}
                  Pending reboot   : {s.PendingReboot}
                  Windows activation: {s.WindowsActivation}

                RECENT EVENTS ({_overview.RecentEvents.Count})
                {(_overview.RecentEvents.Count == 0 ? "  (no Critical / Error events in the last 48 h)" : string.Join(Environment.NewLine, _overview.RecentEvents.Select(e => $"  [{e.Level}] {e.Time}  {e.Log} · {e.Source} · Event {e.EventId}: {e.Message}")))}

                LAST DIAGNOSIS
                  Status       : {DiagnosisStatus}
                  Score        : {(DiagnosisScore?.ToString() ?? "—")}/100 ({DiagnosisScoreStatus})
                  Last run     : {DiagnosisLastRun}
                """;
            Clipboard.SetText(text);
            AddActivity("System summary copied to clipboard", ActivityStatus.Success);
        }
        catch (Exception ex)
        {
            AddActivity($"Copy failed: {ex.Message}", ActivityStatus.Error);
        }
    }
}
