using NetScopeDiagnosticCenter.Core.Models;
using NetScopeDiagnosticCenter.Infrastructure;

namespace NetScopeDiagnosticCenter.Collectors;

public sealed class IpConfigurationCollector : JsonCollectorBase
{
    public IpConfigurationCollector(PowerShellRunner powerShellRunner) : base(powerShellRunner)
    {
    }

    public async Task<IpConfigurationInfo> GetIpConfigurationAsync(string adapterName, CancellationToken cancellationToken = default)
    {
        var safeAdapterName = PsSingleQuote(adapterName);
        var script = $$"""
$ErrorActionPreference = 'Stop'
$config = Get-NetIPConfiguration -InterfaceAlias '{{safeAdapterName}}' -ErrorAction SilentlyContinue
if ($null -eq $config) {
    [pscustomobject]@{ InterfaceAlias = '{{safeAdapterName}}'; CollectorStatus = 'IP configuration unavailable' } | ConvertTo-Json -Depth 5
    exit 0
}

$ipv4 = $config.IPv4Address | Select-Object -First 1 -ExpandProperty IPAddress
$gateway = $config.IPv4DefaultGateway | Select-Object -First 1 -ExpandProperty NextHop
$dns = @()
if ($config.DNSServer -and $config.DNSServer.ServerAddresses) {
    $dns = @($config.DNSServer.ServerAddresses)
}

[pscustomobject]@{
    InterfaceAlias = [string]$config.InterfaceAlias
    IpAddress = if ($ipv4) { [string]$ipv4 } else { 'Unknown' }
    Gateway = if ($gateway) { [string]$gateway } else { 'Unknown' }
    DnsServers = $dns
    CollectorStatus = 'OK'
} | ConvertTo-Json -Depth 5
""";

        var (data, execution) = await RunCollectorWithExecutionAsync<IpConfigurationInfo>(script, TimeSpan.FromSeconds(15), cancellationToken);
        return data ?? new IpConfigurationInfo
        {
            InterfaceAlias = adapterName,
            CollectorStatus = execution.TimedOut
                ? "IP collector timed out."
                : $"IP collector failed: {execution.Error}"
        };
    }
}
