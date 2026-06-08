using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32;
using NetScopeDiagnosticCenter.Core.Models;
using NetScopeDiagnosticCenter.Infrastructure;
using NetScopeDiagnosticCenter.Infrastructure.Wlan;

namespace NetScopeDiagnosticCenter.Collectors;

/// <summary>
/// Gathers a READ-ONLY local system snapshot for the Technician Home page. Two tiers:
///
/// <list type="bullet">
///   <item><b>Fast tier (pure BCL, synchronous, &lt;5 ms):</b> identity, OS version from
///   the registry, architecture, uptime, and the active adapter's IP config from
///   <see cref="NetworkInterface"/>. No process spawn, never throws (all guarded).</item>
///   <item><b>Heavy tier (one static PowerShell script, time-boxed):</b> CIM
///   manufacturer/model/serial, domain/Entra/hybrid join via <c>dsregcmd</c>, Defender /
///   firewall / BitLocker / UAC / Secure Boot / TPM / pending-reboot posture. Every value
///   is individually guarded in-script and falls back to "Unknown" / "Requires admin".</item>
/// </list>
///
/// <para>
/// Strictly local: NO ping, scan, SNMP, port test, share or DC lookup — safe to run on
/// page load. The script is a fixed literal (no interpolation of untrusted input → no
/// injection) and uses CIM/registry, not locale-dependent text parsing.
/// </para>
/// </summary>
public interface ISystemOverviewCollector
{
    Task<SystemOverview> GetAsync(CancellationToken cancellationToken = default);
}

public sealed class SystemOverviewCollector : JsonCollectorBase, ISystemOverviewCollector
{
    private readonly IWlanApi? _wlan;

    public SystemOverviewCollector(PowerShellRunner powerShell, IWlanApi? wlan = null)
        : base(powerShell)
    {
        _wlan = wlan;
    }

    public async Task<SystemOverview> GetAsync(CancellationToken cancellationToken = default)
    {
        var overview = new SystemOverview();

        // ---- Fast tier: never throws, always populated. ----
        try { FillIdentityAndOs(overview); } catch { /* leave defaults */ }
        try { FillNetwork(overview); } catch { /* leave defaults */ }
        try { FillWifiSsid(overview); } catch { /* leave defaults */ }

        // ---- Heavy tier: PowerShell, time-boxed, fully optional. ----
        try
        {
            var heavy = await RunCollectorAsync<HeavyFacts>(HeavyScript, TimeSpan.FromSeconds(12), cancellationToken)
                        .ConfigureAwait(false);
            if (heavy is not null) ApplyHeavy(overview, heavy);
            else overview.CollectorNote = "Some details need administrator rights or were unavailable.";
        }
        catch (OperationCanceledException)
        {
            overview.CollectorNote = "Detail lookup cancelled.";
        }
        catch
        {
            overview.CollectorNote = "Some details need administrator rights or were unavailable.";
        }

        overview.CapturedAt = DateTimeOffset.Now;
        return overview;
    }

    // ----------------------------------------------------------------- fast tier

    private static void FillIdentityAndOs(SystemOverview o)
    {
        o.Device.DeviceName = SafeOr(Environment.MachineName);
        o.User.CurrentUser = SafeOr($"{Environment.UserDomainName}\\{Environment.UserName}");
        o.User.ProfilePath = SafeOr(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

        try
        {
            using var id = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(id);
            var isAdmin = principal.IsInRole(WindowsBuiltInRole.Administrator);
            o.User.AdminRights = isAdmin ? "Administrator" : "Standard user";
            // The process token is only in the Administrators role when it's elevated.
            o.User.Elevated = isAdmin ? "Yes (elevated)" : "No (not elevated)";
        }
        catch { /* leave Unknown */ }

        o.Device.Architecture = RuntimeInformation.OSArchitecture.ToString();

        // OS edition / version / build straight from the registry (locale-independent;
        // Environment.OSVersion under-reports the build on Win10/11).
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            if (key is not null)
            {
                o.Device.WindowsEdition = SafeOr(key.GetValue("ProductName") as string);
                var display = key.GetValue("DisplayVersion") as string;     // e.g. "23H2"
                var release = key.GetValue("ReleaseId") as string;          // older fallback
                o.Device.WindowsVersion = SafeOr(display ?? release);
                var build = key.GetValue("CurrentBuild") as string;
                var ubr = key.GetValue("UBR");
                o.Device.OsBuild = build is null
                    ? "Unknown"
                    : ubr is int u ? $"{build}.{u}" : build;

                // Win11 still says "Windows 10 ..." in ProductName; correct it by build.
                if (int.TryParse(build, out var b) && b >= 22000 &&
                    o.Device.WindowsEdition.Contains("Windows 10", StringComparison.OrdinalIgnoreCase))
                {
                    o.Device.WindowsEdition = o.Device.WindowsEdition
                        .Replace("Windows 10", "Windows 11", StringComparison.OrdinalIgnoreCase);
                }
            }
        }
        catch { /* leave Unknown */ }

        try
        {
            var up = TimeSpan.FromMilliseconds(Environment.TickCount64);
            o.Device.Uptime = up.TotalDays >= 1
                ? $"{(int)up.TotalDays}d {up.Hours}h {up.Minutes}m"
                : $"{up.Hours}h {up.Minutes}m";
        }
        catch { /* leave Unknown */ }
    }

    private static void FillNetwork(SystemOverview o)
    {
        // "Active" adapter = operational, non-loopback/tunnel, has a unicast IPv4 AND a
        // default gateway (i.e. the NIC actually carrying traffic).
        var nic = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up
                        && n.NetworkInterfaceType != NetworkInterfaceType.Loopback
                        && n.NetworkInterfaceType != NetworkInterfaceType.Tunnel)
            .Select(n => new { Nic = n, Props = SafeProps(n) })
            .Where(x => x.Props is not null
                        && x.Props!.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork)
                        && x.Props.UnicastAddresses.Any(a => a.Address.AddressFamily == AddressFamily.InterNetwork))
            .Select(x => x.Nic)
            .FirstOrDefault()
            ?? NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n =>
                   n.OperationalStatus == OperationalStatus.Up
                   && n.NetworkInterfaceType != NetworkInterfaceType.Loopback);

        if (nic is null) return;

        o.Network.AdapterName = SafeOr(nic.Name);
        o.Network.ConnectionType = nic.NetworkInterfaceType switch
        {
            NetworkInterfaceType.Wireless80211 => "Wi-Fi",
            NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet
                or NetworkInterfaceType.FastEthernetT or NetworkInterfaceType.FastEthernetFx => "Ethernet",
            _ => nic.NetworkInterfaceType.ToString(),
        };
        o.Network.MacAddress = FormatMac(nic.GetPhysicalAddress());
        o.Network.LinkSpeed = nic.Speed > 0 ? $"{nic.Speed / 1_000_000.0:0.#} Mbps" : "Unknown";

        var props = SafeProps(nic);
        if (props is null) return;

        var ipv4 = props.UnicastAddresses.FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork);
        if (ipv4 is not null)
        {
            o.Network.Ipv4Address = ipv4.Address.ToString();
            o.Network.SubnetPrefix = $"/{ipv4.PrefixLength} ({PrefixToMask(ipv4.PrefixLength)})";
        }
        var gw = props.GatewayAddresses.FirstOrDefault(g => g.Address.AddressFamily == AddressFamily.InterNetwork);
        o.Network.DefaultGateway = SafeOr(gw?.Address.ToString());

        var dns = props.DnsAddresses
            .Where(a => a.AddressFamily == AddressFamily.InterNetwork)
            .Select(a => a.ToString())
            .ToArray();
        o.Network.DnsServers = dns.Length > 0 ? string.Join(", ", dns) : "Unknown";

        try
        {
            var v4 = props.GetIPv4Properties();
            o.Network.DhcpEnabled = v4 is null ? "Unknown" : (v4.IsDhcpEnabled ? "Yes" : "No (static)");
        }
        catch { /* leave Unknown */ }

        var dhcp = props.DhcpServerAddresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
        o.Network.DhcpServer = dhcp is null ? "—" : dhcp.ToString();
    }

    private void FillWifiSsid(SystemOverview o)
    {
        if (_wlan is null || !_wlan.IsAvailable) return;
        try
        {
            var ifaces = _wlan.EnumerateInterfaces();
            var connected = ifaces.FirstOrDefault(i => i.State == WlanInterfaceState.Connected);
            if (connected is null) return;
            var conn = _wlan.QueryCurrentConnection(connected.InterfaceGuid);
            if (conn is { InterfaceState: WlanInterfaceState.Connected } && !string.IsNullOrEmpty(conn.Ssid))
            {
                o.Network.WifiSsid = conn.Ssid;
            }
        }
        catch { /* leave "—" */ }
    }

    private static IPInterfaceProperties? SafeProps(NetworkInterface n)
    {
        try { return n.GetIPProperties(); }
        catch { return null; }
    }

    // ----------------------------------------------------------------- heavy tier

    private static void ApplyHeavy(SystemOverview o, HeavyFacts h)
    {
        o.Device.Manufacturer = SafeOr(h.Manufacturer);
        o.Device.Model = SafeOr(h.Model);
        o.Device.SerialNumber = SafeOr(h.SerialNumber);
        o.Device.LastBootTime = SafeOr(h.LastBootTime);

        o.Hardware.Manufacturer = SafeOr(h.Manufacturer);
        o.Hardware.Model = SafeOr(h.Model);
        o.Hardware.Chassis = SafeOr(h.Chassis);
        o.Hardware.Processor = SafeOr(h.Processor);
        o.Hardware.Cores = SafeOr(h.Cores);
        o.Hardware.InstalledRam = SafeOr(h.InstalledRam);
        o.Hardware.MemoryUsage = SafeOr(h.MemoryUsage);
        o.Hardware.Graphics = SafeOr(h.Graphics);
        o.Hardware.Storage = SafeOr(h.Storage);
        o.Hardware.StorageFree = SafeOr(h.StorageFree);
        o.Hardware.BiosVersion = SafeOr(h.BiosVersion);
        o.Hardware.Battery = SafeOr(h.Battery, "Not present");

        o.Organization.WorkgroupOrDomain = SafeOr(h.WorkgroupOrDomain);
        o.Organization.DomainJoined = SafeOr(h.DomainJoined);
        o.Organization.EntraJoined = SafeOr(h.EntraJoined);
        o.Organization.HybridJoined = SafeOr(h.HybridJoined);
        o.Organization.MdmEnrollment = SafeOr(h.MdmEnrollment);
        o.Organization.LogonServer = string.Equals(o.Organization.DomainJoined, "No", StringComparison.OrdinalIgnoreCase)
            ? "Not domain joined"
            : SafeOr(h.LogonServer);

        o.Security.DefenderStatus = SafeOr(h.DefenderStatus);
        o.Security.FirewallStatus = SafeOr(h.FirewallStatus);
        o.Security.BitLockerStatus = SafeOr(h.BitLockerStatus);
        o.Security.UacStatus = SafeOr(h.UacStatus);
        o.Security.PendingReboot = SafeOr(h.PendingReboot);
        o.Security.SecureBoot = SafeOr(h.SecureBoot);
        o.Security.TpmStatus = SafeOr(h.TpmStatus);
        o.Security.WindowsActivation = SafeOr(h.WindowsActivation);

        // Recent Critical / Error events — the heavy script returns a fixed-size array.
        if (h.RecentEvents is { Length: > 0 })
        {
            o.RecentEvents = h.RecentEvents
                .Where(e => e is not null)
                .Select(e => new SystemOverviewEvent
                {
                    Time = SafeOr(e!.Time, ""),
                    Log = SafeOr(e.Log, ""),
                    Level = SafeOr(e.Level, "Error"),
                    Source = SafeOr(e.Source, ""),
                    EventId = e.EventId,
                    Message = SafeOr(e.Message, ""),
                })
                .ToArray();
        }
    }

    private sealed class HeavyFacts
    {
        public string? Manufacturer { get; set; }
        public string? Model { get; set; }
        public string? SerialNumber { get; set; }
        public string? Chassis { get; set; }
        public string? Processor { get; set; }
        public string? Cores { get; set; }
        public string? InstalledRam { get; set; }
        public string? MemoryUsage { get; set; }
        public string? Graphics { get; set; }
        public string? Storage { get; set; }
        public string? StorageFree { get; set; }
        public string? BiosVersion { get; set; }
        public string? Battery { get; set; }
        public string? LastBootTime { get; set; }
        public string? WorkgroupOrDomain { get; set; }
        public string? DomainJoined { get; set; }
        public string? EntraJoined { get; set; }
        public string? HybridJoined { get; set; }
        public string? MdmEnrollment { get; set; }
        public string? LogonServer { get; set; }
        public string? DefenderStatus { get; set; }
        public string? FirewallStatus { get; set; }
        public string? BitLockerStatus { get; set; }
        public string? UacStatus { get; set; }
        public string? PendingReboot { get; set; }
        public string? SecureBoot { get; set; }
        public string? TpmStatus { get; set; }
        public string? WindowsActivation { get; set; }
        public HeavyEvent[]? RecentEvents { get; set; }
    }

    private sealed class HeavyEvent
    {
        public string? Time { get; set; }
        public string? Log { get; set; }
        public string? Level { get; set; }
        public string? Source { get; set; }
        public int EventId { get; set; }
        public string? Message { get; set; }
    }

    // Static literal — no interpolation of untrusted input. Every probe is individually
    // guarded so one missing capability (admin-only BitLocker/TPM, no dsregcmd, …) never
    // fails the whole snapshot. Output forced to a single JSON object.
    private const string HeavyScript = """
$ErrorActionPreference = 'SilentlyContinue'
function S($v, $fallback='Unknown') { if ($null -ne $v -and "$v".Trim() -ne '') { "$v".Trim() } else { $fallback } }

$cs  = Get-CimInstance Win32_ComputerSystem -ErrorAction SilentlyContinue
$bios= Get-CimInstance Win32_BIOS -ErrorAction SilentlyContinue
$os  = Get-CimInstance Win32_OperatingSystem -ErrorAction SilentlyContinue

# ---- Hardware (local CIM, no admin) ----
$cpuName='Unknown'; $cores='Unknown'
try {
    $cpu = Get-CimInstance Win32_Processor -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($cpu) {
        $cpuName = $cpu.Name
        $cores = "$($cpu.NumberOfCores) cores / $($cpu.NumberOfLogicalProcessors) threads"
    }
} catch {}

$ram='Unknown'
try {
    $bytes = (Get-CimInstance Win32_PhysicalMemory -ErrorAction SilentlyContinue | Measure-Object -Property Capacity -Sum).Sum
    if (-not $bytes -and $cs) { $bytes = $cs.TotalPhysicalMemory }
    if ($bytes) { $ram = "{0:N0} GB" -f [math]::Round($bytes / 1GB) }
} catch {}

# Live memory usage from Win32_OperatingSystem (KB units).
$memUsage='Unknown'
try {
    if ($os -and $os.TotalVisibleMemorySize -gt 0) {
        $totalKb = [double]$os.TotalVisibleMemorySize
        $freeKb  = [double]$os.FreePhysicalMemory
        $usedKb  = $totalKb - $freeKb
        $totalGb = [math]::Round($totalKb / 1048576, 1)
        $usedGb  = [math]::Round($usedKb  / 1048576, 1)
        $pct     = [math]::Round(($usedKb / $totalKb) * 100)
        $memUsage = "$usedGb / $totalGb GB used ($pct%)"
    }
} catch {}

$gpu='Unknown'
try {
    $g = Get-CimInstance Win32_VideoController -ErrorAction SilentlyContinue |
         Where-Object { $_.Name -and $_.Name -notmatch 'Basic Display|Remote Display|Meta|Mirror' } |
         Select-Object -ExpandProperty Name -Unique
    if ($g) { $gpu = ($g -join ' · ') }
} catch {}

$disk='Unknown'
try {
    $d = Get-CimInstance Win32_DiskDrive -ErrorAction SilentlyContinue |
         Where-Object { $_.Size -gt 0 } |
         ForEach-Object { "{0} ({1:N0} GB)" -f ($_.Model.Trim()), [math]::Round($_.Size / 1GB) }
    if ($d) { $disk = ($d -join ' · ') }
} catch {}

# System-drive free space (the one the OS boots from).
$diskFree='Unknown'
try {
    $sysDrive = $env:SystemDrive
    $ld = Get-CimInstance Win32_LogicalDisk -Filter "DriveType=3" -ErrorAction SilentlyContinue |
          Where-Object { $_.DeviceID -eq $sysDrive } | Select-Object -First 1
    if ($ld -and $ld.Size -gt 0) {
        $freeGb  = [math]::Round($ld.FreeSpace / 1GB)
        $totalGb = [math]::Round($ld.Size      / 1GB)
        $pct     = [math]::Round(($ld.FreeSpace / $ld.Size) * 100)
        $diskFree = "$freeGb GB free of $totalGb GB ($pct%)"
    }
} catch {}

$chassis='Unknown'
try {
    $ct = (Get-CimInstance Win32_SystemEnclosure -ErrorAction SilentlyContinue | Select-Object -First 1).ChassisTypes
    if ($ct) {
        $c = [int]$ct[0]
        if     ($c -in 8,9,10,11,14,18,21,30,31,32) { $chassis = 'Laptop / portable' }
        elseif ($c -in 3,4,5,6,7,15,16,24,35,36)    { $chassis = 'Desktop' }
        elseif ($c -in 17,23,28)                    { $chassis = 'Server' }
        elseif ($c -eq 13)                          { $chassis = 'All-in-one' }
        elseif ($c -eq 34)                          { $chassis = 'Embedded PC' }
        else                                        { $chassis = "Type $c" }
    }
} catch {}

$biosVer='Unknown'
try { if ($bios -and $bios.SMBIOSBIOSVersion) { $biosVer = "$($bios.SMBIOSBIOSVersion)" } } catch {}

# Battery (laptops only). Win32_Battery.BatteryStatus is an enum, see Microsoft docs.
$battery='Not present'
try {
    $bat = Get-CimInstance Win32_Battery -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($bat) {
        $statusName = switch ([int]$bat.BatteryStatus) {
            1  { 'Discharging' }
            2  { 'Plugged in (AC)' }
            3  { 'Fully charged' }
            4  { 'Low' }
            5  { 'Critical' }
            6  { 'Charging' }
            7  { 'Charging (high)' }
            8  { 'Charging (low)' }
            9  { 'Charging (critical)' }
            10 { 'Undefined' }
            11 { 'Partially charged' }
            default { "Status $($bat.BatteryStatus)" }
        }
        $pct = if ($bat.EstimatedChargeRemaining) { $bat.EstimatedChargeRemaining } else { '?' }
        $battery = "$pct% · $statusName"
    }
} catch {}

# Absolute last-boot time (separate from the relative uptime number).
$lastBoot='Unknown'
try { if ($os -and $os.LastBootUpTime) { $lastBoot = $os.LastBootUpTime.ToString('yyyy-MM-dd HH:mm:ss') } } catch {}

$workOrDomain = if ($cs -and $cs.PartOfDomain) { $cs.Domain } elseif ($cs) { $cs.Workgroup } else { $null }
$domainJoined = if ($cs) { if ($cs.PartOfDomain) { 'Yes' } else { 'No' } } else { 'Unknown' }

# Entra / hybrid join + MDM from dsregcmd (fixed English tokens, not localized).
# $dj is initialized so the hybrid comparison below is well-defined when dsregcmd is absent.
$aad='Unknown'; $dj='Unknown'; $hybrid='Unknown'; $mdm='Not enrolled'
try {
    $ds = (dsregcmd /status) 2>$null
    if ($ds) {
        $txt = ($ds -join "`n")
        if ($txt -match 'AzureAdJoined\s*:\s*(\w+)') { $aad = $Matches[1] }
        if ($txt -match 'DomainJoined\s*:\s*(\w+)')  { $dj  = $Matches[1] }
        if ($aad -eq 'YES' -and $dj -eq 'YES') { $hybrid='Yes' } elseif ($aad -eq 'YES') { $hybrid='No (Entra only)' } else { $hybrid='No' }
        if ($txt -match 'MdmUrl\s*:\s*(\S+)') { $mdm = 'Enrolled' }
    }
} catch {}

# Defender
$def='Unknown'
try {
    $mp = Get-MpComputerStatus -ErrorAction SilentlyContinue
    if ($mp) {
        $rt = if ($mp.RealTimeProtectionEnabled) { 'real-time on' } else { 'real-time OFF' }
        $av = if ($mp.AntivirusEnabled) { 'AV on' } else { 'AV OFF' }
        $def = "$av, $rt"
    }
} catch {}

# Firewall (per profile)
$fw='Unknown'
try {
    $p = Get-NetFirewallProfile -ErrorAction SilentlyContinue
    if ($p) {
        $on  = ($p | Where-Object { $_.Enabled } | ForEach-Object { $_.Name }) -join '/'
        $off = ($p | Where-Object { -not $_.Enabled } | ForEach-Object { $_.Name }) -join '/'
        if ($off) { $fw = "On: $on; OFF: $off" } else { $fw = "On (all profiles)" }
    }
} catch {}

# BitLocker on the system drive (needs admin → 'Requires admin' on failure)
$bl='Requires admin'
try {
    $sys = $env:SystemDrive
    $v = Get-BitLockerVolume -MountPoint $sys -ErrorAction Stop
    if ($v) { $bl = "$($v.VolumeStatus) ($($v.ProtectionStatus))" }
} catch { $bl = 'Requires admin' }

# UAC (registry, no admin needed)
$uac='Unknown'
try {
    $u = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System' -Name EnableLUA -ErrorAction Stop).EnableLUA
    $uac = if ($u -eq 1) { 'Enabled' } else { 'DISABLED' }
} catch {}

# Secure Boot (Confirm-SecureBootUEFI throws on legacy BIOS / no admin)
$sb='Unknown'
try { $sb = if (Confirm-SecureBootUEFI) { 'Enabled' } else { 'Disabled' } } catch { $sb = 'Unknown (legacy BIOS or needs admin)' }

# TPM
$tpm='Unknown'
try {
    $t = Get-Tpm -ErrorAction Stop
    if ($t) { $tpm = if ($t.TpmPresent) { if ($t.TpmReady) { 'Present, ready' } else { 'Present, not ready' } } else { 'Not present' } }
} catch { $tpm = 'Requires admin' }

# Pending reboot (any of the well-known markers)
$pending='No'
try {
    $cbs = Test-Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending'
    $wu  = Test-Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired'
    $pfr = (Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager' -Name PendingFileRenameOperations -ErrorAction SilentlyContinue) -ne $null
    if ($cbs -or $wu -or $pfr) { $pending = 'Yes — reboot required' }
} catch {}

# Windows activation — SoftwareLicensingProduct license-status enum.
$activation='Unknown'
try {
    $lic = Get-CimInstance SoftwareLicensingProduct -Filter "ApplicationID = '55c92734-d682-4d71-983e-d6ec3f16059f' AND PartialProductKey IS NOT NULL" -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($lic) {
        $activation = switch ([int]$lic.LicenseStatus) {
            1 { 'Activated' }
            0 { 'Unlicensed' }
            2 { 'Initial grace period' }
            3 { 'Additional grace period' }
            4 { 'Non-genuine grace period' }
            5 { 'Notification (needs activation)' }
            default { "Status $($lic.LicenseStatus)" }
        }
    }
} catch {}

$logon = 'Not domain joined'
if ($cs -and $cs.PartOfDomain -and $env:LOGONSERVER) {
    $candidate = "$env:LOGONSERVER".TrimStart('\').Trim()
    if ($candidate -and $candidate -ne $env:COMPUTERNAME) { $logon = $candidate }
}

# Recent Critical / Error events — last 48 h, top 8 across System + Application.
$events=@()
try {
    $cutoff = (Get-Date).AddHours(-48)
    $sys = Get-WinEvent -FilterHashtable @{LogName='System';      Level=1,2; StartTime=$cutoff} -MaxEvents 8 -ErrorAction SilentlyContinue
    $app = Get-WinEvent -FilterHashtable @{LogName='Application'; Level=1,2; StartTime=$cutoff} -MaxEvents 8 -ErrorAction SilentlyContinue
    $all = @()
    if ($sys) { $all += $sys }
    if ($app) { $all += $app }
    $top = $all | Sort-Object TimeCreated -Descending | Select-Object -First 8
    $events = @($top | ForEach-Object {
        $msg = if ($_.Message) { ($_.Message -replace '\s+', ' ').Trim() } else { '' }
        if ($msg.Length -gt 200) { $msg = $msg.Substring(0, 200) + '…' }
        [pscustomobject]@{
            Time    = $_.TimeCreated.ToString('yyyy-MM-dd HH:mm:ss')
            Log     = $_.LogName
            Level   = if ($_.Level -eq 1) { 'Critical' } else { 'Error' }
            Source  = if ($_.ProviderName) { $_.ProviderName } else { '' }
            EventId = [int]$_.Id
            Message = $msg
        }
    })
} catch {}

[pscustomobject]@{
    Manufacturer      = S ($cs.Manufacturer)
    Model             = S ($cs.Model)
    SerialNumber      = S ($bios.SerialNumber)
    Chassis           = S $chassis
    Processor         = S $cpuName
    Cores             = S $cores
    InstalledRam      = S $ram
    MemoryUsage       = S $memUsage
    Graphics          = S $gpu
    Storage           = S $disk
    StorageFree       = S $diskFree
    BiosVersion       = S $biosVer
    Battery           = S $battery 'Not present'
    LastBootTime      = S $lastBoot
    WorkgroupOrDomain = S $workOrDomain
    DomainJoined      = S $domainJoined
    EntraJoined       = S $aad
    HybridJoined      = S $hybrid
    MdmEnrollment     = S $mdm
    LogonServer       = S $logon
    DefenderStatus    = S $def
    FirewallStatus    = S $fw
    BitLockerStatus   = S $bl
    UacStatus         = S $uac
    PendingReboot     = S $pending
    SecureBoot        = S $sb
    TpmStatus         = S $tpm
    WindowsActivation = S $activation
    RecentEvents      = @($events)
} | ConvertTo-Json -Depth 4 -Compress
""";

    // ----------------------------------------------------------------- helpers

    private static string SafeOr(string? v, string fallback = "Unknown")
        => string.IsNullOrWhiteSpace(v) ? fallback : v.Trim();

    private static string FormatMac(PhysicalAddress addr)
    {
        var b = addr.GetAddressBytes();
        return b.Length == 6 ? string.Join(":", b.Select(x => x.ToString("X2"))) : "Unknown";
    }

    private static string PrefixToMask(int prefix)
    {
        if (prefix is < 0 or > 32) return "?";
        uint mask = prefix == 0 ? 0u : 0xFFFFFFFFu << (32 - prefix);
        return $"{(mask >> 24) & 0xFF}.{(mask >> 16) & 0xFF}.{(mask >> 8) & 0xFF}.{mask & 0xFF}";
    }
}
