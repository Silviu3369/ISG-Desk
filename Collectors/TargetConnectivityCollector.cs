using NetScopeDiagnosticCenter.Core;
using NetScopeDiagnosticCenter.Core.Models;
using NetScopeDiagnosticCenter.Infrastructure;

namespace NetScopeDiagnosticCenter.Collectors;

public class TargetConnectivityCollector : JsonCollectorBase
{
    public TargetConnectivityCollector(PowerShellRunner powerShellRunner) : base(powerShellRunner)
    {
    }

    public virtual async Task<TargetProbeResult> ProbeTargetAsync(
        DiagnosticTarget target,
        IReadOnlyList<int> defaultPorts,
        CancellationToken cancellationToken = default)
    {
        var ports = (target.Ports.Count > 0 ? target.Ports : defaultPorts)
            .Distinct()
            .Where(port => port is > 0 and <= 65535)
            .Take(DiagnosticConstants.MaxTargetedTestPorts)
            .ToList();

        if (!DiagnosticTargetValidator.TryNormalizeHostOrShare(target.Host, out var normalizedHost, out var normalizedSharePath, out var validationReason))
        {
            var validationDetails = $"{validationReason} Targeted test probe was not started.";
            return new TargetProbeResult
            {
                Target = target,
                DnsStatus = "Unknown",
                PingStatus = "Unknown",
                PacketLoss = PacketLossResult.Unknown(target.Host ?? string.Empty),
                CollectorStatus = validationReason,
                Details = validationDetails,
                Ports = ports
                    .Select(port => new PortProbeResult
                    {
                        Port = port,
                        TcpSucceeded = false,
                        Details = validationDetails
                    })
                    .ToList()
            };
        }

        if (!string.IsNullOrWhiteSpace(target.SharePath))
        {
            if (!DiagnosticTargetValidator.TryNormalizeHostOrShare(target.SharePath, out var shareHost, out normalizedSharePath, out validationReason) ||
                string.IsNullOrWhiteSpace(normalizedSharePath))
            {
                var validationDetails = $"{validationReason} Share path probe was not started.";
                return new TargetProbeResult
                {
                    Target = target,
                    DnsStatus = "Unknown",
                    PingStatus = "Unknown",
                    PacketLoss = PacketLossResult.Unknown(target.Host ?? string.Empty),
                    CollectorStatus = validationReason,
                    Details = validationDetails,
                    ShareStatus = "Unknown",
                    ShareDetails = validationDetails,
                    Ports = ports
                        .Select(port => new PortProbeResult
                        {
                            Port = port,
                            TcpSucceeded = false,
                            Details = validationDetails
                        })
                        .ToList()
                };
            }

            if (!shareHost.Equals(normalizedHost, StringComparison.OrdinalIgnoreCase))
            {
                const string mismatch = "Share path host must match the target host.";
                return new TargetProbeResult
                {
                    Target = target,
                    DnsStatus = "Unknown",
                    PingStatus = "Unknown",
                    PacketLoss = PacketLossResult.Unknown(target.Host ?? string.Empty),
                    CollectorStatus = mismatch,
                    Details = mismatch,
                    ShareStatus = "Unknown",
                    ShareDetails = mismatch,
                    Ports = ports
                        .Select(port => new PortProbeResult
                        {
                            Port = port,
                            TcpSucceeded = false,
                            Details = mismatch
                        })
                        .ToList()
                };
            }
        }

        var safeHost = PsSingleQuote(normalizedHost);
        var safeSharePath = PsSingleQuote(normalizedSharePath);
        var safeName = PsSingleQuote(target.Name);
        var safePurpose = PsSingleQuote(target.Purpose);
        var portArray = string.Join(",", ports);

        var script = $$"""
$ErrorActionPreference = 'Continue'
$hostName = '{{safeHost}}'
$sharePath = '{{safeSharePath}}'
$targetName = '{{safeName}}'
$purpose = '{{safePurpose}}'
$ports = @({{portArray}})

function New-PacketLoss($target, $status, $sent, $received, $loss, $details) {
    [pscustomobject]@{ Target = $target; Status = $status; Sent = $sent; Received = $received; LossPercent = $loss; Details = $details }
}

$resolvedAddress = 'Unknown'
$dnsStatus = 'Unknown'
try {
    $ipAddress = $null
    if ([System.Net.IPAddress]::TryParse($hostName, [ref]$ipAddress)) {
        $resolvedAddress = $hostName
        $dnsStatus = 'OK'
    } else {
        $resolved = Resolve-DnsName -Name $hostName -ErrorAction Stop | Where-Object { $_.IPAddress } | Select-Object -First 1
        if ($resolved -and $resolved.IPAddress) {
            $resolvedAddress = [string]$resolved.IPAddress
            $dnsStatus = 'OK'
        } else {
            $dnsStatus = 'Unknown'
        }
    }
}
catch {
    $dnsStatus = 'Critical'
}

$sent = 4
$received = 0
$avg = $null
$pingStatus = 'Unknown'
try {
    $latencies = @()
    $ping = [System.Net.NetworkInformation.Ping]::new()
    for ($i = 0; $i -lt $sent; $i++) {
        $reply = $ping.Send($hostName, 1200)
        if ($reply.Status -eq [System.Net.NetworkInformation.IPStatus]::Success) {
            $received++
            $latencies += [double]$reply.RoundtripTime
        }
    }
    if ($latencies.Count -gt 0) { $avg = [math]::Round(($latencies | Measure-Object -Average).Average, 1) }
    $pingStatus = if ($received -eq $sent) { 'OK' } elseif ($received -gt 0) { 'Warning' } else { 'Critical' }
}
catch {
    $pingStatus = 'Critical'
}

$loss = if ($sent -gt 0) { [math]::Round((($sent - $received) / $sent) * 100, 1) } else { $null }
$lossStatus = if ($loss -eq $null) { 'Unknown' } elseif ($loss -eq 0) { 'OK' } elseif ($loss -le 5) { 'Warning' } else { 'Critical' }
$portResults = @()
foreach ($port in $ports) {
    try {
        $sw = [System.Diagnostics.Stopwatch]::StartNew()
        $client = [System.Net.Sockets.TcpClient]::new()
        $task = $client.ConnectAsync($hostName, [int]$port)
        $completed = $task.Wait(2500)
        $connected = $completed -and $client.Connected
        $sw.Stop()
        $client.Close()
        $details = if ($connected) { 'TCP port open.' } elseif (-not $completed) { 'TCP port timed out.' } else { 'TCP port closed, filtered, or service unavailable.' }
        $latency = if ($completed) { [math]::Round($sw.Elapsed.TotalMilliseconds, 1) } else { $null }
        $portResults += [pscustomobject]@{ Port = [int]$port; TcpSucceeded = [bool]$connected; LatencyMs = $latency; Details = $details }
    }
    catch {
        $portResults += [pscustomobject]@{ Port = [int]$port; TcpSucceeded = $false; LatencyMs = $null; Details = $_.Exception.Message }
    }
}

# Application-layer probe for web ports: a TCP-open 443 says "socket reachable";
# an HTTP status code says "the SERVICE answers". 401/403 still prove the app is alive.
foreach ($portResult in $portResults) {
    if (-not $portResult.TcpSucceeded) { continue }
    $portNumber = [int]$portResult.Port
    $scheme = $null
    if ($portNumber -eq 80 -or $portNumber -eq 8080) { $scheme = 'http' }
    elseif ($portNumber -eq 443 -or $portNumber -eq 8443) { $scheme = 'https' }
    if ($null -eq $scheme) { continue }

    $statusCode = $null
    $httpDetail = ''
    $previousCallback = [System.Net.ServicePointManager]::ServerCertificateValidationCallback
    try {
        # Internal servers commonly use self-signed certs - reachability matters here, not trust.
        [System.Net.ServicePointManager]::ServerCertificateValidationCallback = { $true }
        $request = [System.Net.HttpWebRequest]::Create(("{0}://{1}:{2}/" -f $scheme, $hostName, $portNumber))
        $request.Method = 'GET'
        $request.Timeout = 5000
        $request.ReadWriteTimeout = 5000
        $request.AllowAutoRedirect = $false
        $request.UserAgent = 'ISG-Desk-Diagnostics'
        $response = $request.GetResponse()
        $statusCode = [int]$response.StatusCode
        $httpDetail = "HTTP $statusCode $($response.StatusDescription)"
        $response.Close()
    }
    catch [System.Net.WebException] {
        $errorResponse = $_.Exception.Response
        if ($errorResponse) {
            $statusCode = [int]$errorResponse.StatusCode
            $httpDetail = "HTTP $statusCode $($errorResponse.StatusDescription) - service is answering"
            $errorResponse.Close()
        } else {
            $httpDetail = "HTTP probe failed: $($_.Exception.Message)"
        }
    }
    catch {
        $httpDetail = "HTTP probe failed: $($_.Exception.Message)"
    }
    finally {
        [System.Net.ServicePointManager]::ServerCertificateValidationCallback = $previousCallback
    }

    $portResult | Add-Member -NotePropertyName HttpStatusCode -NotePropertyValue $statusCode -Force
    $portResult | Add-Member -NotePropertyName HttpDetails -NotePropertyValue $httpDetail -Force
}

$shareStatus = 'Not tested'
$shareDetails = ''
if (-not [string]::IsNullOrWhiteSpace($sharePath)) {
    $smbResult = $portResults | Where-Object { $_.Port -eq 445 } | Select-Object -First 1
    if ($smbResult -and -not $smbResult.TcpSucceeded) {
        $shareStatus = 'Skipped'
        $shareDetails = 'Share path was not tested because TCP 445 failed.'
    } else {
        try {
            $item = Get-Item -LiteralPath $sharePath -ErrorAction Stop
            if ($item.PSIsContainer) {
                $shareStatus = 'OK'
                $shareDetails = 'UNC share path is reachable.'
            } else {
                $shareStatus = 'Critical'
                $shareDetails = 'UNC path exists but is not a folder/share.'
            }
        }
        catch [System.UnauthorizedAccessException] {
            $shareStatus = 'Warning'
            $shareDetails = 'UNC share exists but access was denied for the current user.'
        }
        catch {
            $shareStatus = 'Critical'
            $shareDetails = $_.Exception.Message
        }
    }
}

[pscustomobject]@{
    Target = [pscustomobject]@{ Name = $targetName; Host = $hostName; SharePath = $sharePath; Ports = @($ports); Purpose = $purpose }
    ResolvedAddress = $resolvedAddress
    DnsStatus = $dnsStatus
    PingStatus = $pingStatus
    AverageLatencyMs = $avg
    PacketLoss = New-PacketLoss $hostName $lossStatus $sent $received $loss "$loss% packet loss ($received/$sent replies)."
    Ports = $portResults
    ShareStatus = $shareStatus
    ShareDetails = $shareDetails
    CollectorStatus = 'OK'
    Details = if ([string]::IsNullOrWhiteSpace($sharePath)) { "DNS=$dnsStatus; Ping=$pingStatus; PacketLoss=$loss%." } else { "DNS=$dnsStatus; Ping=$pingStatus; PacketLoss=$loss%; Share=$shareStatus." }
} | ConvertTo-Json -Depth 8
""";

        var (data, execution) = await RunCollectorWithExecutionAsync<TargetProbeResult>(script, TimeSpan.FromSeconds(35), cancellationToken);
        if (data is not null)
        {
            await EnrichWithShareEnumerationAsync(data, normalizedHost, cancellationToken).ConfigureAwait(false);
            return data;
        }

        var details = execution.TimedOut
            ? "Target probe collector timed out."
            : $"Target probe collector failed: {execution.Error}";
        return new TargetProbeResult
        {
            Target = target,
            DnsStatus = "Unknown",
            PingStatus = "Unknown",
            CollectorStatus = details,
            Details = details,
            Ports = ports
                .Distinct()
                .Where(port => port > 0)
                .Select(port => new PortProbeResult
                {
                    Port = port,
                    TcpSucceeded = false,
                    Details = details
                })
                .ToList()
        };
    }

    private const int MaxVisibleSharesShown = 12;

    /// <summary>
    /// For file-server targets with SMB reachable, lists the shares the server actually
    /// publishes (NetShareEnum level 1) so the verdict can say "these shares exist"
    /// instead of only "445 is open". Best-effort: failures degrade to a status string,
    /// never to a probe failure.
    /// </summary>
    private async Task EnrichWithShareEnumerationAsync(
        TargetProbeResult probe,
        string normalizedHost,
        CancellationToken cancellationToken)
    {
        var isFileServerTarget = string.Equals(probe.Target.Purpose, "File server", StringComparison.OrdinalIgnoreCase);
        var smbOpen = probe.Ports.Any(port => port.Port == 445 && port.TcpSucceeded);
        if (!isFileServerTarget || !smbOpen)
        {
            return;
        }

        var enumeration = await EnumerateSharesAsync(normalizedHost, cancellationToken).ConfigureAwait(false);
        probe.ShareEnumStatus = enumeration.Status;
        probe.ShareEnumDetails = enumeration.Detail;
        probe.VisibleShares = enumeration.Shares
            .Select(share => share.DisplayLabel)
            .Take(MaxVisibleSharesShown)
            .ToList();
        if (enumeration.Shares.Count > MaxVisibleSharesShown)
        {
            probe.VisibleShares.Add($"+{enumeration.Shares.Count - MaxVisibleSharesShown} more");
        }
    }

    /// <summary>Virtual seam so unit tests can fake share lists without touching netapi32.</summary>
    protected virtual Task<SmbShareEnumerationResult> EnumerateSharesAsync(string host, CancellationToken cancellationToken) =>
        SmbShareEnumerator.EnumerateAsync(host, TimeSpan.FromSeconds(8), cancellationToken);
}
