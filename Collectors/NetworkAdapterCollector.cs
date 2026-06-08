using NetScopeDiagnosticCenter.Core.Models;
using NetScopeDiagnosticCenter.Infrastructure;

namespace NetScopeDiagnosticCenter.Collectors;

public sealed class NetworkAdapterCollector : JsonCollectorBase
{
    public NetworkAdapterCollector(PowerShellRunner powerShellRunner) : base(powerShellRunner)
    {
    }

    public async Task<AdapterInfo> GetActiveAdapterAsync(CancellationToken cancellationToken = default)
    {
        const string script = """
$ErrorActionPreference = 'Stop'
function Convert-LinkSpeedMbps($speedBitsPerSecond) {
    # Use the NUMERIC Speed property (bits/sec) — locale-independent. The old code
    # parsed the localized LinkSpeed string ("1 Gbps" / "1 Gbit/s") with a comma->dot
    # hack that mis-read decimal-comma + thousands-separator locales.
    if ($null -eq $speedBitsPerSecond) { return $null }
    $bps = [double]$speedBitsPerSecond
    if ($bps -le 0) { return $null }
    return [math]::Round($bps / 1000000.0, 2)
}

$allAdapters = @(Get-NetAdapter -ErrorAction Stop)
$adapter = $null

# Prefer the adapter Windows is actually using for the current IPv4 default route.
# This avoids diagnosing the wrong NIC when multiple hardware adapters are Up.
$defaultRoutes = @(Get-NetRoute -DestinationPrefix '0.0.0.0/0' -ErrorAction SilentlyContinue |
    Sort-Object RouteMetric, ifMetric, InterfaceAlias)
foreach ($route in $defaultRoutes) {
    $candidate = $allAdapters |
        Where-Object {
            $_.Status -eq 'Up' -and
            $_.HardwareInterface -and
            ($_.ifIndex -eq $route.ifIndex -or $_.Name -eq $route.InterfaceAlias)
        } |
        Select-Object -First 1

    if ($candidate) {
        $adapter = $candidate
        break
    }
}

if ($null -eq $adapter) {
    $adapter = $allAdapters |
        Where-Object { $_.Status -eq 'Up' -and $_.HardwareInterface } |
        Sort-Object InterfaceMetric, Name |
        Select-Object -First 1
}

if ($null -eq $adapter) {
    $adapter = $allAdapters |
        Sort-Object @{Expression = { if ($_.Status -eq 'Up') { 0 } else { 1 } }}, Name |
        Select-Object -First 1
}

if ($null -eq $adapter) {
    [pscustomobject]@{ CollectorStatus = 'No network adapter found' } | ConvertTo-Json -Depth 5
    exit 0
}

$stats = Get-NetAdapterStatistics -Name $adapter.Name -ErrorAction SilentlyContinue
# RegistryKeyword is the locale-stable identifier ('*SpeedDuplex'); DisplayName is
# localized ("Speed & Duplex" → "Geschwindigkeit & Duplex" on a German OS).
$speedDuplex = Get-NetAdapterAdvancedProperty -Name $adapter.Name -ErrorAction SilentlyContinue |
    Where-Object { $_.RegistryKeyword -eq '*SpeedDuplex' } |
    Select-Object -First 1 -ExpandProperty DisplayValue

function Get-Counters($statistics) {
    if (-not $statistics) {
        return [pscustomobject]@{ Errors = 0; Discards = 0; Bytes = 0 }
    }

    [pscustomobject]@{
        Errors = [long]($statistics.ReceivedPacketErrors + $statistics.OutboundPacketErrors)
        Discards = [long]($statistics.ReceivedDiscardedPackets + $statistics.OutboundDiscardedPackets)
        Bytes = [long]($statistics.SentBytes + $statistics.ReceivedBytes)
    }
}

$errors = 0
$discards = 0
$errorsPerSecond = $null
$discardsPerSecond = $null
$bytesPerSecond = $null
$samplingStatus = 'Statistics unavailable'
if ($stats) {
    $first = Get-Counters $stats
    Start-Sleep -Milliseconds 1500
    $stats2 = Get-NetAdapterStatistics -Name $adapter.Name -ErrorAction SilentlyContinue
    if ($stats2) {
        $second = Get-Counters $stats2
        $seconds = 1.5
        $errorsPerSecond = [math]::Round([math]::Max(0, $second.Errors - $first.Errors) / $seconds, 2)
        $discardsPerSecond = [math]::Round([math]::Max(0, $second.Discards - $first.Discards) / $seconds, 2)
        $bytesPerSecond = [math]::Round([math]::Max(0, $second.Bytes - $first.Bytes) / $seconds, 2)
        $errors = $second.Errors
        $discards = $second.Discards
        $samplingStatus = 'Sampled for 1.5 seconds'
    } else {
        $errors = $first.Errors
        $discards = $first.Discards
        $samplingStatus = 'First statistics snapshot only'
    }
}

[pscustomobject]@{
    Name = [string]$adapter.Name
    InterfaceDescription = [string]$adapter.InterfaceDescription
    Status = [string]$adapter.Status
    ConnectionType = if ("$($adapter.PhysicalMediaType)" -match '802\.11|Wireless|WiFi|Wi-Fi') { 'Wi-Fi' } else { 'Ethernet' }
    LinkSpeed = [string]$adapter.LinkSpeed
    LinkSpeedMbps = Convert-LinkSpeedMbps $adapter.Speed
    MacAddress = [string]$adapter.MacAddress
    SpeedDuplex = if ($speedDuplex) { [string]$speedDuplex } else { 'Unknown' }
    DriverInformation = [string]$adapter.DriverInformation
    Errors = $errors
    Discards = $discards
    BytesSent = if ($stats) { [long]$stats.SentBytes } else { 0 }
    BytesReceived = if ($stats) { [long]$stats.ReceivedBytes } else { 0 }
    ErrorsPerSecond = $errorsPerSecond
    DiscardsPerSecond = $discardsPerSecond
    BytesPerSecond = $bytesPerSecond
    SamplingStatus = $samplingStatus
    CollectorStatus = 'OK'
} | ConvertTo-Json -Depth 5
""";

        var (data, execution) = await RunCollectorWithExecutionAsync<AdapterInfo>(script, TimeSpan.FromSeconds(18), cancellationToken);
        return data ?? new AdapterInfo
        {
            CollectorStatus = execution.TimedOut
                ? "Adapter collector timed out."
                : $"Adapter collector failed: {execution.Error}"
        };
    }
}
