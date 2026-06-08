using NetScopeDiagnosticCenter.Core;
using NetScopeDiagnosticCenter.Core.Models;
using NetScopeDiagnosticCenter.Infrastructure;

namespace NetScopeDiagnosticCenter.Collectors;

public sealed class PortTestCollector : JsonCollectorBase
{
    public PortTestCollector(PowerShellRunner powerShellRunner) : base(powerShellRunner)
    {
    }

    public async Task<PortTestResult> TestPortAsync(string target, int port, CancellationToken cancellationToken = default)
    {
        if (port is < 1 or > 65535)
        {
            return new PortTestResult
            {
                Target = target ?? string.Empty,
                Port = port,
                Severity = "Critical",
                Verdict = "Invalid port (port must be 1-65535)."
            };
        }

        if (!DiagnosticTargetValidator.TryNormalizeHost(target, out var normalizedTarget, out var validationReason))
        {
            return new PortTestResult
            {
                Target = target ?? string.Empty,
                Port = port,
                Severity = "Critical",
                Verdict = validationReason
            };
        }

        // Single audited choke-point: PsSingleQuote strips control chars + doubles quotes
        // so the host can't break out of the literal. $port is a validated int (1-65535)
        // so it's safe to interpolate unquoted.
        var safeTarget = PsSingleQuote(normalizedTarget);
        var script = $$"""
$ErrorActionPreference = 'Continue'
$target = '{{safeTarget}}'
$port = {{port}}
try {
    $result = Test-NetConnection -ComputerName $target -Port $port -InformationLevel Detailed -WarningAction SilentlyContinue -ErrorAction Stop
    $ping = [bool]$result.PingSucceeded
    $tcp = [bool]$result.TcpTestSucceeded
    $severity = if ($tcp) { 'OK' } elseif ($ping) { 'Warning' } else { 'Critical' }
    $verdict = if ($tcp) {
        'Target reachable and TCP port is open.'
    } elseif ($ping) {
        'Server reachable, but TCP port is closed or blocked.'
    } else {
        'Target is not reachable or ICMP is blocked; TCP port test failed.'
    }

    [pscustomobject]@{
        Target = $target
        Port = $port
        PingSucceeded = $ping
        TcpSucceeded = $tcp
        RemoteAddress = if ($result.RemoteAddress) { [string]$result.RemoteAddress } else { 'Unknown' }
        Verdict = $verdict
        Severity = $severity
        Details = [string]$result.Detailed
    } | ConvertTo-Json -Depth 5
}
catch {
    [pscustomobject]@{
        Target = $target
        Port = $port
        PingSucceeded = $false
        TcpSucceeded = $false
        RemoteAddress = 'Unknown'
        Verdict = 'Port test failed.'
        Severity = 'Critical'
        Details = $_.Exception.Message
    } | ConvertTo-Json -Depth 5
}
""";

        return await RunCollectorAsync<PortTestResult>(script, TimeSpan.FromSeconds(25), cancellationToken).ConfigureAwait(false)
            ?? new PortTestResult
            {
                Target = normalizedTarget,
                Port = port,
                Severity = "Critical",
                Verdict = "Port test collector failed."
            };
    }
}
