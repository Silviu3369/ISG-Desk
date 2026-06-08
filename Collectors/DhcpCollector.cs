using NetScopeDiagnosticCenter.Core.Models;
using NetScopeDiagnosticCenter.Infrastructure;

namespace NetScopeDiagnosticCenter.Collectors;

public sealed class DhcpCollector : JsonCollectorBase
{
    public DhcpCollector(PowerShellRunner powerShellRunner) : base(powerShellRunner)
    {
    }

    public async Task<DhcpInfo> GetDhcpInfoAsync(string adapterName, CancellationToken cancellationToken = default)
    {
        var safeAdapterName = PsSingleQuote(adapterName);
        var script = $$$"""
$ErrorActionPreference = 'Continue'
$adapterName = '{{{safeAdapterName}}}'
$adapter = if ($adapterName) {
    Get-NetAdapter -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -eq $adapterName } |
        Select-Object -First 1
} else {
    $null
}

$config = $null
if ($adapter) {
    $configs = @(Get-CimInstance Win32_NetworkAdapterConfiguration -Filter "IPEnabled=True" -ErrorAction SilentlyContinue)
    $config = $configs |
        Where-Object {
            $_.InterfaceIndex -eq $adapter.ifIndex -or
            $_.Description -eq $adapter.InterfaceDescription
        } |
        Select-Object -First 1
}

if ($null -eq $config -and -not $adapterName) {
    $config = Get-CimInstance Win32_NetworkAdapterConfiguration -Filter "IPEnabled=True" -ErrorAction SilentlyContinue |
        Sort-Object @{Expression = { if ($_.DefaultIPGateway) { 0 } else { 1 } }} |
        Select-Object -First 1
}

if ($null -eq $config) {
    [pscustomobject]@{
        AdapterName = if ($adapterName) { $adapterName } else { 'Unknown' }
        CollectorStatus = if ($adapterName) { 'DHCP information unavailable for selected adapter' } else { 'DHCP information unavailable' }
    } | ConvertTo-Json -Depth 4
    exit 0
}

[pscustomobject]@{
    AdapterName = [string]$config.Description
    DhcpEnabled = if ($null -ne $config.DHCPEnabled) { [bool]$config.DHCPEnabled } else { $null }
    DhcpServer = if ($config.DHCPServer) { [string]$config.DHCPServer } else { 'Unknown' }
    LeaseObtained = if ($config.DHCPLeaseObtained) { [string]$config.DHCPLeaseObtained } else { 'Unknown' }
    LeaseExpires = if ($config.DHCPLeaseExpires) { [string]$config.DHCPLeaseExpires } else { 'Unknown' }
    CollectorStatus = 'OK'
} | ConvertTo-Json -Depth 4
""";

        var (data, execution) = await RunCollectorWithExecutionAsync<DhcpInfo>(script, TimeSpan.FromSeconds(12), cancellationToken);
        return data ?? new DhcpInfo
        {
            AdapterName = adapterName,
            CollectorStatus = execution.TimedOut
                ? "DHCP collector timed out."
                : $"DHCP collector failed: {execution.Error}"
        };
    }
}
