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
