using NetScopeDiagnosticCenter.Core.Models;
using NetScopeDiagnosticCenter.Infrastructure;

namespace NetScopeDiagnosticCenter.Collectors;

public sealed class DnsCollector : JsonCollectorBase
{
    public DnsCollector(PowerShellRunner powerShellRunner) : base(powerShellRunner)
    {
    }

    public async Task<ConnectivityTests> RunBasicTestsAsync(
        string gateway,
        IReadOnlyList<string> dnsServers,
        CancellationToken cancellationToken = default)
    {
        var safeGateway = PsSingleQuote(gateway);
        var dnsArrayLiteral = dnsServers.Count == 0
            ? "@()"
            : "@(" + string.Join(", ", dnsServers.Select(server => $"'{PsSingleQuote(server)}'")) + ")";

        var script = $$"""
$ErrorActionPreference = 'Continue'
function New-UnknownResult($target, $details) {
    [pscustomobject]@{ Target = $target; Status = 'Unknown'; LatencyMs = $null; Details = $details }
}

function Test-PingTarget($target, $label) {
    if ([string]::IsNullOrWhiteSpace($target) -or $target -eq 'Unknown') {
        return New-UnknownResult $label 'No target available.'
    }

    $sent = 2
    $received = 0
    $latencies = @()
    try {
        $ping = [System.Net.NetworkInformation.Ping]::new()
        for ($i = 0; $i -lt $sent; $i++) {
            $reply = $ping.Send($target, 1200)
            if ($reply.Status -eq [System.Net.NetworkInformation.IPStatus]::Success) {
                $received++
                $latencies += [double]$reply.RoundtripTime
            }
        }
        $avg = if ($latencies.Count -gt 0) { [math]::Round(($latencies | Measure-Object -Average).Average, 1) } else { $null }
        $status = if ($received -eq 0) { 'Critical' } elseif ($received -lt $sent -or ($avg -ne $null -and $avg -gt 100)) { 'Warning' } else { 'OK' }
        $details = 'Ping {0}: {1}/{2} replies.' -f $target, $received, $sent
        return [pscustomobject]@{ Target = $label; Status = $status; LatencyMs = $avg; Details = $details }
    }
    catch {
        return [pscustomobject]@{ Target = $label; Status = 'Critical'; LatencyMs = $null; Details = $_.Exception.Message }
    }
}

function Test-PacketLoss($target, $label) {
    if ([string]::IsNullOrWhiteSpace($target) -or $target -eq 'Unknown') {
        return [pscustomobject]@{ Target = $label; Status = 'Unknown'; Sent = 0; Received = 0; LossPercent = $null; Details = 'No target available.' }
    }

    $sent = 4
    try {
        $received = 0
        $ping = [System.Net.NetworkInformation.Ping]::new()
        for ($i = 0; $i -lt $sent; $i++) {
            $reply = $ping.Send($target, 1200)
            if ($reply.Status -eq [System.Net.NetworkInformation.IPStatus]::Success) {
                $received++
            }
        }
        $loss = [math]::Round((($sent - $received) / $sent) * 100, 1)
        $status = if ($loss -eq 0) { 'OK' } elseif ($loss -le 5) { 'Warning' } else { 'Critical' }
        return [pscustomobject]@{
            Target = $target
            Status = $status
            Sent = $sent
            Received = $received
            LossPercent = $loss
            Details = "$loss% packet loss ($received/$sent replies)."
        }
    }
    catch {
        return [pscustomobject]@{ Target = $target; Status = 'Critical'; Sent = $sent; Received = 0; LossPercent = 100; Details = $_.Exception.Message }
    }
}

function Test-DnsLookup($name, $label, $server) {
    try {
        $sw = [System.Diagnostics.Stopwatch]::StartNew()
        if ([string]::IsNullOrWhiteSpace($server)) {
            Resolve-DnsName -Name $name -Type A -ErrorAction Stop | Out-Null
        } else {
            Resolve-DnsName -Name $name -Type A -Server $server -ErrorAction Stop | Out-Null
        }
        $sw.Stop()
        $elapsed = [math]::Round($sw.Elapsed.TotalMilliseconds, 1)
        $status = if ($elapsed -gt 750) { 'Warning' } else { 'OK' }
        $serverText = if ([string]::IsNullOrWhiteSpace($server)) { 'system resolver' } else { "DNS server $server" }
        return [pscustomobject]@{ Target = $label; Status = $status; LatencyMs = $elapsed; Details = "Resolved $name using $serverText." }
    }
    catch {
        return [pscustomobject]@{ Target = $label; Status = 'Critical'; LatencyMs = $null; Details = $_.Exception.Message }
    }
}

function Merge-Results($results, $target, $noneDetails) {
    if ($null -eq $results -or @($results).Count -eq 0) {
        return New-UnknownResult $target $noneDetails
    }

    $items = @($results)
    $criticalCount = @($items | Where-Object { $_.Status -eq 'Critical' }).Count
    $warningCount = @($items | Where-Object { $_.Status -eq 'Warning' }).Count
    $okCount = @($items | Where-Object { $_.Status -eq 'OK' }).Count
    $status = if ($okCount -eq 0 -and $criticalCount -gt 0) { 'Critical' } elseif ($criticalCount -gt 0 -or $warningCount -gt 0) { 'Warning' } else { 'OK' }
    $details = "$okCount OK, $warningCount warning, $criticalCount critical."
    return [pscustomobject]@{ Target = $target; Status = $status; LatencyMs = $null; Details = $details }
}

function Test-TcpPort($target, $port, $label) {
    try {
        $sw = [System.Diagnostics.Stopwatch]::StartNew()
        $client = [System.Net.Sockets.TcpClient]::new()
        $task = $client.ConnectAsync($target, $port)
        if (-not $task.Wait(3000)) {
            $client.Close()
            return [pscustomobject]@{ Target = $label; Status = 'Critical'; LatencyMs = $null; Details = "TCP $port to $target timed out." }
        }
        $sw.Stop()
        $status = if ($client.Connected) { 'OK' } else { 'Critical' }
        $details = if ($client.Connected) { "TCP $port to $target connected." } else { "TCP $port to $target failed." }
        $client.Close()
        return [pscustomobject]@{ Target = $label; Status = $status; LatencyMs = [math]::Round($sw.Elapsed.TotalMilliseconds, 1); Details = $details }
    }
    catch {
        return [pscustomobject]@{ Target = $label; Status = 'Critical'; LatencyMs = $null; Details = $_.Exception.Message }
    }
}

function Test-HttpsGet($uri, $label) {
    try {
        $sw = [System.Diagnostics.Stopwatch]::StartNew()
        $response = Invoke-WebRequest -Uri $uri -Method Get -UseBasicParsing -TimeoutSec 8 -ErrorAction Stop
        $sw.Stop()
        $code = [int]$response.StatusCode
        $status = if ($code -ge 200 -and $code -lt 400) { 'OK' } else { 'Warning' }
        return [pscustomobject]@{ Target = $label; Status = $status; LatencyMs = [math]::Round($sw.Elapsed.TotalMilliseconds, 1); Details = "HTTPS GET returned HTTP $code." }
    }
    catch {
        return [pscustomobject]@{ Target = $label; Status = 'Critical'; LatencyMs = $null; Details = $_.Exception.Message }
    }
}

function Test-CaptivePortal {
    # Windows NCSI: http://www.msftconnecttest.com/connecttest.txt must return EXACTLY
    # "Microsoft Connect Test". Anything else (redirect, login HTML, blank) = a portal
    # or filter is intercepting traffic even though L3 looks fine.
    try {
        $r = Invoke-WebRequest -Uri 'http://www.msftconnecttest.com/connecttest.txt' -UseBasicParsing -TimeoutSec 6 -MaximumRedirection 0 -ErrorAction Stop
        $code = [int]$r.StatusCode
        $body = ("$($r.Content)").Trim()
        if ($code -eq 200 -and $body -eq 'Microsoft Connect Test') {
            return [pscustomobject]@{ Target = 'Captive portal'; Status = 'OK'; LatencyMs = $null; Details = 'No portal - open internet (NCSI body matched).' }
        }
        return [pscustomobject]@{ Target = 'Captive portal'; Status = 'Critical'; LatencyMs = $null; Details = "Captive portal / interception likely (HTTP $code, unexpected body). Sign in to the network's portal page." }
    }
    catch {
        # A redirect throws here with -MaximumRedirection 0 - classic portal signature.
        $resp = $_.Exception.Response
        if ($resp -and [int]$resp.StatusCode -ge 300 -and [int]$resp.StatusCode -lt 400) {
            return [pscustomobject]@{ Target = 'Captive portal'; Status = 'Critical'; LatencyMs = $null; Details = 'Captive portal detected (HTTP redirect on the connectivity probe). Sign in to the network portal.' }
        }
        return [pscustomobject]@{ Target = 'Captive portal'; Status = 'Unknown'; LatencyMs = $null; Details = "Could not run portal probe: $($_.Exception.Message)" }
    }
}

$gateway = '{{safeGateway}}'
$dnsServers = {{dnsArrayLiteral}}
$dnsServerResults = @()
foreach ($dns in $dnsServers) {
    $dnsServerResults += Test-DnsLookup 'www.microsoft.com' "DNS server $dns" $dns
}
$internetPingResults = @(
    Test-PingTarget '1.1.1.1' '1.1.1.1'
    Test-PingTarget '8.8.8.8' '8.8.8.8'
    Test-PingTarget 'www.google.com' 'www.google.com'
)

[pscustomobject]@{
    Gateway = Test-PingTarget $gateway 'Gateway'
    DnsServer = Merge-Results $dnsServerResults 'DNS servers' 'No configured DNS servers available.'
    DnsServerResults = @($dnsServerResults)
    DnsResolution = Test-DnsLookup 'www.microsoft.com' 'External DNS lookup' ''
    InternetPing = Merge-Results @($internetPingResults | Where-Object { $_.Target -ne 'www.google.com' }) 'Internet IP pings' 'No internet IP targets tested.'
    InternetPingResults = @($internetPingResults)
    InternetDnsResolution = Test-DnsLookup 'www.google.com' 'Internet DNS lookup' ''
    ExternalTcp443 = Test-TcpPort 'www.google.com' 443 'External TCP 443'
    HttpsGet = Test-HttpsGet 'https://www.google.com/generate_204' 'HTTPS GET'
    CaptivePortal = Test-CaptivePortal
    GatewayPacketLoss = Test-PacketLoss $gateway 'Gateway'
    InternetPacketLoss = Test-PacketLoss '1.1.1.1' 'Internet IP'
} | ConvertTo-Json -Depth 6
""";

        var (data, execution) = await RunCollectorWithExecutionAsync<ConnectivityTests>(script, TimeSpan.FromSeconds(35), cancellationToken);
        if (data is not null)
        {
            return data;
        }

        var details = execution.TimedOut
            ? "DNS/internet collector timed out."
            : $"DNS/internet collector failed: {execution.Error}";
        return new ConnectivityTests
        {
            // CollectorStatus carries the failure so the engine records a warning and the
            // rule engine reports "diagnosis incomplete" instead of a false "healthy".
            CollectorStatus = details,
            Gateway = new TestResult { Target = "Gateway", Status = "Unknown", Details = details },
            DnsServer = new TestResult { Target = "DNS servers", Status = "Unknown", Details = details },
            DnsResolution = new TestResult { Target = "External DNS lookup", Status = "Unknown", Details = details },
            InternetPing = new TestResult { Target = "Internet IP pings", Status = "Unknown", Details = details },
            InternetDnsResolution = new TestResult { Target = "Internet DNS lookup", Status = "Unknown", Details = details },
            ExternalTcp443 = new TestResult { Target = "External TCP 443", Status = "Unknown", Details = details },
            HttpsGet = new TestResult { Target = "HTTPS GET", Status = "Unknown", Details = details }
        };
    }
}
