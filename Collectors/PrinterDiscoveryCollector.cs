using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using NetScopeDiagnosticCenter.Collectors.Shared;
using NetScopeDiagnosticCenter.Core;
using NetScopeDiagnosticCenter.Core.Models;
using NetScopeDiagnosticCenter.Infrastructure;

namespace NetScopeDiagnosticCenter.Collectors;

public class PrinterDiscoveryCollector : JsonCollectorBase
{
    private const int DefaultMaxHosts = DiagnosticConstants.MaxPrinterScanHosts;
    private static readonly TimeSpan PortTimeout = TimeSpan.FromMilliseconds(220);
    private static readonly TimeSpan ReverseDnsTimeout = TimeSpan.FromMilliseconds(350);

    public PrinterDiscoveryCollector(PowerShellRunner powerShellRunner) : base(powerShellRunner)
    {
    }

    public virtual async Task<PrinterDiscoveryResult> ScanLocalSubnetAsync(
        PrinterNetworkAdapterInfo? preferredAdapter,
        CancellationToken cancellationToken = default)
    {
        var local = preferredAdapter is not null
            ? BuildLocalRange(preferredAdapter)
            : await TryGetDefaultRouteScanRangeAsync(cancellationToken);
        if (!string.IsNullOrWhiteSpace(local.Error))
        {
            return new PrinterDiscoveryResult
            {
                Mode = "LocalSubnetScan",
                Verdict = local.Error,
                Severity = "Warning",
                MaxHosts = DefaultMaxHosts,
                Evidence = [local.Error],
                Limitations = ["Printer scan did not run because no usable local IPv4 configuration was available."]
            };
        }

        var request = new PrinterScanRequest
        {
            Input = local.Range,
            MaxHosts = DefaultMaxHosts
        };

        return await ScanRangeAsync(request, cancellationToken, local.Evidence, local);
    }

    public virtual async Task<IReadOnlyList<PrinterNetworkAdapterInfo>> DetectLocalAdaptersAsync(
        CancellationToken cancellationToken = default)
    {
        var adapters = await TryGetDefaultRouteAdaptersAsync(cancellationToken);
        if (adapters.Count > 0)
        {
            return adapters;
        }

        var fallback = TryGetLocalScanRange();
        if (!string.IsNullOrWhiteSpace(fallback.Error))
        {
            return [];
        }

        return
        [
            new PrinterNetworkAdapterInfo
            {
                LocalIpAddress = fallback.LocalIpAddress,
                AdapterName = fallback.LocalAdapterName,
                PrefixLength = fallback.PrefixLength,
                Gateway = fallback.Gateway,
                RouteMetric = fallback.RouteMetric,
                InterfaceMetric = fallback.InterfaceMetric,
                DetectedSubnet = fallback.DetectedSubnet,
                DetectedSubnetHosts = fallback.DetectedSubnetHosts,
                SafeScanRange = fallback.SafeScanRange,
                Warning = fallback.Warning
            }
        ];
    }

    public virtual async Task<PrinterDiscoveryResult> ScanRangeAsync(
        PrinterScanRequest request,
        CancellationToken cancellationToken = default)
    {
        return await ScanRangeAsync(request, cancellationToken, []);
    }

    private async Task<PrinterDiscoveryResult> ScanRangeAsync(
        PrinterScanRequest request,
        CancellationToken cancellationToken,
        IReadOnlyList<string> initialEvidence,
        LocalRangeResult? localRange = null)
    {
        var maxHosts = request.MaxHosts <= 0 ? DefaultMaxHosts : Math.Min(request.MaxHosts, DefaultMaxHosts);
        var evidence = new List<string>(initialEvidence);
        var resolveInput = request.Input;
        LocalRangeResult? detectedLocal = null;
        if (string.IsNullOrWhiteSpace(resolveInput))
        {
            detectedLocal = TryGetLocalScanRange();
            if (!string.IsNullOrWhiteSpace(detectedLocal.Error))
            {
                evidence.AddRange(detectedLocal.Evidence);
                return new PrinterDiscoveryResult
                {
                    Mode = "RangeScan",
                    Source = request.Input,
                    Verdict = detectedLocal.Error,
                    Severity = "Warning",
                    MaxHosts = maxHosts,
                    SkippedReason = detectedLocal.Error,
                    LocalIpAddress = detectedLocal.LocalIpAddress,
                    LocalAdapterName = detectedLocal.LocalAdapterName,
                    LocalPrefixLength = detectedLocal.PrefixLength,
                    Evidence = evidence,
                    Limitations =
                    [
                        "Scan was not started because the target range is invalid or larger than the production safety limit.",
                        "NetScope v1 does not scan networks larger than 254 IP addresses automatically."
                    ]
                };
            }

            evidence.AddRange(detectedLocal.Evidence);
            resolveInput = detectedLocal.Range;
        }

        var parse = IpRangeParser.TryResolveHosts(resolveInput, maxHosts);
        evidence.AddRange(parse.Evidence);
        var localIpAddress = FirstNonEmpty(localRange?.LocalIpAddress, detectedLocal?.LocalIpAddress);
        var localAdapterName = FirstNonEmpty(localRange?.LocalAdapterName, detectedLocal?.LocalAdapterName);
        var localPrefixLength = localRange?.PrefixLength ?? detectedLocal?.PrefixLength;
        var detectedSubnet = FirstNonEmpty(localRange?.DetectedSubnet, detectedLocal?.DetectedSubnet, parse.DetectedSubnet);
        var detectedSubnetHosts = localRange?.DetectedSubnetHosts > 0
            ? localRange.DetectedSubnetHosts
            : detectedLocal is not null && detectedLocal.DetectedSubnetHosts > 0
                ? detectedLocal.DetectedSubnetHosts
                : parse.DetectedSubnetHosts;
        var safeScanRange = FirstNonEmpty(localRange?.SafeScanRange, detectedLocal?.SafeScanRange, parse.SafeScanRange);

        if (!string.IsNullOrWhiteSpace(parse.Error))
        {
            return new PrinterDiscoveryResult
            {
                Mode = "RangeScan",
                Source = request.Input,
                Verdict = parse.Error,
                Severity = "Warning",
                EstimatedHosts = parse.EstimatedHosts,
                MaxHosts = maxHosts,
                SkippedReason = parse.Error,
                LocalIpAddress = localIpAddress,
                LocalAdapterName = localAdapterName,
                LocalPrefixLength = localPrefixLength,
                Gateway = localRange?.Gateway ?? string.Empty,
                RouteMetric = localRange?.RouteMetric ?? 0,
                InterfaceMetric = localRange?.InterfaceMetric ?? 0,
                DetectedSubnet = detectedSubnet,
                DetectedSubnetHosts = detectedSubnetHosts,
                SafeScanRange = safeScanRange,
                Evidence = evidence,
                Limitations =
                [
                    "Scan was not started because the target range is invalid or larger than the production safety limit.",
                    "NetScope v1 does not scan networks larger than 254 IP addresses automatically."
                ]
            };
        }

        var ports = request.Ports.Count > 0 ? request.Ports.Distinct().ToArray() : DiagnosticConstants.PrinterPorts.ToArray();
        evidence.Add($"Scanning {parse.Source} for printer-related TCP ports: {string.Join(", ", ports)}.");
        evidence.Add($"Scan host count: {parse.Hosts.Count}; safety limit: {maxHosts}.");

        var results = new List<PrinterScanResult>();
        using var concurrency = new SemaphoreSlim(48);
        var tasks = parse.Hosts.Select(async address =>
        {
            await concurrency.WaitAsync(cancellationToken);
            try
            {
                var result = await ScanHostAsync(address, ports, cancellationToken);
                if (result is not null)
                {
                    lock (results)
                    {
                        results.Add(result);
                    }
                }
            }
            finally
            {
                concurrency.Release();
            }
        });

        await Task.WhenAll(tasks);

        var orderedResults = results
            .OrderBy(result => ToUInt32(IPAddress.Parse(result.Address)))
            .ToList();

        var verdict = orderedResults.Count > 0
            ? $"Found {orderedResults.Count} devices with printer-related ports."
            : $"No printer-related ports found in {parse.Source}.";

        return new PrinterDiscoveryResult
        {
            Mode = "RangeScan",
            Source = parse.Source,
            Verdict = verdict,
            Severity = orderedResults.Count > 0 ? "OK" : "Warning",
            EstimatedHosts = parse.Hosts.Count,
            MaxHosts = maxHosts,
            LocalIpAddress = localIpAddress,
            LocalAdapterName = localAdapterName,
            LocalPrefixLength = localPrefixLength,
            Gateway = localRange?.Gateway ?? string.Empty,
            RouteMetric = localRange?.RouteMetric ?? 0,
            InterfaceMetric = localRange?.InterfaceMetric ?? 0,
            DetectedSubnet = detectedSubnet,
            DetectedSubnetHosts = detectedSubnetHosts,
            SafeScanRange = safeScanRange,
            Evidence = evidence,
            Limitations =
            [
                "TCP port scan only indicates likely printer services.",
                "SNMP identification is required to confirm model, name or device identity.",
                "The scan is read-only and does not install or modify printers."
            ],
            ScanResults = orderedResults
        };
    }

    private static async Task<PrinterScanResult?> ScanHostAsync(
        IPAddress address,
        IReadOnlyList<int> ports,
        CancellationToken cancellationToken)
    {
        var openPorts = new List<int>();
        foreach (var port in ports)
        {
            if (await IsTcpOpenAsync(address.ToString(), port, cancellationToken))
            {
                openPorts.Add(port);
            }
        }

        if (openPorts.Count == 0)
        {
            return null;
        }

        var hostName = await TryReverseDnsAsync(address, cancellationToken);
        // Only printer-protocol ports are scanned (9100/515/631), so every row here is a
        // host speaking a print protocol — never a router/NAS that merely has a web UI.
        var hasPrintPort = openPorts.Contains(9100) || openPorts.Contains(515) || openPorts.Contains(631);
        var classification = hasPrintPort ? "Likely printer" : "Unknown device";
        var confidence = hasPrintPort ? "High" : "Low";
        var reason = hasPrintPort
            ? "Printer protocol port is open."
            : "No printer protocol port answered; SNMP is needed to confirm identity.";

        return new PrinterScanResult
        {
            Address = address.ToString(),
            HostName = hostName,
            OpenPorts = openPorts,
            Classification = classification,
            Confidence = confidence,
            Reason = reason,
            Details = $"Open ports: {string.Join(", ", openPorts)}"
        };
    }

    private static async Task<bool> IsTcpOpenAsync(string address, int port, CancellationToken cancellationToken)
    {
        using var client = new TcpClient();
        try
        {
            // Use the .NET 5+ ConnectAsync(EndPoint, CancellationToken) overload so the
            // underlying socket connect actually aborts on cancel/timeout — the old
            // .WaitAsync(...) merely returned to the caller while the socket kept
            // dialing until the OS gave up, leaking 1270 fds per cancelled /24 scan.
            using var ctsTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            ctsTimeout.CancelAfter(PortTimeout);
            await client.ConnectAsync(address, port, ctsTimeout.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // True scan-wide cancellation — propagate.
            throw;
        }
        catch
        {
            // Per-port timeout / connect refusal — host is just unreachable on this port.
            return false;
        }
    }

    private static async Task<string> TryReverseDnsAsync(IPAddress address, CancellationToken cancellationToken)
    {
        try
        {
            var entry = await Dns.GetHostEntryAsync(address).WaitAsync(ReverseDnsTimeout, cancellationToken);
            return entry.HostName;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static LocalRangeResult TryGetLocalScanRange()
    {
        foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (networkInterface.OperationalStatus != OperationalStatus.Up ||
                networkInterface.NetworkInterfaceType == NetworkInterfaceType.Loopback)
            {
                continue;
            }

            var properties = networkInterface.GetIPProperties();
            foreach (var unicast in properties.UnicastAddresses)
            {
                if (unicast.Address.AddressFamily != AddressFamily.InterNetwork ||
                    IsApipa(unicast.Address) ||
                    !IsPrivateIpv4(unicast.Address))
                {
                    continue;
                }

                var ip = unicast.Address;
                var prefix = unicast.PrefixLength;
                var actualNetwork = ToUInt32(ip) & Mask(prefix);
                var detectedSubnet = $"{UInt32ToIp(actualNetwork)}/{prefix}";
                var detectedHostCount = EstimateUsableHosts(prefix);
                var safeNetwork = prefix < 24 ? ToUInt32(ip) & Mask(24) : actualNetwork;
                var safePrefix = prefix < 24 ? 24 : prefix;
                var range = $"{UInt32ToIp(safeNetwork)}/{safePrefix}";
                var evidence = new List<string>
                {
                    $"Detected local IPv4 {ip} on adapter {networkInterface.Name}.",
                    $"Local adapter prefix length is /{prefix}.",
                    $"Detected subnet is {detectedSubnet} with approximately {detectedHostCount:N0} usable hosts.",
                    $"Auto safe scan range is {range}."
                };

                if (prefix < 24)
                {
                    evidence.Add("Detected network is larger than /24; NetScope will scan only a 254-host safe window around this PC unless a safe custom range is entered.");
                }

                return new LocalRangeResult(
                    range,
                    ip.ToString(),
                    networkInterface.Name,
                    0,
                    prefix,
                    string.Empty,
                    0,
                    0,
                    detectedSubnet,
                    detectedHostCount,
                    range,
                    prefix < 24 ? "Detected network is larger than /24; NetScope will scan only a 254-host safe window around this PC unless a safe custom range is entered." : string.Empty,
                    evidence,
                    string.Empty);
            }
        }

        return new LocalRangeResult(string.Empty, string.Empty, string.Empty, 0, null, string.Empty, 0, 0, string.Empty, 0, string.Empty, string.Empty, [], "Local IPv4 subnet could not be detected.");
    }

    private async Task<LocalRangeResult> TryGetDefaultRouteScanRangeAsync(CancellationToken cancellationToken)
    {
        var adapters = await TryGetDefaultRouteAdaptersAsync(cancellationToken);
        if (adapters.Count > 0)
        {
            return BuildLocalRange(adapters[0]);
        }

        return TryGetLocalScanRange();
    }

    private async Task<List<PrinterNetworkAdapterInfo>> TryGetDefaultRouteAdaptersAsync(CancellationToken cancellationToken)
    {
        const string script = """
$ErrorActionPreference = 'SilentlyContinue'
$candidates = @()
$routes = @(Get-NetRoute -DestinationPrefix '0.0.0.0/0' -AddressFamily IPv4 |
    Where-Object { $_.NextHop -and $_.NextHop -ne '0.0.0.0' } |
    Sort-Object @{ Expression = { $_.RouteMetric + $_.InterfaceMetric } }, InterfaceIndex)

foreach ($route in $routes) {
    $adapter = Get-NetAdapter -InterfaceIndex $route.InterfaceIndex -ErrorAction SilentlyContinue
    if ($null -eq $adapter -or $adapter.Status -ne 'Up') { continue }
    if ($adapter.Name -match 'Loopback|vEthernet|Hyper-V|VirtualBox|VMware|Docker|WSL|TAP|TUN') { continue }

    $ip = Get-NetIPAddress -InterfaceIndex $route.InterfaceIndex -AddressFamily IPv4 -ErrorAction SilentlyContinue |
        Where-Object { $_.IPAddress -notlike '169.254.*' -and $_.PrefixLength -gt 0 } |
        Sort-Object PrefixOrigin |
        Select-Object -First 1
    if ($null -eq $ip) { continue }

    $candidates += [pscustomobject]@{
        LocalIpAddress = [string]$ip.IPAddress
        AdapterName = [string]$adapter.Name
        InterfaceIndex = [int]$route.InterfaceIndex
        PrefixLength = [int]$ip.PrefixLength
        Gateway = [string]$route.NextHop
        RouteMetric = [int]$route.RouteMetric
        InterfaceMetric = [int]$route.InterfaceMetric
    }
}

if ($candidates.Count -eq 0) {
    $routes = @(Get-NetRoute -DestinationPrefix '0.0.0.0/0' -AddressFamily IPv4 |
        Where-Object { $_.NextHop -and $_.NextHop -ne '0.0.0.0' } |
        Sort-Object @{ Expression = { $_.RouteMetric + $_.InterfaceMetric } }, InterfaceIndex)
    foreach ($route in $routes) {
        $adapter = Get-NetAdapter -InterfaceIndex $route.InterfaceIndex -ErrorAction SilentlyContinue
        if ($null -eq $adapter -or $adapter.Status -ne 'Up') { continue }
        $ip = Get-NetIPAddress -InterfaceIndex $route.InterfaceIndex -AddressFamily IPv4 -ErrorAction SilentlyContinue |
            Where-Object { $_.IPAddress -notlike '169.254.*' -and $_.PrefixLength -gt 0 } |
            Select-Object -First 1
        if ($null -eq $ip) { continue }
        $candidates += [pscustomobject]@{
            LocalIpAddress = [string]$ip.IPAddress
            AdapterName = [string]$adapter.Name
            InterfaceIndex = [int]$route.InterfaceIndex
            PrefixLength = [int]$ip.PrefixLength
            Gateway = [string]$route.NextHop
            RouteMetric = [int]$route.RouteMetric
            InterfaceMetric = [int]$route.InterfaceMetric
        }
    }
}

ConvertTo-Json -InputObject $candidates -Depth 5
""";

        var (data, _) = await RunCollectorWithExecutionAsync<List<PrinterNetworkAdapterInfo>>(
            script,
            TimeSpan.FromSeconds(8),
            cancellationToken);
        if (data is null || data.Count == 0)
        {
            return [];
        }

        foreach (var adapter in data)
        {
            EnrichAdapterScanRange(adapter);
        }

        return data
            .Where(adapter => IPAddress.TryParse(adapter.LocalIpAddress, out var address) && IsPrivateIpv4(address))
            .OrderBy(adapter => adapter.TotalMetric)
            .ThenBy(adapter => adapter.InterfaceIndex)
            .ToList();
    }

    private static LocalRangeResult BuildLocalRange(PrinterNetworkAdapterInfo adapter)
    {
        if (!IPAddress.TryParse(adapter.LocalIpAddress, out var ip))
        {
            return new LocalRangeResult(string.Empty, string.Empty, string.Empty, 0, null, string.Empty, 0, 0, string.Empty, 0, string.Empty, string.Empty, [], "Selected adapter does not have a valid IPv4 address.");
        }

        if (!IsPrivateIpv4(ip))
        {
            return new LocalRangeResult(string.Empty, adapter.LocalIpAddress, adapter.AdapterName, adapter.InterfaceIndex, adapter.PrefixLength, adapter.Gateway, adapter.RouteMetric, adapter.InterfaceMetric, string.Empty, 0, string.Empty, string.Empty, [], "Selected adapter IPv4 address is not private. Public IP scanning is not allowed.");
        }

        EnrichAdapterScanRange(adapter);
        var evidence = new List<string>
        {
            $"Detected local IPv4 {adapter.LocalIpAddress} on adapter {adapter.AdapterName}.",
            $"Default gateway is {adapter.Gateway}; route metric {adapter.RouteMetric}; interface metric {adapter.InterfaceMetric}.",
            $"Local adapter prefix length is /{adapter.PrefixLength}.",
            $"Detected subnet is {adapter.DetectedSubnet} with approximately {adapter.DetectedSubnetHosts:N0} usable hosts.",
            $"Auto safe scan range is {adapter.SafeScanRange}."
        };

        if (!string.IsNullOrWhiteSpace(adapter.Warning))
        {
            evidence.Add(adapter.Warning);
        }

        return new LocalRangeResult(
            adapter.SafeScanRange,
            adapter.LocalIpAddress,
            adapter.AdapterName,
            adapter.InterfaceIndex,
            adapter.PrefixLength,
            adapter.Gateway,
            adapter.RouteMetric,
            adapter.InterfaceMetric,
            adapter.DetectedSubnet,
            adapter.DetectedSubnetHosts,
            adapter.SafeScanRange,
            adapter.Warning,
            evidence,
            string.Empty);
    }

    private static void EnrichAdapterScanRange(PrinterNetworkAdapterInfo adapter)
    {
        if (!IPAddress.TryParse(adapter.LocalIpAddress, out var ip) || adapter.PrefixLength is null)
        {
            return;
        }

        var prefix = adapter.PrefixLength.Value;
        var actualNetwork = ToUInt32(ip) & Mask(prefix);
        var safeNetwork = prefix < 24 ? ToUInt32(ip) & Mask(24) : actualNetwork;
        var safePrefix = prefix < 24 ? 24 : prefix;
        adapter.DetectedSubnet = $"{UInt32ToIp(actualNetwork)}/{prefix}";
        adapter.DetectedSubnetHosts = EstimateUsableHosts(prefix);
        adapter.SafeScanRange = $"{UInt32ToIp(safeNetwork)}/{safePrefix}";
        adapter.Warning = prefix < 24
            ? "Detected network is larger than /24; NetScope will scan only a 254-host safe window around this PC unless a safe custom range is entered."
            : string.Empty;
    }

    private static int EstimateUsableHosts(int prefix) => NetworkSafetyPolicy.EstimateUsableHosts(prefix);
    private static string FirstNonEmpty(params string?[] values) => DiagnosticHelpers.FirstNonEmpty(values!);
    private static uint Mask(int prefix) => NetworkSafetyPolicy.Mask(prefix);
    private static uint ToUInt32(IPAddress address) => NetworkSafetyPolicy.ToUInt32(address);
    private static IPAddress UInt32ToIp(uint value) => NetworkSafetyPolicy.FromUInt32(value);
    private static bool IsApipa(IPAddress address) => NetworkSafetyPolicy.IsApipa(address);
    private static bool IsPrivateIpv4(IPAddress address) => NetworkSafetyPolicy.IsPrivateIpv4(address);

    private sealed record LocalRangeResult(
        string Range,
        string LocalIpAddress,
        string LocalAdapterName,
        int InterfaceIndex,
        int? PrefixLength,
        string Gateway,
        int RouteMetric,
        int InterfaceMetric,
        string DetectedSubnet,
        int DetectedSubnetHosts,
        string SafeScanRange,
        string Warning,
        IReadOnlyList<string> Evidence,
        string Error);

}
