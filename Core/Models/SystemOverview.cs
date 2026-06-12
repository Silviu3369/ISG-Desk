namespace NetScopeDiagnosticCenter.Core.Models;

/// <summary>
/// Read-only snapshot of the LOCAL machine for the Technician Home page — the IT-support
/// equivalent of "Settings &gt; System &gt; About". Every field is a display string that
/// defaults to "Unknown" so the UI degrades gracefully when a value needs admin rights or
/// a WMI namespace that isn't reachable. Nothing here touches the network: it is pure
/// local identity / config / posture, gathered once on page load.
/// </summary>
public sealed class SystemOverview
{
    public SystemOverviewHardware Hardware { get; set; } = new();
    public SystemOverviewDevice Device { get; set; } = new();
    public SystemOverviewUser User { get; set; } = new();
    public SystemOverviewNetwork Network { get; set; } = new();
    public SystemOverviewOrg Organization { get; set; } = new();
    public SystemOverviewSecurity Security { get; set; } = new();

    /// <summary>When this snapshot was captured (drives the "refreshed at" label + activity log).</summary>
    public DateTimeOffset CapturedAt { get; set; } = DateTimeOffset.Now;

    /// <summary>Free-text note when the heavy (PowerShell) part failed/timed out — UI shows it subtly.</summary>
    public string? CollectorNote { get; set; }

    /// <summary>
    /// Latest Critical / Error events from the System and Application logs (last 48 h, top 8 by
    /// time). Populated by the heavy tier; empty when Event Log isn't reachable.
    /// </summary>
    public IReadOnlyList<SystemOverviewEvent> RecentEvents { get; set; } = Array.Empty<SystemOverviewEvent>();

    /// <summary>
    /// Physical disks with their S.M.A.R.T.-backed health. Populated by the heavy tier
    /// (MSFT_PhysicalDisk); empty when the Storage module isn't reachable.
    /// </summary>
    public IReadOnlyList<SystemOverviewDisk> Disks { get; set; } = Array.Empty<SystemOverviewDisk>();

    /// <summary>
    /// Fixed volumes with free-space usage. Populated by the fast tier (DriveInfo) so the
    /// bars render even when PowerShell is unavailable.
    /// </summary>
    public IReadOnlyList<SystemOverviewVolume> Volumes { get; set; } = Array.Empty<SystemOverviewVolume>();
}

public sealed class SystemOverviewHardware
{
    public string Manufacturer { get; set; } = "Unknown";
    public string Model { get; set; } = "Unknown";
    public string Chassis { get; set; } = "Unknown";
    public string Processor { get; set; } = "Unknown";
    public string Cores { get; set; } = "Unknown";
    public string InstalledRam { get; set; } = "Unknown";
    /// <summary>Live memory consumption — e.g. "10.4 / 16.0 GB used (65%)".</summary>
    public string MemoryUsage { get; set; } = "Unknown";
    public string Graphics { get; set; } = "Unknown";
    public string Storage { get; set; } = "Unknown";
    /// <summary>System-drive free space — e.g. "230 GB free of 477 GB (48%)".</summary>
    public string StorageFree { get; set; } = "Unknown";
    public string BiosVersion { get; set; } = "Unknown";
    /// <summary>Battery readout for laptops — e.g. "78% · Charging". "Not present" on desktops.</summary>
    public string Battery { get; set; } = "Not present";
}

public sealed class SystemOverviewDevice
{
    public string DeviceName { get; set; } = "Unknown";
    public string Manufacturer { get; set; } = "Unknown";
    public string Model { get; set; } = "Unknown";
    public string SerialNumber { get; set; } = "Unknown";
    public string WindowsEdition { get; set; } = "Unknown";
    public string WindowsVersion { get; set; } = "Unknown";
    public string OsBuild { get; set; } = "Unknown";
    public string Architecture { get; set; } = "Unknown";
    public string Uptime { get; set; } = "Unknown";
    /// <summary>Absolute timestamp of the last boot — separates the uptime number from "when".</summary>
    public string LastBootTime { get; set; } = "Unknown";
}

public sealed class SystemOverviewUser
{
    public string CurrentUser { get; set; } = "Unknown";
    public string ProfilePath { get; set; } = "Unknown";
    public string AdminRights { get; set; } = "Unknown";
    public string Elevated { get; set; } = "Unknown";
}

public sealed class SystemOverviewNetwork
{
    public string AdapterName { get; set; } = "Unknown";
    public string ConnectionType { get; set; } = "Unknown";

    /// <summary>
    /// "Ethernet 2 · Ethernet" — collapses to a single value when the adapter is literally
    /// named after its type (a Wi-Fi NIC named "Wi-Fi" would otherwise show "Wi-Fi · Wi-Fi").
    /// </summary>
    public string AdapterDisplay =>
        string.Equals(AdapterName.Trim(), ConnectionType.Trim(), StringComparison.OrdinalIgnoreCase)
            ? AdapterName.Trim()
            : $"{AdapterName} · {ConnectionType}";
    public string Ipv4Address { get; set; } = "Unknown";
    public string SubnetPrefix { get; set; } = "Unknown";
    public string DefaultGateway { get; set; } = "Unknown";
    public string DnsServers { get; set; } = "Unknown";
    public string DhcpEnabled { get; set; } = "Unknown";
    public string DhcpServer { get; set; } = "Unknown";
    public string MacAddress { get; set; } = "Unknown";
    public string LinkSpeed { get; set; } = "Unknown";
    public string WifiSsid { get; set; } = "—";
}

public sealed class SystemOverviewOrg
{
    public string WorkgroupOrDomain { get; set; } = "Unknown";
    public string DomainJoined { get; set; } = "Unknown";
    public string EntraJoined { get; set; } = "Unknown";
    public string HybridJoined { get; set; } = "Unknown";
    public string MdmEnrollment { get; set; } = "Unknown";
    public string LogonServer { get; set; } = "Unknown";
}

public sealed class SystemOverviewSecurity
{
    public string DefenderStatus { get; set; } = "Unknown";
    public string FirewallStatus { get; set; } = "Unknown";
    public string BitLockerStatus { get; set; } = "Unknown";
    public string UacStatus { get; set; } = "Unknown";
    public string PendingReboot { get; set; } = "Unknown";
    public string SecureBoot { get; set; } = "Unknown";
    public string TpmStatus { get; set; } = "Unknown";
    /// <summary>Windows activation status — e.g. "Activated", "Notification", "Unlicensed".</summary>
    public string WindowsActivation { get; set; } = "Unknown";
}

/// <summary>
/// A single Critical / Error event read from the local Event Log. Display strings only — the
/// collector pre-formats Time / Level / Message so the UI binds directly without converters.
/// </summary>
public sealed class SystemOverviewEvent
{
    public string Time { get; set; } = "";          // "yyyy-MM-dd HH:mm:ss"
    public string Log { get; set; } = "";           // "System" / "Application"
    public string Level { get; set; } = "Error";    // "Critical" / "Error"
    public string Source { get; set; } = "";        // ProviderName
    public int EventId { get; set; }
    public string Message { get; set; } = "";       // trimmed + single-line

    /// <summary>Compact header line shown above the message — "System · Source · Event 1234".</summary>
    public string HeaderText => $"{Log} · {Source} · Event {EventId}";

    /// <summary>Severity key for the StatusBrush converter — Critical → red, Error → amber.</summary>
    public string Severity => string.Equals(Level, "Critical", StringComparison.OrdinalIgnoreCase) ? "Critical" : "Warning";
}

/// <summary>
/// One physical disk with its S.M.A.R.T.-backed health readout. <see cref="HealthStatus"/> and
/// <see cref="FailurePredicted"/> come from MSFT_PhysicalDisk / Win32_DiskDrive and are readable
/// by standard users; Wear / Temperature / PowerOnHours come from MSFT_StorageReliabilityCounter,
/// which usually needs administrator rights — <see cref="SmartAvailable"/> records whether those
/// counters could be read so the UI can say "run as administrator" instead of showing blanks.
/// </summary>
public sealed class SystemOverviewDisk
{
    public string Model { get; set; } = "Unknown";
    public string MediaType { get; set; } = "Unknown";      // "SSD" / "HDD" / "SCM" / "Unknown"
    public string BusType { get; set; } = "Unknown";        // "NVMe" / "SATA" / "USB" / ...
    public string Size { get; set; } = "Unknown";           // "477 GB"
    public string SerialNumber { get; set; } = "Unknown";
    public string HealthStatus { get; set; } = "Unknown";   // "Healthy" / "Warning" / "Unhealthy" / "Unknown"

    /// <summary>S.M.A.R.T. failure prediction (OperationalStatus "Predictive Failure" or Win32_DiskDrive "Pred Fail").</summary>
    public bool FailurePredicted { get; set; }

    /// <summary>True when MSFT_StorageReliabilityCounter could be read (usually needs admin).</summary>
    public bool SmartAvailable { get; set; }

    /// <summary>SSD rated life used, 0–100 %. Null when the disk doesn't report it.</summary>
    public int? WearPercent { get; set; }
    public int? TemperatureC { get; set; }
    public long? PowerOnHours { get; set; }

    /// <summary>Rotational speed for HDDs (e.g. 5400 / 7200). Null for SSDs or when unknown.</summary>
    public int? SpindleRpm { get; set; }

    /// <summary>Traffic-light key for the StatusBrush converter.</summary>
    public string Severity
    {
        get
        {
            if (FailurePredicted) return "Critical";
            if (WearPercent is >= 95) return "Critical";
            if (string.Equals(HealthStatus, "Unhealthy", StringComparison.OrdinalIgnoreCase)) return "Critical";
            if (string.Equals(HealthStatus, "Warning", StringComparison.OrdinalIgnoreCase)) return "Warning";
            if (WearPercent is >= 80) return "Warning";
            return string.Equals(HealthStatus, "Healthy", StringComparison.OrdinalIgnoreCase) ? "OK" : "Unknown";
        }
    }

    /// <summary>Text for the health pill — failure prediction overrides the plain health status.</summary>
    public string HealthDisplay => FailurePredicted ? "Failure predicted" : HealthStatus;

    /// <summary>"SSD · NVMe · 477 GB · SN ABC123" — only the parts that are actually known.</summary>
    public string TypeLine
    {
        get
        {
            var parts = new List<string>(5);
            if (IsKnown(MediaType)) parts.Add(MediaType);
            if (IsKnown(BusType) && !string.Equals(BusType, MediaType, StringComparison.OrdinalIgnoreCase)) parts.Add(BusType);
            if (IsKnown(Size)) parts.Add(Size);
            if (SpindleRpm is int rpm) parts.Add($"{rpm} RPM");
            if (IsKnown(SerialNumber)) parts.Add($"SN {SerialNumber}");
            return parts.Count == 0 ? "Unknown" : string.Join(" · ", parts);
        }
    }

    /// <summary>"34 °C · wear 4% used · 1,234 h powered on" or the requires-admin hint.</summary>
    public string SmartDetail
    {
        get
        {
            if (!SmartAvailable)
                return "S.M.A.R.T. counters unavailable — run as administrator for temperature / wear.";
            var parts = new List<string>(3);
            if (TemperatureC is int t) parts.Add($"{t} °C");
            if (WearPercent is int w) parts.Add($"wear {w}% used");
            if (PowerOnHours is long h) parts.Add($"{h:N0} h powered on");
            return parts.Count == 0
                ? "S.M.A.R.T. counters: no data reported by this disk."
                : string.Join(" · ", parts);
        }
    }

    private static bool IsKnown(string? value) =>
        !string.IsNullOrWhiteSpace(value) && !value.Trim().Equals("Unknown", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// One fixed volume with raw byte counts so usage bars and free-space severity are computed
/// here (testable) instead of in the UI. Gathered by the fast tier via DriveInfo — no admin,
/// no PowerShell.
/// </summary>
public sealed class SystemOverviewVolume
{
    private const long CriticalFreeBytesFloor = 5L * 1024 * 1024 * 1024;   // < 5 GiB free is always Critical
    private const long WarningFreeBytesFloor = 15L * 1024 * 1024 * 1024;   // < 15 GiB free is at least Warning

    public string DriveLetter { get; set; } = "";    // "C:"
    public string Label { get; set; } = "Local Disk";
    public string FileSystem { get; set; } = "";     // "NTFS"
    public long TotalBytes { get; set; }
    public long FreeBytes { get; set; }
    public bool IsSystemDrive { get; set; }

    public double UsedPercent => TotalBytes <= 0 ? 0 : Math.Clamp(100.0 * (TotalBytes - FreeBytes) / TotalBytes, 0.0, 100.0);
    public double FreePercent => TotalBytes <= 0 ? 0 : 100.0 - UsedPercent;

    /// <summary>"228.9 GB free of 476.1 GB (48% free)".</summary>
    public string UsageText => TotalBytes <= 0
        ? "Unknown"
        : $"{FormatSize(FreeBytes)} free of {FormatSize(TotalBytes)} ({FreePercent:0}% free)";

    /// <summary>
    /// Traffic-light key — Critical below 5 % or 5 GiB free (Windows updates start failing),
    /// Warning below 12 % or 15 GiB free.
    /// </summary>
    public string Severity
    {
        get
        {
            if (TotalBytes <= 0) return "Unknown";
            if (FreePercent < 5 || FreeBytes < CriticalFreeBytesFloor) return "Critical";
            if (FreePercent < 12 || FreeBytes < WarningFreeBytesFloor) return "Warning";
            return "OK";
        }
    }

    private static string FormatSize(long bytes)
    {
        const double Gib = 1024d * 1024 * 1024;
        var gb = bytes / Gib;
        return gb >= 1024 ? $"{gb / 1024:0.##} TB" : $"{gb:0.#} GB";
    }
}
