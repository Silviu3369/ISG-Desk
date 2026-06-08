using NetScopeDiagnosticCenter.Collectors.Shared;
using NetScopeDiagnosticCenter.Core.Models;
using NetScopeDiagnosticCenter.Infrastructure;

namespace NetScopeDiagnosticCenter.Collectors;

public class PrintServerCollector : JsonCollectorBase
{
    public PrintServerCollector(PowerShellRunner powerShellRunner) : base(powerShellRunner)
    {
    }

    public virtual async Task<PrinterDiscoveryResult> DiscoverFromPrintServerAsync(
        string printServer,
        CancellationToken cancellationToken = default)
    {
        // Safety: the print-server name flows into Resolve-DnsName, Test-NetConnection
        // 135/445 and Get-Printer -ComputerName. We restrict to single-label LAN names
        // (no dots) or RFC-1918 private IPv4 addresses.
        if (!PrinterTargetValidator.TryNormalizePrintServer(printServer, out var normalizedPrintServer, out var rejectReason))
        {
            return new PrinterDiscoveryResult
            {
                Source = printServer ?? string.Empty,
                Severity = "Critical",
                Verdict = rejectReason,
                Evidence = { rejectReason },
                Limitations = { "Print Server discovery accepts only a LAN host: a single-label NetBIOS name (no dots) or a private IPv4 (10/8, 172.16/12, 192.168/16)." },
            };
        }

        // Centralized choke-point: strips control chars + caps length + doubles quotes.
        var safePrintServer = PsSingleQuote(normalizedPrintServer);
        var script = $$"""
$ErrorActionPreference = 'Continue'
$server = '{{safePrintServer}}'
$evidence = New-Object System.Collections.Generic.List[string]
$printers = @()
$ports = @{}

function Add-Evidence($text) { $script:evidence.Add([string]$text) | Out-Null }
function Read-Property($object, $name) {
    if ($null -eq $object) { return '' }
    $property = $object.PSObject.Properties[$name]
    if ($null -eq $property -or $null -eq $property.Value) { return '' }
    return [string]$property.Value
}
function Format-PortProtocol($value) {
    if ($null -eq $value -or [string]::IsNullOrWhiteSpace([string]$value)) { return '' }
    switch ([string]$value) {
        '1' { return 'RAW' }
        '2' { return 'LPR' }
        default { return [string]$value }
    }
}

try {
    Resolve-DnsName -Name $server -ErrorAction Stop | Out-Null
    Add-Evidence "DNS resolve for $server succeeded."
} catch {
    Add-Evidence "DNS resolve for $server failed: $($_.Exception.Message)"
}

try {
    $ping = Test-Connection -ComputerName $server -Count 2 -Quiet -ErrorAction SilentlyContinue
    Add-Evidence ('Ping to {0}: {1}.' -f $server, $ping)
} catch {
    Add-Evidence "Ping to $server failed: $($_.Exception.Message)"
}

foreach ($port in @(135,445)) {
    try {
        $tcp = Test-NetConnection -ComputerName $server -Port $port -InformationLevel Quiet -WarningAction SilentlyContinue -ErrorAction SilentlyContinue
        Add-Evidence ('TCP {0} to {1}: {2}.' -f $port, $server, $tcp)
    } catch {
        Add-Evidence "TCP $port to $server failed: $($_.Exception.Message)"
    }
}

$spoolerStatus = 'Unknown'
try {
    $spooler = Get-Service -ComputerName $server -Name Spooler -ErrorAction Stop
    $spoolerStatus = [string]$spooler.Status
    Add-Evidence "Print Spooler on $server status: $spoolerStatus."
} catch {
    Add-Evidence "Print Spooler status on $server could not be read: $($_.Exception.Message)"
}

try {
    Get-PrinterPort -ComputerName $server -ErrorAction Stop | ForEach-Object {
        $ports[[string]$_.Name] = $_
    }
    Add-Evidence "Printer port enumeration returned $($ports.Count) ports from $server."
} catch {
    Add-Evidence "Printer port enumeration from $server failed: $($_.Exception.Message)"
}

try {
    $printers = @(Get-Printer -ComputerName $server -ErrorAction Stop | ForEach-Object {
        $port = $ports[[string]$_.PortName]
        $share = if ($_.ShareName) { [string]$_.ShareName } else { '' }
        $installable = [bool]$_.Shared -and -not [string]::IsNullOrWhiteSpace($share)
        [pscustomobject]@{
            Name = [string]$_.Name
            ShareName = $share
            DriverName = if ($_.DriverName) { [string]$_.DriverName } else { '' }
            PortName = if ($_.PortName) { [string]$_.PortName } else { '' }
            PortHostAddress = Read-Property $port 'PrinterHostAddress'
            PortProtocol = Format-PortProtocol (Read-Property $port 'Protocol')
            ConnectionName = if ($installable) { ('\\{0}\{1}' -f $server, $share) } else { '' }
            PrinterStatus = if ($_.PrinterStatus) { [string]$_.PrinterStatus } else { 'Unknown' }
            Location = Read-Property $_ 'Location'
            Comment = Read-Property $_ 'Comment'
            Shared = [bool]$_.Shared
            IsDefault = $false
            Installable = $installable
            InstallStatus = ''
            InstallError = ''
        }
    })
    Add-Evidence "Printer enumeration returned $($printers.Count) printers."
    $verdict = if ($printers.Count -gt 0) { 'Print server reachable and printers were enumerated.' } else { 'Print server reachable but no printers were returned.' }
    $severity = if ($printers.Count -gt 0 -and $spoolerStatus -eq 'Running') { 'OK' } else { 'Warning' }
} catch {
    Add-Evidence "Printer enumeration failed: $($_.Exception.Message)"
    $verdict = 'Print server reachable checks completed, but printer enumeration failed.'
    $severity = 'Warning'
}

[pscustomobject]@{
    Mode = 'PrintServer'
    Source = $server
    Verdict = $verdict
    Severity = $severity
    MaxHosts = 254
    PrintServerSpoolerStatus = $spoolerStatus
    Evidence = @($evidence)
    Limitations = @('Print server discovery is read-only. Queue installation must be started explicitly from a selected shared queue.')
    Printers = @($printers)
    ScanResults = @()
} | ConvertTo-Json -Depth 8
""";

        return await RunCollectorAsync<PrinterDiscoveryResult>(script, TimeSpan.FromSeconds(35), cancellationToken)
            ?? new PrinterDiscoveryResult
            {
                Mode = "PrintServer",
                Source = normalizedPrintServer,
                Verdict = "Print server discovery collector failed.",
                Severity = "Critical",
                MaxHosts = 254
            };
    }
}
