using NetScopeDiagnosticCenter.Core.Models;
using NetScopeDiagnosticCenter.Infrastructure;

namespace NetScopeDiagnosticCenter.Collectors;

/// <summary>
/// First-aid network repair actions for the Diagnosis page. Unlike the read-only
/// collectors, these CHANGE system state — every action is explicit, single-shot,
/// time-boxed and reports exactly what it did.
///
/// <list type="bullet">
///   <item><b>Flush DNS</b> — <c>Clear-DnsClientCache</c>; no admin needed, no outage.</item>
///   <item><b>Renew DHCP lease</b> — <c>ipconfig /release</c> + <c>/renew</c>; connectivity
///   drops for a few seconds; no admin needed.</item>
///   <item><b>Reset Winsock</b> — <c>netsh winsock reset</c>; needs admin and a restart.</item>
///   <item><b>Restart adapter</b> — <c>Restart-NetAdapter</c>; needs admin; brief outage.</item>
/// </list>
///
/// Adapter names are embedded through <see cref="JsonCollectorBase.PsSingleQuote"/> —
/// the single audited choke-point — so a hostile adapter name can never break out of
/// the script literal.
/// </summary>
public class NetworkRepairService : JsonCollectorBase
{
    public NetworkRepairService(PowerShellRunner powerShell)
        : base(powerShell)
    {
    }

    // Virtual so unit tests can fake outcomes without spawning PowerShell.
    public virtual Task<RepairActionResult> FlushDnsAsync(CancellationToken cancellationToken = default) =>
        RunRepairAsync("Flush DNS cache", FlushDnsScript, TimeSpan.FromSeconds(30), cancellationToken);

    public virtual Task<RepairActionResult> RenewDhcpLeaseAsync(CancellationToken cancellationToken = default) =>
        RunRepairAsync("Renew DHCP lease", RenewDhcpScript, TimeSpan.FromSeconds(90), cancellationToken);

    public virtual Task<RepairActionResult> ResetWinsockAsync(CancellationToken cancellationToken = default) =>
        RunRepairAsync("Reset Winsock", ResetWinsockScript, TimeSpan.FromSeconds(45), cancellationToken);

    public virtual Task<RepairActionResult> RestartAdapterAsync(string adapterName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(adapterName))
        {
            return Task.FromResult(new RepairActionResult
            {
                ActionName = "Restart adapter",
                Success = false,
                Summary = "No active adapter is known yet — run Quick Diagnosis first."
            });
        }

        var script = RestartAdapterScriptTemplate.Replace("__ADAPTER__", PsSingleQuote(adapterName), StringComparison.Ordinal);
        return RunRepairAsync($"Restart adapter '{adapterName.Trim()}'", script, TimeSpan.FromSeconds(60), cancellationToken);
    }

    private async Task<RepairActionResult> RunRepairAsync(
        string actionName,
        string script,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        try
        {
            var dto = await RunCollectorAsync<RepairDto>(script, timeout, cancellationToken).ConfigureAwait(false);
            if (dto is null)
            {
                return new RepairActionResult
                {
                    ActionName = actionName,
                    Success = false,
                    Summary = "The repair command produced no result (PowerShell unavailable or timed out)."
                };
            }

            return new RepairActionResult
            {
                ActionName = actionName,
                Success = dto.Success,
                Summary = string.IsNullOrWhiteSpace(dto.Summary) ? (dto.Success ? "Completed." : "Failed.") : dto.Summary.Trim(),
                OutputLines = (dto.Output ?? [])
                    .Where(line => !string.IsNullOrWhiteSpace(line))
                    .Select(line => line.Trim())
                    .Take(8)
                    .ToArray(),
                RestartRequired = dto.RestartRequired
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new RepairActionResult
            {
                ActionName = actionName,
                Success = false,
                Summary = $"Repair failed: {ex.Message}"
            };
        }
    }

    private sealed class RepairDto
    {
        public bool Success { get; set; }
        public string? Summary { get; set; }
        public string[]? Output { get; set; }
        public bool RestartRequired { get; set; }
    }

    // ---- Scripts: static literals (only the adapter name is injected, via PsSingleQuote). ----

    private const string FlushDnsScript = """
$ErrorActionPreference = 'Stop'
$result = $null
try {
    Clear-DnsClientCache
    $result = [pscustomobject]@{ Success = $true; Summary = 'DNS client cache cleared. Stale name lookups will resolve fresh.'; Output = @(); RestartRequired = $false }
} catch {
    $result = [pscustomobject]@{ Success = $false; Summary = "Flush DNS failed: $($_.Exception.Message)"; Output = @(); RestartRequired = $false }
}
$result | ConvertTo-Json -Compress
""";

    private const string RenewDhcpScript = """
$ErrorActionPreference = 'SilentlyContinue'
$null = (ipconfig /release) 2>&1
Start-Sleep -Seconds 1
$renewText = (ipconfig /renew) 2>&1 | Out-String
$code = $LASTEXITCODE
# Show the fresh DHCP-assigned IPv4 addresses as proof of the new lease.
$ips = @(Get-NetIPAddress -AddressFamily IPv4 -PrefixOrigin Dhcp -ErrorAction SilentlyContinue |
         Where-Object { $_.IPAddress -and $_.IPAddress -ne '0.0.0.0' } |
         ForEach-Object { "$($_.InterfaceAlias): $($_.IPAddress)" })
$ok = ($code -eq 0) -and ($ips.Count -gt 0)
$summary = if ($ok) { 'DHCP lease released and renewed.' }
           elseif ($code -ne 0) { 'ipconfig /renew reported an error - check that DHCP is enabled on the adapter.' }
           else { 'Renew finished but no DHCP-assigned IPv4 address was found.' }
[pscustomobject]@{
    Success = $ok
    Summary = $summary
    Output = @($ips)
    RestartRequired = $false
} | ConvertTo-Json -Compress
""";

    private const string ResetWinsockScript = """
$out = (netsh winsock reset) 2>&1 | Out-String
$code = $LASTEXITCODE
$ok = $code -eq 0
$lines = @(($out -split "`r?`n") | Where-Object { $_.Trim() } | Select-Object -First 6)
[pscustomobject]@{
    Success = $ok
    Summary = if ($ok) { 'Winsock catalog reset. Restart the computer to complete the repair.' }
              else { 'Winsock reset failed - this action requires administrator rights.' }
    Output = $lines
    RestartRequired = $ok
} | ConvertTo-Json -Compress
""";

    private const string RestartAdapterScriptTemplate = """
$ErrorActionPreference = 'Stop'
$name = '__ADAPTER__'
$result = $null
try {
    Restart-NetAdapter -Name $name -Confirm:$false
    Start-Sleep -Seconds 4
    $status = (Get-NetAdapter -Name $name -ErrorAction Stop).Status
    $result = [pscustomobject]@{
        Success = ($status -eq 'Up')
        Summary = "Adapter '$name' restarted. Current status: $status."
        Output = @()
        RestartRequired = $false
    }
} catch {
    $result = [pscustomobject]@{
        Success = $false
        Summary = "Restart adapter failed: $($_.Exception.Message)"
        Output = @()
        RestartRequired = $false
    }
}
$result | ConvertTo-Json -Compress
""";
}
