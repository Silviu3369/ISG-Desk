using NetScopeDiagnosticCenter.Core.Models;
using NetScopeDiagnosticCenter.Infrastructure;

namespace NetScopeDiagnosticCenter.Collectors;

public class DomainCollector : JsonCollectorBase
{
    public DomainCollector(PowerShellRunner powerShellRunner) : base(powerShellRunner)
    {
    }

    public virtual async Task<DomainInfo> GetDomainInfoAsync(CancellationToken cancellationToken = default)
    {
        const string script = """
$ErrorActionPreference = 'Continue'
$computer = Get-CimInstance Win32_ComputerSystem -ErrorAction SilentlyContinue
$isDomainJoined = if ($computer) { [bool]$computer.PartOfDomain } else { $false }
$suffixes = @()
if ($isDomainJoined) {
try {
    $global = Get-DnsClientGlobalSetting -ErrorAction SilentlyContinue
    if ($global.SuffixSearchList) { $suffixes += @($global.SuffixSearchList) }
    $clientSuffixes = Get-DnsClient -ErrorAction SilentlyContinue | Where-Object { $_.ConnectionSpecificSuffix } | Select-Object -ExpandProperty ConnectionSpecificSuffix
    if ($clientSuffixes) { $suffixes += @($clientSuffixes) }
} catch { }
}

$logonServer = 'Not domain joined'
if ($isDomainJoined -and $env:LOGONSERVER) {
    $candidate = $env:LOGONSERVER.TrimStart('\').Trim()
    if ($candidate -and $candidate -ne $env:COMPUTERNAME) { $logonServer = $candidate }
}

[pscustomobject]@{
    IsDomainJoined = $isDomainJoined
    DomainName = if ($isDomainJoined -and $computer.Domain) { [string]$computer.Domain } else { 'Not domain joined' }
    LogonServer = $logonServer
    DnsSuffixes = @($suffixes | Where-Object { $_ } | Select-Object -Unique)
    CollectorStatus = if ($computer) { 'OK' } else { 'Domain information unavailable' }
} | ConvertTo-Json -Depth 5
""";

        var (data, execution) = await RunCollectorWithExecutionAsync<DomainInfo>(script, TimeSpan.FromSeconds(12), cancellationToken);
        return data ?? new DomainInfo
        {
            CollectorStatus = execution.TimedOut
                ? "Domain collector timed out."
                : $"Domain collector failed: {execution.Error}"
        };
    }

    /// <summary>
    /// Verifies the machine-account secure channel to the domain
    /// (<c>Test-ComputerSecureChannel</c> — the "trust relationship failed" check).
    /// Per Microsoft docs the cmdlet needs administrator rights and only applies to
    /// domain members; both limitations degrade to Status "Unknown" with a hint,
    /// never to a failure of the targeted test.
    /// </summary>
    public virtual async Task<SecureChannelCheckResult> TestSecureChannelAsync(CancellationToken cancellationToken = default)
    {
        const string script = """
$ErrorActionPreference = 'Stop'
try {
    $healthy = Test-ComputerSecureChannel
    [pscustomobject]@{
        Status = if ($healthy) { 'OK' } else { 'Critical' }
        Detail = if ($healthy) { 'Machine account secure channel to the domain verified successfully.' }
                 else { 'Secure channel verification FAILED - the machine account trust with the domain is broken.' }
    }
} catch {
    $msg = $_.Exception.Message
    $needsAdmin = $msg -match 'denied|elevat|privile'
    [pscustomobject]@{
        Status = 'Unknown'
        Detail = if ($needsAdmin) { 'Secure channel check needs administrator rights - relaunch elevated to verify the machine account trust.' }
                 else { "Secure channel could not be verified: $msg" }
    }
} | ConvertTo-Json -Compress
""";

        var (data, execution) = await RunCollectorWithExecutionAsync<SecureChannelCheckResult>(script, TimeSpan.FromSeconds(20), cancellationToken);
        return data ?? new SecureChannelCheckResult
        {
            Status = "Unknown",
            Detail = execution.TimedOut
                ? "Secure channel check timed out."
                : $"Secure channel check failed: {execution.Error}"
        };
    }
}

/// <summary>Outcome of the machine-account trust verification ("OK" / "Critical" / "Unknown").</summary>
public sealed class SecureChannelCheckResult
{
    public string Status { get; set; } = "Unknown";
    public string Detail { get; set; } = string.Empty;
}
