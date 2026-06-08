using NetScopeDiagnosticCenter.Core.Models;
using NetScopeDiagnosticCenter.Infrastructure;

namespace NetScopeDiagnosticCenter.Collectors;

public sealed class PcContextCollector : JsonCollectorBase
{
    public PcContextCollector(PowerShellRunner powerShellRunner) : base(powerShellRunner)
    {
    }

    public async Task<PcContextInfo> GetContextAsync(CancellationToken cancellationToken = default)
    {
        const string script = """
$ErrorActionPreference = 'Continue'

function Test-IsAdmin {
    try {
        $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
        $principal = [Security.Principal.WindowsPrincipal]::new($identity)
        return [bool]$principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    } catch {
        return $null
    }
}

$vpnAdapters = @()
try {
    $vpnAdapters = @(Get-NetAdapter -ErrorAction SilentlyContinue |
        Where-Object {
            $_.Status -eq 'Up' -and (
                $_.Name -match 'vpn|wireguard|openvpn|anyconnect|globalprotect|fortinet|checkpoint|tap|tun' -or
                $_.InterfaceDescription -match 'vpn|wireguard|openvpn|anyconnect|globalprotect|fortinet|checkpoint|tap|tun'
            )
        } |
        ForEach-Object { "$($_.Name) ($($_.InterfaceDescription))" })
} catch { }

$defaultRoutes = @()
try {
    $defaultRoutes = @(Get-NetRoute -DestinationPrefix '0.0.0.0/0' -ErrorAction SilentlyContinue |
        Sort-Object RouteMetric, InterfaceMetric |
        ForEach-Object { "$($_.NextHop) via $($_.InterfaceAlias) metric $($_.RouteMetric + $_.InterfaceMetric)" })
} catch { }

$proxyEnabled = $null
$proxyServer = 'Unknown'
try {
    $proxy = Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Internet Settings' -ErrorAction SilentlyContinue
    if ($proxy) {
        $proxyEnabled = [bool]$proxy.ProxyEnable
        if ($proxy.ProxyServer) { $proxyServer = [string]$proxy.ProxyServer }
    }
} catch { }

$ipv6Enabled = $null
try {
    $bindings = @(Get-NetAdapterBinding -ComponentID ms_tcpip6 -ErrorAction SilentlyContinue)
    if ($bindings.Count -gt 0) {
        $ipv6Enabled = [bool](@($bindings | Where-Object { $_.Enabled }).Count -gt 0)
    }
} catch { }

$mtu = $null
try {
    $ipInterface = Get-NetIPInterface -AddressFamily IPv4 -ErrorAction SilentlyContinue |
        Where-Object { $_.ConnectionState -eq 'Connected' } |
        Sort-Object InterfaceMetric |
        Select-Object -First 1
    if ($ipInterface -and $ipInterface.NlMtu) { $mtu = [int]$ipInterface.NlMtu }
} catch { }

[pscustomobject]@{
    IsAdministrator = Test-IsAdmin
    ProxyEnabled = $proxyEnabled
    ProxyServer = $proxyServer
    VpnAdapters = @($vpnAdapters)
    DefaultRoutes = @($defaultRoutes)
    Ipv6Enabled = $ipv6Enabled
    Mtu = $mtu
    CollectorStatus = 'OK'
} | ConvertTo-Json -Depth 6
""";

        var (data, execution) = await RunCollectorWithExecutionAsync<PcContextInfo>(script, TimeSpan.FromSeconds(15), cancellationToken);
        return data ?? new PcContextInfo
        {
            CollectorStatus = execution.TimedOut
                ? "PC context collector timed out."
                : $"PC context collector failed: {execution.Error}"
        };
    }
}
