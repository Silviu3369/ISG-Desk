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
}
