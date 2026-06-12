using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using NetScopeDiagnosticCenter.Collectors.Shared;
using NetScopeDiagnosticCenter.Core;
using NetScopeDiagnosticCenter.Core.Models;
using NetScopeDiagnosticCenter.Infrastructure;

namespace NetScopeDiagnosticCenter.Collectors;

public class NetworkDeviceCollector : JsonCollectorBase
{
    private const int DefaultMaxScanHosts = DiagnosticConstants.MaxNetworkDeviceScanHosts;
    private const double HighInterfaceUtilizationPercent = 80;
    private static readonly TimeSpan PingTimeout = TimeSpan.FromMilliseconds(900);
    private static readonly TimeSpan TcpTimeout = TimeSpan.FromMilliseconds(900);
    private static readonly TimeSpan ReverseDnsTimeout = TimeSpan.FromMilliseconds(700);
    private static readonly TimeSpan InterfaceSampleInterval = TimeSpan.FromSeconds(5);
    private static readonly int[] ManagementTcpPorts = DiagnosticConstants.ManagementPorts.ToArray();

    private readonly SnmpClientService _snmpClient;

    private static readonly string[] IdentityOids =
    [
        SysDescr,
        SysUpTime,
        SysContact,
        SysName,
        SysLocation
    ];

    private const string SysDescr = "1.3.6.1.2.1.1.1.0";
    private const string SysUpTime = "1.3.6.1.2.1.1.3.0";
    private const string SysContact = "1.3.6.1.2.1.1.4.0";
    private const string SysName = "1.3.6.1.2.1.1.5.0";
    private const string SysLocation = "1.3.6.1.2.1.1.6.0";

    private const string IfIndex = "1.3.6.1.2.1.2.2.1.1";
    private const string IfDescr = "1.3.6.1.2.1.2.2.1.2";
    private const string IfSpeed = "1.3.6.1.2.1.2.2.1.5";
    private const string IfAdminStatus = "1.3.6.1.2.1.2.2.1.7";
    private const string IfOperStatus = "1.3.6.1.2.1.2.2.1.8";
    private const string IfInOctets = "1.3.6.1.2.1.2.2.1.10";
    private const string IfInDiscards = "1.3.6.1.2.1.2.2.1.13";
    private const string IfInErrors = "1.3.6.1.2.1.2.2.1.14";
    private const string IfOutOctets = "1.3.6.1.2.1.2.2.1.16";
    private const string IfOutDiscards = "1.3.6.1.2.1.2.2.1.19";
    private const string IfOutErrors = "1.3.6.1.2.1.2.2.1.20";
    private const string IfName = "1.3.6.1.2.1.31.1.1.1.1";
    private const string IfHighSpeed = "1.3.6.1.2.1.31.1.1.1.15";
    // 64-bit high-capacity octet counters (ifXTable). 32-bit ifInOctets/ifOutOctets wrap
    // at ~4.29 GB — on a gigabit link that can happen inside the 5 s sample window,
    // making the delta read as 0. Prefer these; fall back to 32-bit per interface.
    private const string IfHCInOctets = "1.3.6.1.2.1.31.1.1.1.6";
    private const string IfHCOutOctets = "1.3.6.1.2.1.31.1.1.1.10";

    public NetworkDeviceCollector(PowerShellRunner powerShellRunner, SnmpClientService snmpClient)
        : base(powerShellRunner)
    {
        _snmpClient = snmpClient;
    }

    public virtual async Task<NetworkDeviceResult> IdentifyAsync(
        SnmpSessionOptions options,
        CancellationToken cancellationToken = default)
    {
        return await CollectAsync(options, includeInterfaces: false, includeDiagnostics: true, cancellationToken);
    }

    public virtual async Task<NetworkDeviceResult> ReadInterfacesAsync(
        SnmpSessionOptions options,
        CancellationToken cancellationToken = default)
    {
        return await CollectAsync(options, includeInterfaces: true, includeDiagnostics: true, cancellationToken);
    }

    public virtual async Task<NetworkScanRangeInfo> DetectLocalScanRangeAsync(
        CancellationToken cancellationToken = default)
    {
        var defaultRoute = await TryGetDefaultRouteScanRangeAsync(cancellationToken);
        return string.IsNullOrWhiteSpace(defaultRoute.Error)
            ? defaultRoute
            : TryGetLocalScanRange();
    }

    public virtual async Task<NetworkDeviceScanResult> ScanLocalSubnetAsync(
        SnmpSessionOptions options,
        CancellationToken cancellationToken = default,
        Action<NetworkDeviceScanResult>? progress = null)
    {
        var local = await DetectLocalScanRangeAsync(cancellationToken);
        if (!string.IsNullOrWhiteSpace(local.Error))
        {
            return new NetworkDeviceScanResult
            {
                Source = string.Empty,
                Verdict = local.Error,
                Severity = "Warning",
                MaxHosts = DefaultMaxScanHosts,
                SnmpProtocol = options.Protocol,
                SnmpCommunityMasked = MaskCredential(options),
                Evidence = [local.Error],
                Limitations = ["LAN scan did not run because no usable private local IPv4 configuration was available."]
            };
        }

        return await ScanRangeAsync(local.ScanRange, options, cancellationToken, local.Evidence, local, progress);
    }

    public virtual async Task<NetworkDeviceScanResult> ScanRangeAsync(
        string rangeInput,
        SnmpSessionOptions options,
        CancellationToken cancellationToken = default,
        Action<NetworkDeviceScanResult>? progress = null)
    {
        return await ScanRangeAsync(rangeInput, options, cancellationToken, [], null, progress);
    }

    private async Task<NetworkDeviceScanResult> ScanRangeAsync(
        string rangeInput,
        SnmpSessionOptions options,
        CancellationToken cancellationToken,
        IReadOnlyList<string> initialEvidence,
        NetworkScanRangeInfo? localRange,
        Action<NetworkDeviceScanResult>? progress)
    {
        var evidence = new List<string>(initialEvidence);
        var resolveInput = rangeInput;
        if (string.IsNullOrWhiteSpace(resolveInput))
        {
            var localDet = TryGetLocalScanRange();
            if (!string.IsNullOrWhiteSpace(localDet.Error))
            {
                evidence.AddRange(localDet.Evidence);
                return new NetworkDeviceScanResult
                {
                    Source = rangeInput,
                    Verdict = localDet.Error,
                    Severity = "Warning",
                    MaxHosts = DefaultMaxScanHosts,
                    SkippedReason = localDet.Error,
                    SnmpProtocol = options.Protocol,
                    SnmpCommunityMasked = MaskCredential(options),
                    Evidence = evidence,
                    Limitations =
                    [
                        "Scan was not started because the target range is invalid, public, or larger than the production safety limit.",
                        "NetScope does not scan networks larger than 256 IP addresses automatically."
                    ]
                };
            }

            resolveInput = localDet.ScanRange;
        }

        var parse = IpRangeParser.TryResolveHosts(resolveInput, DefaultMaxScanHosts);
        evidence.AddRange(parse.Evidence);

        if (!string.IsNullOrWhiteSpace(parse.Error))
        {
            return new NetworkDeviceScanResult
            {
                Source = rangeInput,
                Verdict = parse.Error,
                Severity = "Warning",
                EstimatedHosts = parse.EstimatedHosts,
                MaxHosts = DefaultMaxScanHosts,
                SkippedReason = parse.Error,
                SnmpProtocol = options.Protocol,
                SnmpCommunityMasked = MaskCredential(options),
                Evidence = evidence,
                LocalIpAddress = localRange?.LocalIpAddress ?? string.Empty,
                AdapterName = localRange?.AdapterName ?? string.Empty,
                Gateway = localRange?.Gateway ?? string.Empty,
                RouteMetric = localRange?.RouteMetric ?? 0,
                InterfaceMetric = localRange?.InterfaceMetric ?? 0,
                DetectedSubnet = localRange?.DetectedSubnet ?? string.Empty,
                SafeScanRange = localRange?.SafeScanRange ?? string.Empty,
                Limitations =
                [
                    "Scan was not started because the target range is invalid, public, or larger than the production safety limit.",
                    "NetScope does not scan networks larger than 256 IP addresses automatically."
                ]
            };
        }

        evidence.Add($"Discovery scan of {parse.Source}: ping sweep + neighbor table + names + signature ports, then {options.Protocol} identity where available.");
        evidence.Add($"Scan host count: {parse.Hosts.Count}; safety limit: {DefaultMaxScanHosts}.");

        var scanResult = new NetworkDeviceScanResult
        {
            Source = parse.Source,
            Verdict = $"Scanning {parse.Source}...",
            Severity = "Unknown",
            EstimatedHosts = parse.Hosts.Count,
            MaxHosts = DefaultMaxScanHosts,
            SnmpProtocol = options.Protocol,
            SnmpCommunityMasked = MaskCredential(options),
            Evidence = evidence,
            LocalIpAddress = localRange?.LocalIpAddress ?? string.Empty,
            AdapterName = localRange?.AdapterName ?? string.Empty,
            Gateway = localRange?.Gateway ?? string.Empty,
            RouteMetric = localRange?.RouteMetric ?? 0,
            InterfaceMetric = localRange?.InterfaceMetric ?? 0,
            DetectedSubnet = localRange?.DetectedSubnet ?? string.Empty,
            SafeScanRange = localRange?.SafeScanRange ?? string.Empty,
            IsRunning = true,
            Limitations =
            [
                "Device types without SNMP identity are heuristic (ports, MAC vendor, names) — treat 'Probably …' labels as hints.",
                "Hosts that block ping AND have no ARP entry cannot be discovered.",
                "Physical location cannot be detected from the network: it comes from SNMP sysLocation or the technician's saved label.",
                "The scan is read-only and limited to 256 private IPv4 addresses."
            ]
        };

        var sync = new object();
        var devices = new List<NetworkDeviceResult>();
        var scannedHosts = 0;
        var timedOutHosts = 0;
        var failedHosts = 0;
        progress?.Invoke(Snapshot(scanResult));

        // ---- Tier 1: parallel ping sweep. Finds every live host (and primes the OS
        // ARP cache so the neighbor table below knows their MAC addresses). ----
        var reachable = new HashSet<string>(StringComparer.Ordinal);
        using (var sweepConcurrency = new SemaphoreSlim(48))
        {
            var sweepTasks = parse.Hosts.Select(async address =>
            {
                await sweepConcurrency.WaitAsync(cancellationToken);
                var shouldPublish = false;
                try
                {
                    var ping = await ProbePingAsync(address, samples: 1, cancellationToken);
                    lock (sync)
                    {
                        scannedHosts++;
                        if (ping.PacketLoss.Received > 0)
                        {
                            reachable.Add(address.ToString());
                        }

                        UpdateScanProgress(scanResult, devices, scannedHosts, timedOutHosts, failedHosts, isRunning: true);
                        shouldPublish = scannedHosts == 1 || scannedHosts % 16 == 0 || scannedHosts == parse.Hosts.Count;
                    }
                }
                catch (Exception) when (!cancellationToken.IsCancellationRequested)
                {
                    lock (sync)
                    {
                        scannedHosts++;
                        failedHosts++;
                        UpdateScanProgress(scanResult, devices, scannedHosts, timedOutHosts, failedHosts, isRunning: true);
                    }
                }
                finally
                {
                    sweepConcurrency.Release();
                }

                if (shouldPublish)
                {
                    progress?.Invoke(Snapshot(scanResult));
                }
            });
            await Task.WhenAll(sweepTasks);
        }

        // One neighbor-table readout for the whole range (instead of 254 per-host calls):
        // picks up MACs for every swept host, including ones that block ICMP.
        var neighbors = await ReadNeighborTableAsync(cancellationToken);
        var liveHosts = parse.Hosts
            .Where(address => reachable.Contains(address.ToString()) || neighbors.ContainsKey(address.ToString()))
            .ToList();
        lock (sync)
        {
            scanResult.Evidence.Add($"Ping sweep: {reachable.Count} host(s) replied; neighbor table added {liveHosts.Count - reachable.Count(address => liveHosts.Any(live => live.ToString() == address))} more with a known MAC.");
            scanResult.Evidence.Add($"Live hosts to identify: {liveHosts.Count}.");
        }

        // ---- Tier 2: enrich each live host — names, vendor, signature ports, and an
        // SNMP attempt for infrastructure identity. SNMP is a bonus, not a requirement. ----
        using (var enrichConcurrency = new SemaphoreSlim(12))
        {
            var enrichTasks = liveHosts.Select(async address =>
            {
                await enrichConcurrency.WaitAsync(cancellationToken);
                try
                {
                    var device = await EnrichDiscoveredHostAsync(
                        address,
                        neighbors,
                        reachable.Contains(address.ToString()),
                        localRange?.Gateway ?? scanResult.Gateway,
                        options,
                        cancellationToken);
                    ApplySelfIdentity(device, localRange?.LocalIpAddress ?? scanResult.LocalIpAddress, Environment.MachineName);
                    lock (sync)
                    {
                        devices.Add(device);
                        if (string.Equals(device.SnmpStatus, "Timeout", StringComparison.OrdinalIgnoreCase))
                        {
                            timedOutHosts++;
                        }

                        UpdateScanProgress(scanResult, devices, scannedHosts, timedOutHosts, failedHosts, isRunning: true);
                    }

                    progress?.Invoke(Snapshot(scanResult));
                }
                catch (Exception) when (!cancellationToken.IsCancellationRequested)
                {
                    lock (sync)
                    {
                        failedHosts++;
                        UpdateScanProgress(scanResult, devices, scannedHosts, timedOutHosts, failedHosts, isRunning: true);
                    }
                }
                finally
                {
                    enrichConcurrency.Release();
                }
            });
            await Task.WhenAll(enrichTasks);
        }

        lock (sync)
        {
            UpdateScanProgress(scanResult, devices, scannedHosts, timedOutHosts, failedHosts, isRunning: false);
            var snmpCount = scanResult.Devices.Count(device => device.Identity?.Success == true);
            scanResult.Verdict = scanResult.Devices.Count > 0
                ? $"Found {scanResult.Devices.Count} device(s) in {parse.Source}; {snmpCount} answered SNMP."
                : $"No live devices found in {parse.Source}.";
            scanResult.Severity = scanResult.Devices.Count > 0 ? "OK" : "Warning";
        }

        progress?.Invoke(Snapshot(scanResult));
        return Snapshot(scanResult);
    }

    /// <summary>TCP signature ports probed per discovered host — each implies a device family.</summary>
    private static readonly int[] DiscoveryTcpPorts = [22, 80, 443, 445, 3389, 8009, 9100];

    /// <summary>
    /// The scanning machine itself shows up in its own scan; without this the row reads
    /// like a foreign device ("Silviu.telenet.be — PC / NAS (SMB)") and confuses the
    /// technician. Public + pure so the rule is test-pinned.
    /// </summary>
    public static void ApplySelfIdentity(NetworkDeviceResult device, string? localIp, string machineName)
    {
        if (string.IsNullOrWhiteSpace(localIp) ||
            !string.Equals(device.Address.Trim(), localIp.Trim(), StringComparison.Ordinal))
        {
            return;
        }

        device.NetBiosName = machineName;
        device.DeviceType = "This PC (running the scan)";
        device.ClassificationConfidence = "High";
        device.ConfirmationStatus = "This computer";
        device.Evidence.Insert(0, $"This is the computer running the scan ({machineName}).");
    }

    /// <summary>Builds the full device row for one live host (tier 2 of the scan).</summary>
    private async Task<NetworkDeviceResult> EnrichDiscoveredHostAsync(
        IPAddress address,
        IReadOnlyDictionary<string, NeighborTableEntry> neighbors,
        bool pingReachable,
        string gateway,
        SnmpSessionOptions options,
        CancellationToken cancellationToken)
    {
        var ipText = address.ToString();
        var result = new NetworkDeviceResult
        {
            Target = ipText,
            Address = ipText,
            PingReachable = pingReachable,
            PingStatus = pingReachable ? "OK" : "Unknown",
            IsGateway = !string.IsNullOrWhiteSpace(gateway) && string.Equals(gateway.Trim(), ipText, StringComparison.Ordinal),
            Verdict = "Online",
            Severity = "OK"
        };
        result.Evidence.Add(pingReachable
            ? "ICMP ping replied during the discovery sweep."
            : "No ICMP reply, but the host has an ARP/neighbor entry (likely blocks ping).");

        if (neighbors.TryGetValue(ipText, out var neighbor))
        {
            result.MacAddress = neighbor.Mac;
            result.NeighborState = neighbor.State;
            result.NeighborInterface = neighbor.InterfaceAlias;
            result.MacVendor = ResolveMacVendor(neighbor.Mac);
            result.Evidence.Add($"MAC {result.MacAddress}{(string.IsNullOrWhiteSpace(result.MacVendor) ? string.Empty : $" ({result.MacVendor})")} from the neighbor table.");
        }

        result.ReverseDnsName = await TryReverseDnsAsync(address, cancellationToken);
        result.NetBiosName = await NetBiosNameResolver.ResolveAsync(ipText, cancellationToken) ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(result.ReverseDnsName))
        {
            result.Evidence.Add($"Reverse DNS: {result.ReverseDnsName}.");
        }
        if (!string.IsNullOrWhiteSpace(result.NetBiosName))
        {
            result.Evidence.Add($"NetBIOS name: {result.NetBiosName}.");
        }

        result.Ports = (await Task.WhenAll(DiscoveryTcpPorts.Select(port => ProbeTcpPortAsync(address, port, cancellationToken))))
            .OrderBy(port => port.Port)
            .ToList();

        // SNMP attempt — infrastructure answers with full identity, everything else times out quietly.
        try
        {
            var snmp = await CollectAsync(ForTarget(options, ipText), includeInterfaces: false, includeDiagnostics: false, cancellationToken);
            result.SnmpStatus = snmp.SnmpStatus;
            result.SnmpProtocol = snmp.SnmpProtocol;
            result.SnmpCommunityMasked = snmp.SnmpCommunityMasked;
            if (snmp.Identity?.Success == true)
            {
                result.Identity = snmp.Identity;
                result.Evidence.Add($"SNMP identity: {snmp.Identity.SysNameDisplay}; location: {snmp.Identity.SysLocationDisplay}.");
            }
        }
        catch (TimeoutException)
        {
            result.SnmpStatus = "Timeout";
        }

        result.DeviceType = ClassifyDiscoveredDevice(result, out var classificationConfidence);
        result.ClassificationConfidence = classificationConfidence;
        result.ConfirmationStatus = result.Identity?.Success == true
            ? "Confirmed by SNMP identity"
            : "Heuristic (ports / vendor / names)";
        return result;
    }

    /// <summary>
    /// Honest multi-signal classification for discovered hosts: SNMP identity wins,
    /// then port signatures, then MAC-vendor / name hints (worded as "Probably …").
    /// Public and pure so tests can pin the decision table.
    /// </summary>
    public static string ClassifyDiscoveredDevice(NetworkDeviceResult result, out string confidence)
    {
        if (result.Identity?.Success == true)
        {
            confidence = "High";
            return ClassifyDevice(result.Identity, result);
        }

        bool Open(int port) => result.Ports.Any(item => item.Port == port && item.TcpSucceeded);

        if (Open(9100))
        {
            confidence = "High";
            return "Printer";
        }

        if (result.IsGateway)
        {
            confidence = "High";
            return "Router / gateway";
        }

        if (Open(3389) || (Open(445) && !string.IsNullOrWhiteSpace(result.NetBiosName)))
        {
            confidence = "Medium";
            return "Windows PC / server";
        }

        if (Open(445))
        {
            confidence = "Medium";
            return "PC / NAS (SMB)";
        }

        if (Open(8009))
        {
            confidence = "Medium";
            return "TV / media (cast)";
        }

        if (Open(22) && (Open(80) || Open(443)))
        {
            confidence = "Medium";
            return "Network device / managed host";
        }

        var hints = $"{result.MacVendor} {result.ReverseDnsName} {result.NetBiosName}".ToLowerInvariant();
        var vendorGuess = GuessTypeFromVendorHints(hints);
        if (vendorGuess is not null)
        {
            confidence = "Low";
            return vendorGuess;
        }

        if (!string.IsNullOrWhiteSpace(result.NetBiosName))
        {
            confidence = "Medium";
            return "Windows PC";
        }

        if (Open(80) || Open(443))
        {
            confidence = "Low";
            return "Device with web interface";
        }

        confidence = "Low";
        return "Unknown device (online)";
    }

    private static string? GuessTypeFromVendorHints(string hints)
    {
        if (ContainsAny(hints, "apple"))
            return "Apple device (iPhone/iPad/Mac)";
        if (ContainsAny(hints, "samsung", "xiaomi", "huawei", "oneplus", "oppo", "vivo", "realme", "motorola", "honor"))
            return "Probably phone / tablet";
        if (ContainsAny(hints, "raspberry"))
            return "Raspberry Pi / IoT";
        if (ContainsAny(hints, "espressif", "tuya", "sonoff", "shelly", "broadlink", "tasmota"))
            return "IoT / smart home";
        if (ContainsAny(hints, "amazon"))
            return "Smart speaker / TV stick";
        if (ContainsAny(hints, "google"))
            return "Chromecast / Nest";
        if (ContainsAny(hints, "sony", "lg electronics", "tcl", "vestel", "hisense", "panasonic", "philips"))
            return "TV / media";
        if (ContainsAny(hints, "tp-link", "ubiquiti", "mikrotik", "d-link", "netgear", "zyxel", "aruba", "cisco", "juniper", "ruckus"))
            return "Probably network device";
        if (ContainsAny(hints, "hewlett", "hp inc", "canon", "epson", "brother", "kyocera", "lexmark", "ricoh", "xerox", "konica", "zebra"))
            return "Probably printer";
        if (ContainsAny(hints, "intel corporate", "azurewave", "liteon", "killer"))
            return "PC / laptop";
        return null;
    }

    /// <summary>Full OUI database from the Wi-Fi module first; tiny built-in table as fallback.</summary>
    private static string ResolveMacVendor(string macAddress)
    {
        if (string.IsNullOrWhiteSpace(macAddress))
        {
            return string.Empty;
        }

        return Core.Wifi.WifiOuiLookup.Lookup(macAddress) ?? LookupMacVendor(macAddress);
    }

    private sealed record NeighborTableEntry(string Mac, string State, string InterfaceAlias);

    /// <summary>One bulk neighbor-table readout — ip → (MAC, state, interface).</summary>
    private async Task<IReadOnlyDictionary<string, NeighborTableEntry>> ReadNeighborTableAsync(CancellationToken cancellationToken)
    {
        const string script = """
$ErrorActionPreference = 'SilentlyContinue'
$rows = Get-NetNeighbor -AddressFamily IPv4 -ErrorAction SilentlyContinue |
    Where-Object {
        $_.LinkLayerAddress -and
        $_.LinkLayerAddress -ne '00-00-00-00-00-00' -and
        $_.State -in 'Reachable','Stale','Permanent','Delay','Probe'
    } |
    ForEach-Object { [pscustomobject]@{ Ip = [string]$_.IPAddress; Mac = [string]$_.LinkLayerAddress; State = [string]$_.State; InterfaceAlias = [string]$_.InterfaceAlias } }
$json = $rows | ConvertTo-Json -Depth 3 -Compress
if (-not $json) { '[]' } elseif ($json[0] -ne '[') { "[$json]" } else { $json }
""";

        try
        {
            var rows = await RunCollectorAsync<NeighborTableRow[]>(script, TimeSpan.FromSeconds(10), cancellationToken)
                       ?? [];
            var map = new Dictionary<string, NeighborTableEntry>(StringComparer.Ordinal);
            foreach (var row in rows)
            {
                if (string.IsNullOrWhiteSpace(row.Ip) || string.IsNullOrWhiteSpace(row.Mac))
                {
                    continue;
                }

                map[row.Ip.Trim()] = new NeighborTableEntry(
                    row.Mac.Trim().Replace('-', ':').ToUpperInvariant(),
                    row.State ?? string.Empty,
                    row.InterfaceAlias ?? string.Empty);
            }

            return map;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return new Dictionary<string, NeighborTableEntry>(StringComparer.Ordinal);
        }
    }

    private sealed class NeighborTableRow
    {
        public string? Ip { get; set; }
        public string? Mac { get; set; }
        public string? State { get; set; }
        public string? InterfaceAlias { get; set; }
    }

    private static void UpdateScanProgress(
        NetworkDeviceScanResult scan,
        IReadOnlyList<NetworkDeviceResult> devices,
        int scannedHosts,
        int timedOutHosts,
        int failedHosts,
        bool isRunning)
    {
        scan.ScannedHosts = scannedHosts;
        scan.TimedOutHosts = timedOutHosts;
        scan.FailedHosts = failedHosts;
        scan.IsRunning = isRunning;
        scan.Devices = devices
            .OrderBy(device => IPAddress.TryParse(device.Address, out var address) ? ToUInt32(address) : uint.MaxValue)
            .ToList();
        if (isRunning)
        {
            scan.Verdict = $"Scanning {scan.Source}: {scan.ProgressText}.";
            scan.Severity = "Unknown";
        }
    }

    private static NetworkDeviceScanResult Snapshot(NetworkDeviceScanResult source)
    {
        return new NetworkDeviceScanResult
        {
            Source = source.Source,
            Verdict = source.Verdict,
            Severity = source.Severity,
            EstimatedHosts = source.EstimatedHosts,
            MaxHosts = source.MaxHosts,
            ScannedHosts = source.ScannedHosts,
            TimedOutHosts = source.TimedOutHosts,
            FailedHosts = source.FailedHosts,
            IsRunning = source.IsRunning,
            SkippedReason = source.SkippedReason,
            SnmpProtocol = source.SnmpProtocol,
            SnmpCommunityMasked = source.SnmpCommunityMasked,
            LocalIpAddress = source.LocalIpAddress,
            AdapterName = source.AdapterName,
            Gateway = source.Gateway,
            RouteMetric = source.RouteMetric,
            InterfaceMetric = source.InterfaceMetric,
            DetectedSubnet = source.DetectedSubnet,
            SafeScanRange = source.SafeScanRange,
            Devices = [.. source.Devices],
            Evidence = [.. source.Evidence],
            Limitations = [.. source.Limitations]
        };
    }

    private async Task<NetworkDeviceResult> CollectAsync(
        SnmpSessionOptions options,
        bool includeInterfaces,
        bool includeDiagnostics,
        CancellationToken cancellationToken)
    {
        options = NormalizeOptions(options);
        var result = CreateBaseResult(options);
        if (string.IsNullOrWhiteSpace(options.Target))
        {
            result.Verdict = "Network device target is required.";
            result.Severity = "Warning";
            result.Evidence.Add("Enter a switch, access point or network device IP/hostname before running SNMP.");
            AddResultRecommendations(result);
            return result;
        }

        if (!DiagnosticTargetValidator.TryNormalizeHost(options.Target, out var normalizedTarget, out var validationReason))
        {
            result.Verdict = "Network device target is invalid.";
            result.Severity = "Warning";
            result.SnmpStatus = "Not tested";
            result.DnsStatus = "Invalid target";
            result.Evidence.Add(validationReason);
            AddResultRecommendations(result);
            return result;
        }

        options.Target = normalizedTarget;
        result.Target = normalizedTarget;

        if (options.Protocol == SnmpProtocolVersion.V3AuthPriv &&
            (string.IsNullOrWhiteSpace(options.UserName) ||
             string.IsNullOrWhiteSpace(options.AuthPassword) ||
             string.IsNullOrWhiteSpace(options.PrivacyPassword)))
        {
            result.Verdict = "SNMPv3 credentials are incomplete.";
            result.Severity = "Warning";
            result.SnmpStatus = "Credentials missing";
            result.Evidence.Add("SNMPv3 authPriv requires username, authentication password and privacy password.");
            AddResultRecommendations(result);
            return result;
        }

        IPAddress address;
        try
        {
            address = await _snmpClient.ResolveIpv4Async(options.Target.Trim(), cancellationToken);
            result.Address = address.ToString();
            result.DnsStatus = IPAddress.TryParse(options.Target.Trim(), out _) ? "IP address" : "OK";
            result.Evidence.Add($"Resolved target {options.Target.Trim()} to {address}.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var targetRejected = IsPrivateIpv4SafetyRejection(ex.Message);
            result.Verdict = targetRejected ? "Target rejected by safety policy." : "Device hostname/IP could not be resolved.";
            result.Severity = "Critical";
            result.SnmpStatus = "Not tested";
            result.DnsStatus = targetRejected ? "Rejected" : "Failed";
            result.Evidence.Add(targetRejected
                ? "Target did not resolve to an authorized private IPv4 address. Ping, TCP and SNMP checks were not started."
                : ex.Message);
            if (targetRejected)
            {
                result.Limitations.Add("Network Devices reachability and SNMP checks are limited to authorized RFC-1918 IPv4 targets.");
            }

            AddResultRecommendations(result);
            return result;
        }

        await ApplyReachabilityDiagnosticsAsync(result, address, includeDiagnostics, cancellationToken);

        try
        {
            var targetOptions = ForTarget(options, address.ToString());
            var identityValues = await _snmpClient.GetAsync(targetOptions, IdentityOids, cancellationToken);
            result.Identity = BuildIdentity(address, identityValues, targetOptions.Protocol);

            if (!result.Identity.Success)
            {
                result.Verdict = result.PingReachable
                    ? "Device reachable, but SNMP identity is unavailable."
                    : "Device unreachable or SNMP unavailable.";
                result.Severity = "Warning";
                result.ConfirmationStatus = "Unknown / unsupported";
                result.SnmpStatus = "Responded without identity";
                result.DeviceType = ClassifyDevice(result);
                result.Evidence.Add(result.Identity.Error);
                AddResultRecommendations(result);
                return result;
            }

            result.ConfirmationStatus = "Confirmed by SNMP";
            result.SnmpStatus = "Responded";
            result.DeviceType = ClassifyDevice(result.Identity, result);
            result.Verdict = "SNMP device identity confirmed.";
            result.Severity = "OK";
            result.Evidence.Add($"SNMP protocol: {targetOptions.Protocol}.");
            result.Evidence.Add($"SNMP identity: {FirstNonEmpty(result.Identity.SysName, result.Identity.SysDescr, result.Address)}.");
            result.Evidence.Add($"Device type classification: {result.DeviceType}.");

            if (!includeInterfaces)
            {
                AddResultRecommendations(result);
                return result;
            }

            var firstSnapshot = await ReadIfMibAsync(targetOptions, cancellationToken);
            if (firstSnapshot.Count == 0)
            {
                result.Verdict = "SNMP identity confirmed, but IF-MIB interfaces were not returned.";
                result.Severity = "Warning";
                result.Evidence.Add("No IF-MIB interface rows were returned by SNMP walk.");
                AddResultRecommendations(result);
                return result;
            }

            result.Evidence.Add($"IF-MIB first snapshot returned {firstSnapshot.Count} interfaces.");
            await Task.Delay(InterfaceSampleInterval, cancellationToken);
            var secondSnapshot = await ReadIfMibAsync(targetOptions, cancellationToken);
            result.Interfaces = secondSnapshot.Count > 0 ? secondSnapshot : firstSnapshot;
            ApplyInterfaceSampling(firstSnapshot, result.Interfaces, InterfaceSampleInterval);

            if (result.Interfaces.Count == 0)
            {
                result.Verdict = "SNMP identity confirmed, but IF-MIB interfaces were not returned.";
                result.Severity = "Warning";
                result.Evidence.Add("No IF-MIB interface rows were returned by SNMP walk.");
                AddResultRecommendations(result);
                return result;
            }

            result.PortsNeedingAttention = result.Interfaces
                .Where(item => item.NeedsAttention)
                .OrderByDescending(item => item.Severity == "Warning")
                .ThenBy(item => item.Index)
                .ToList();
            result.DeviceType = RefineDeviceTypeWithInterfaces(result.DeviceType, result);

            var attentionCount = result.PortsNeedingAttention.Count;
            result.Verdict = attentionCount > 0
                ? $"Read {result.Interfaces.Count} interfaces; {attentionCount} need attention."
                : $"Read {result.Interfaces.Count} interfaces; no obvious interface issues found.";
            result.Severity = attentionCount > 0 ? "Warning" : "OK";
            result.Evidence.Add($"IF-MIB second snapshot returned {result.Interfaces.Count} interfaces.");
            result.Evidence.Add($"Interface counters sampled for {InterfaceSampleInterval.TotalSeconds:N0} seconds.");
            AddResultRecommendations(result);
            return result;
        }
        catch (TimeoutException)
        {
            result.DeviceType = ClassifyDevice(result);
            result.Verdict = result.Ports.Any(port => port.TcpSucceeded)
                ? "Device reachable by TCP, but SNMP timed out on UDP 161."
                : result.PingReachable
                    ? "Device reachable, but SNMP timed out on UDP 161."
                    : "Device unreachable or SNMP timed out on UDP 161.";
            result.Severity = "Warning";
            result.ConfirmationStatus = "Unknown / unsupported";
            result.SnmpStatus = "Timeout";
            result.Evidence.Add("SNMP request timed out. Possible causes: SNMP disabled, UDP 161 blocked, wrong community/credentials, or device ACL.");
            AddResultRecommendations(result);
            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            result.Verdict = "SNMP network device read failed.";
            result.Severity = "Critical";
            result.ConfirmationStatus = "Unknown / unsupported";
            result.SnmpStatus = "Failed";
            result.DeviceType = ClassifyDevice(result);
            result.Evidence.Add(SanitizeError(ex.Message, options));
            AddResultRecommendations(result);
            return result;
        }
    }

    private NetworkDeviceResult CreateBaseResult(SnmpSessionOptions options)
    {
        return new NetworkDeviceResult
        {
            Target = options.Target.Trim(),
            SnmpProtocol = options.Protocol,
            SnmpCommunityMasked = MaskCredential(options),
            Limitations =
            [
                "SNMP is read-only; no device configuration is changed.",
                "SNMPv2c is compatibility mode for authorized internal networks only.",
                "SNMPv3 authPriv is optional secure mode when devices support it.",
                "Counters are cumulative snapshots; sampling is used to identify increasing errors/discards.",
                "Vendor-specific AP client/SSID details are not included in this sprint."
            ]
        };
    }

    private async Task<List<NetworkDeviceInterfaceInfo>> ReadIfMibAsync(
        SnmpSessionOptions options,
        CancellationToken cancellationToken)
    {
        var indexes = await SnmpWalkIndexedAsync(options, IfIndex, cancellationToken);
        var descriptions = await SnmpWalkIndexedAsync(options, IfDescr, cancellationToken);
        var names = await SnmpWalkIndexedAsync(options, IfName, cancellationToken);
        var speeds = await SnmpWalkIndexedAsync(options, IfSpeed, cancellationToken);
        var highSpeeds = await SnmpWalkIndexedAsync(options, IfHighSpeed, cancellationToken);
        var adminStatuses = await SnmpWalkIndexedAsync(options, IfAdminStatus, cancellationToken);
        var operStatuses = await SnmpWalkIndexedAsync(options, IfOperStatus, cancellationToken);
        var inOctets = await SnmpWalkIndexedAsync(options, IfInOctets, cancellationToken);
        var outOctets = await SnmpWalkIndexedAsync(options, IfOutOctets, cancellationToken);
        var hcInOctets = await SnmpWalkIndexedAsync(options, IfHCInOctets, cancellationToken);
        var hcOutOctets = await SnmpWalkIndexedAsync(options, IfHCOutOctets, cancellationToken);
        var inDiscards = await SnmpWalkIndexedAsync(options, IfInDiscards, cancellationToken);
        var outDiscards = await SnmpWalkIndexedAsync(options, IfOutDiscards, cancellationToken);
        var inErrors = await SnmpWalkIndexedAsync(options, IfInErrors, cancellationToken);
        var outErrors = await SnmpWalkIndexedAsync(options, IfOutErrors, cancellationToken);

        var allIndexes = indexes.Keys
            .Concat(descriptions.Keys)
            .Concat(names.Keys)
            .Distinct()
            .OrderBy(index => index);

        var interfaces = new List<NetworkDeviceInterfaceInfo>();
        foreach (var index in allIndexes)
        {
            var highSpeed = TryParseLong(GetValue(highSpeeds, index));
            var speedBits = TryParseLong(GetValue(speeds, index));
            long? speedMbps = null;
            if (highSpeed.HasValue && highSpeed.Value > 0)
            {
                speedMbps = highSpeed.Value;
            }
            else if (speedBits.HasValue && speedBits.Value > 0 && speedBits.Value < 4_294_967_295)
            {
                speedMbps = speedBits.Value / 1_000_000;
            }

            var errors = TryParseUlong(GetValue(inErrors, index)) + TryParseUlong(GetValue(outErrors, index));
            var discards = TryParseUlong(GetValue(inDiscards, index)) + TryParseUlong(GetValue(outDiscards, index));
            // Prefer 64-bit HC counters; fall back to 32-bit only when the device
            // doesn't expose the ifXTable for this interface.
            var hcIn = TryParseUlong(GetValue(hcInOctets, index));
            var hcOut = TryParseUlong(GetValue(hcOutOctets, index));
            var input = hcIn > 0 ? hcIn : TryParseUlong(GetValue(inOctets, index));
            var output = hcOut > 0 ? hcOut : TryParseUlong(GetValue(outOctets, index));

            var item = new NetworkDeviceInterfaceInfo
            {
                Index = index,
                Name = FirstNonEmpty(GetValue(names, index), $"ifIndex {index}"),
                Description = GetValue(descriptions, index),
                AdminStatus = FormatStatus(GetValue(adminStatuses, index)),
                OperStatus = FormatStatus(GetValue(operStatuses, index)),
                SpeedMbps = speedMbps,
                SpeedText = speedMbps.HasValue ? $"{speedMbps.Value:N0} Mbps" : "Unknown",
                InOctets = input,
                OutOctets = output,
                Errors = errors,
                Discards = discards,
                TrafficText = $"In {FormatBytes(input)} / Out {FormatBytes(output)}"
            };

            ApplyInterfaceVerdict(item);
            interfaces.Add(item);
        }

        return interfaces;
    }

    private async Task<Dictionary<int, string>> SnmpWalkIndexedAsync(
        SnmpSessionOptions options,
        string baseOid,
        CancellationToken cancellationToken)
    {
        var raw = await _snmpClient.WalkRawAsync(options, baseOid, cancellationToken);
        var indexed = new Dictionary<int, string>();
        foreach (var (oid, value) in raw)
        {
            var index = ParseIndex(baseOid, oid);
            if (index.HasValue)
            {
                indexed[index.Value] = value;
            }
        }

        return indexed;
    }

    private async Task<NetworkScanRangeInfo> TryGetDefaultRouteScanRangeAsync(CancellationToken cancellationToken)
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

        var (data, _) = await RunCollectorWithExecutionAsync<List<NetworkScanRangeInfo>>(
            script,
            TimeSpan.FromSeconds(8),
            cancellationToken);
        var candidate = data?
            .Where(item => IPAddress.TryParse(item.LocalIpAddress, out var ip) && IsPrivateIpv4(ip))
            .OrderBy(item => item.RouteMetric + item.InterfaceMetric)
            .ThenBy(item => item.InterfaceIndex)
            .FirstOrDefault();

        return candidate is null
            ? new NetworkScanRangeInfo { Error = "Default-route private IPv4 adapter could not be detected." }
            : BuildLocalRange(candidate);
    }

    private static NetworkScanRangeInfo TryGetLocalScanRange()
    {
        NetworkInterface[] networkInterfaces;
        try
        {
            networkInterfaces = NetworkInterface.GetAllNetworkInterfaces();
        }
        catch
        {
            return new NetworkScanRangeInfo { Error = "Local network adapters could not be enumerated." };
        }

        foreach (var networkInterface in networkInterfaces)
        {
            IPInterfaceProperties properties;
            try
            {
                if (networkInterface.OperationalStatus != OperationalStatus.Up ||
                    networkInterface.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                {
                    continue;
                }

                properties = networkInterface.GetIPProperties();
            }
            catch
            {
                continue;
            }

            foreach (var unicast in properties.UnicastAddresses)
            {
                if (unicast.Address.AddressFamily != AddressFamily.InterNetwork || IsApipa(unicast.Address))
                {
                    continue;
                }

                var gateway = properties.GatewayAddresses
                    .FirstOrDefault(item => item.Address.AddressFamily == AddressFamily.InterNetwork)?
                    .Address.ToString() ?? string.Empty;
                var range = BuildLocalRange(new NetworkScanRangeInfo
                {
                    LocalIpAddress = unicast.Address.ToString(),
                    AdapterName = networkInterface.Name,
                    PrefixLength = unicast.PrefixLength,
                    Gateway = gateway
                });

                if (string.IsNullOrWhiteSpace(range.Error))
                {
                    return range;
                }
            }
        }

        return new NetworkScanRangeInfo { Error = "Local private IPv4 subnet could not be detected." };
    }

    private static NetworkScanRangeInfo BuildLocalRange(NetworkScanRangeInfo info)
    {
        if (!IPAddress.TryParse(info.LocalIpAddress, out var ip))
        {
            info.Error = "Selected adapter does not have a valid IPv4 address.";
            return info;
        }

        if (!IsPrivateIpv4(ip))
        {
            info.Error = "Selected adapter IPv4 address is not private. Public IP scanning is not allowed.";
            return info;
        }

        if (info.PrefixLength is null or < 1 or > 32)
        {
            info.Error = "Selected adapter does not have a valid IPv4 prefix length.";
            return info;
        }

        var prefix = info.PrefixLength.Value;
        var actualNetwork = ToUInt32(ip) & Mask(prefix);
        var safeNetwork = prefix < 24 ? ToUInt32(ip) & Mask(24) : actualNetwork;
        var safePrefix = prefix < 24 ? 24 : prefix;
        info.DetectedSubnet = $"{UInt32ToIp(actualNetwork)}/{prefix}";
        info.DetectedSubnetHosts = EstimateUsableHosts(prefix);
        info.SafeScanRange = $"{UInt32ToIp(safeNetwork)}/{safePrefix}";
        info.ScanRange = info.SafeScanRange;
        info.Warning = prefix < 24
            ? "Detected network is larger than /24; NetScope will scan only a safe 256-IP window around this PC unless a safe custom range is entered."
            : string.Empty;
        info.Evidence =
        [
            $"Detected local IPv4 {info.LocalIpAddress} on adapter {info.AdapterName}.",
            string.IsNullOrWhiteSpace(info.Gateway) ? "Default gateway was not detected." : $"Default gateway is {info.Gateway}.",
            $"Route metric {info.RouteMetric}; interface metric {info.InterfaceMetric}.",
            $"Local adapter prefix length is /{prefix}.",
            $"Detected subnet is {info.DetectedSubnet} with approximately {info.DetectedSubnetHosts:N0} usable hosts.",
            $"Auto safe scan range is {info.SafeScanRange}."
        ];
        if (!string.IsNullOrWhiteSpace(info.Warning))
        {
            info.Evidence.Add(info.Warning);
        }

        return info;
    }

    private static void ApplyInterfaceVerdict(NetworkDeviceInterfaceInfo item)
    {
        item.NeedsAttention = false;
        item.AttentionReason = string.Empty;

        if (item.OperStatus.Equals("Down", StringComparison.OrdinalIgnoreCase))
        {
            MarkAttention(item, "Port down", "Confirm whether this port should be active. If yes, check endpoint power, cable, patch panel and switch admin state.");
            return;
        }

        if (item.SpeedMbps == 100)
        {
            MarkAttention(item, "Port negotiated at 100 Mbps", "Verify expected speed, cable category, wall socket, docking station, NIC settings and switch speed/duplex.");
            return;
        }

        if (item.ErrorsPerSecond > 0 || item.DiscardsPerSecond > 0)
        {
            MarkAttention(item, "Errors/discards increasing", "Check physical layer immediately: cable, SFP, patch panel, duplex mismatch and switch counter history.");
            return;
        }

        if (item.Errors > 0 || item.Discards > 0)
        {
            MarkAttention(item, "Errors/discards detected", "Counters are cumulative. Sample again or compare switch counters to confirm whether errors are still increasing.");
            return;
        }

        if (item.UtilizationPercent is >= HighInterfaceUtilizationPercent)
        {
            MarkAttention(item, $"High traffic utilization ({item.UtilizationPercent.Value:N1}%)", "Check whether this is an uplink or expected peak. If not expected, review top talkers, loops, backups or saturation.");
            return;
        }

        if (!item.SpeedMbps.HasValue)
        {
            item.Verdict = "Interface speed unknown";
            item.Severity = "Unknown";
            item.NeedsAttention = true;
            item.AttentionReason = item.Verdict;
            item.RecommendedNextCheck = "Device did not expose interface speed through IF-MIB. Verify vendor MIB support or check the device UI/CLI.";
            return;
        }

        item.Verdict = "OK";
        item.Severity = "OK";
        item.RecommendedNextCheck = "No immediate action.";
    }

    private static void ApplyInterfaceSampling(
        IReadOnlyList<NetworkDeviceInterfaceInfo> firstSnapshot,
        IReadOnlyList<NetworkDeviceInterfaceInfo> secondSnapshot,
        TimeSpan interval)
    {
        var firstByIndex = firstSnapshot.ToDictionary(item => item.Index);
        var seconds = Math.Max(interval.TotalSeconds, 1);
        foreach (var current in secondSnapshot)
        {
            if (!firstByIndex.TryGetValue(current.Index, out var previous))
            {
                ApplyInterfaceVerdict(current);
                continue;
            }

            var byteDelta = Delta(previous.InOctets + previous.OutOctets, current.InOctets + current.OutOctets);
            var errorDelta = Delta(previous.Errors, current.Errors);
            var discardDelta = Delta(previous.Discards, current.Discards);

            current.BytesPerSecond = byteDelta / seconds;
            current.ErrorsPerSecond = errorDelta / seconds;
            current.DiscardsPerSecond = discardDelta / seconds;
            current.TrafficRateText = $"{FormatBytes((ulong)Math.Round(current.BytesPerSecond.Value))}/s";

            if (current.SpeedMbps is > 0)
            {
                var bitsPerSecond = current.BytesPerSecond.Value * 8;
                current.UtilizationPercent = Math.Round(bitsPerSecond / (current.SpeedMbps.Value * 1_000_000d) * 100, 1);
            }

            ApplyInterfaceVerdict(current);
        }
    }

    private static void MarkAttention(NetworkDeviceInterfaceInfo item, string reason, string nextCheck)
    {
        item.Verdict = reason;
        item.Severity = "Warning";
        item.NeedsAttention = true;
        item.AttentionReason = reason;
        item.RecommendedNextCheck = nextCheck;
    }

    private static void AddResultRecommendations(NetworkDeviceResult result)
    {
        ApplyNetworkDeviceDiagnostics(result);
        result.Recommendations.Clear();

        if (string.IsNullOrWhiteSpace(result.Target))
        {
            AddRecommendation(result, "Enter an authorized internal device IP or hostname before running the check.");
            return;
        }

        if (string.Equals(result.DnsStatus, "Invalid target", StringComparison.OrdinalIgnoreCase))
        {
            AddRecommendation(result, "Enter a host name or IP address without protocol, path, spaces or special characters.");
            return;
        }

        if (string.Equals(result.DnsStatus, "Rejected", StringComparison.OrdinalIgnoreCase))
        {
            AddRecommendation(result, "Enter an authorized RFC-1918 IPv4 address or internal hostname that resolves to one. Reachability checks were not started.");
            return;
        }

        if (string.Equals(result.SnmpStatus, "Credentials missing", StringComparison.OrdinalIgnoreCase))
        {
            AddRecommendation(result, "For SNMPv3 authPriv, enter username, authentication password and privacy password for this session.");
            return;
        }

        if (string.Equals(result.DnsStatus, "Failed", StringComparison.OrdinalIgnoreCase))
        {
            AddRecommendation(result, "Check DNS name spelling, DNS suffix, internal DNS servers and whether the target has a valid DNS record.");
        }

        var hasOpenTcp = result.Ports.Any(port => port.TcpSucceeded);
        if (!result.PingReachable && (hasOpenTcp || string.Equals(result.SnmpStatus, "Responded", StringComparison.OrdinalIgnoreCase)))
        {
            AddRecommendation(result, "ICMP is blocked or disabled, but the device is reachable by another protocol. Do not treat ping failure alone as an outage.");
        }

        if (!result.PingReachable &&
            !hasOpenTcp &&
            !string.Equals(result.SnmpStatus, "Responded", StringComparison.OrdinalIgnoreCase))
        {
            AddRecommendation(result, "Device is not reachable by ICMP, common TCP ports or SNMP. Check IP, VLAN/subnet, routing, firewall ACL and whether the device is powered on.");
        }

        if (string.Equals(result.SnmpStatus, "Timeout", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(result.SnmpStatus, "Failed", StringComparison.OrdinalIgnoreCase))
        {
            AddRecommendation(result, "Verify SNMP is enabled on the device, UDP 161 is allowed from this PC, and the selected community or SNMPv3 credentials are correct.");
        }

        if (string.Equals(result.SnmpStatus, "Responded without identity", StringComparison.OrdinalIgnoreCase))
        {
            AddRecommendation(result, "SNMP responded but system identity was incomplete. Check device SNMP view/ACL and whether standard system OIDs are exposed.");
        }

        if (result.Ports.Any(port => port.TcpSucceeded && port.Port is 80 or 443 or 8080 or 8443))
        {
            AddRecommendation(result, "Web management appears reachable. If SNMP fails, compare SNMP settings in the device web UI.");
        }

        if (!string.IsNullOrWhiteSpace(result.MacAddress) && string.IsNullOrWhiteSpace(result.MacVendor))
        {
            AddRecommendation(result, "MAC address was detected but vendor is unknown. Use vendor/OUI lookup or switch MAC table for stronger identification.");
        }

        foreach (var item in result.PortsNeedingAttention.Take(8))
        {
            AddRecommendation(result, $"Interface {item.Index} {FirstNonEmpty(item.Name, item.Description)}: {item.RecommendedNextCheck}");
        }

        if (result.Interfaces.Count > 0 &&
            result.PortsNeedingAttention.Count == 0 &&
            string.Equals(result.SnmpStatus, "Responded", StringComparison.OrdinalIgnoreCase))
        {
            AddRecommendation(result, "No obvious interface issues were detected by IF-MIB. If users still report issues, test the specific client/server path and switch port.");
        }
    }

    private static void AddRecommendation(NetworkDeviceResult result, string recommendation)
    {
        if (!string.IsNullOrWhiteSpace(recommendation) &&
            !result.Recommendations.Contains(recommendation, StringComparer.OrdinalIgnoreCase))
        {
            result.Recommendations.Add(recommendation);
        }
    }

    private static void ApplyNetworkDeviceDiagnostics(NetworkDeviceResult result)
    {
        result.ConfirmedFindings.Clear();
        result.ProbableFindings.Clear();
        result.UnknownFindings.Clear();

        if (string.Equals(result.DnsStatus, "Invalid target", StringComparison.OrdinalIgnoreCase))
        {
            AddFinding(result.UnknownFindings, "Reachability was not tested because the target input format is invalid.");
            result.AffectedLayer = "Target input";
            result.OwnerSuggestion = "Technician";
            result.ClassificationConfidence = "Low";
            result.Confidence = "High";
            return;
        }

        if (string.Equals(result.DnsStatus, "Rejected", StringComparison.OrdinalIgnoreCase))
        {
            AddFinding(result.UnknownFindings, "Reachability was not tested because the target is outside the authorized private IPv4 scope.");
            result.AffectedLayer = "Target validation";
            result.OwnerSuggestion = "Technician";
            result.ClassificationConfidence = "Low";
            result.Confidence = "High";
            return;
        }

        if (string.Equals(result.SnmpStatus, "Credentials missing", StringComparison.OrdinalIgnoreCase))
        {
            AddFinding(result.ProbableFindings, "SNMPv3 credential input is incomplete; reachability and identity checks were not started.");
            result.AffectedLayer = "SNMP credentials";
            result.OwnerSuggestion = "Network";
            result.ClassificationConfidence = "Low";
            result.Confidence = "High";
            return;
        }

        var snmpResponded = string.Equals(result.SnmpStatus, "Responded", StringComparison.OrdinalIgnoreCase);
        var snmpUnavailable =
            string.Equals(result.SnmpStatus, "Timeout", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(result.SnmpStatus, "Failed", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(result.SnmpStatus, "Responded without identity", StringComparison.OrdinalIgnoreCase);
        var hasOpenTcp = result.Ports.Any(port => port.TcpSucceeded);

        AddFinding(result.ConfirmedFindings, result.PingReachable
            ? $"ICMP responded with {result.LossText} packet loss and {result.LatencyText} average latency."
            : string.Empty);
        AddFinding(result.ConfirmedFindings, hasOpenTcp
            ? $"TCP responded on port(s): {result.OpenPortSummary}."
            : string.Empty);
        AddFinding(result.ConfirmedFindings, snmpResponded
            ? $"SNMP responded using {result.SnmpProtocol}."
            : string.Empty);
        AddFinding(result.ConfirmedFindings, result.Identity?.Success == true
            ? $"System identity returned: {FirstNonEmpty(result.Identity.SysName, result.Identity.SysDescr, result.Address)}."
            : string.Empty);
        AddFinding(result.ConfirmedFindings, result.Interfaces.Count > 0
            ? $"IF-MIB returned {result.Interfaces.Count} interfaces."
            : string.Empty);

        foreach (var item in result.PortsNeedingAttention.Take(5))
        {
            AddFinding(result.ConfirmedFindings, $"Interface {item.Index} {FirstNonEmpty(item.Name, item.Description)} needs attention: {item.AttentionReason}.");
        }

        if (string.Equals(result.DnsStatus, "Failed", StringComparison.OrdinalIgnoreCase))
        {
            AddFinding(result.ProbableFindings, "DNS/name resolution is the first problem to fix for this target.");
        }

        if (!result.PingReachable && (hasOpenTcp || snmpResponded))
        {
            AddFinding(result.ProbableFindings, "ICMP is probably blocked or disabled; TCP/SNMP proves the device is reachable.");
        }

        if (!result.PingReachable && !hasOpenTcp && !snmpResponded)
        {
            AddFinding(result.ProbableFindings, "Device may be offline, on the wrong subnet/VLAN, blocked by firewall ACL, or using different SNMP settings.");
        }

        if (snmpUnavailable)
        {
            AddFinding(result.ProbableFindings, "SNMP may be disabled, blocked by ACL/firewall, unsupported, or using different credentials.");
        }

        if (result.Identity?.Success == true && result.Interfaces.Count == 0 && result.Verdict.Contains("IF-MIB", StringComparison.OrdinalIgnoreCase))
        {
            AddFinding(result.ProbableFindings, "Device exposes SNMP system identity but not standard IF-MIB interface rows.");
        }

        if (result.DeviceType.StartsWith("Possible", StringComparison.OrdinalIgnoreCase) ||
            result.DeviceType.Contains("Unknown", StringComparison.OrdinalIgnoreCase) ||
            result.DeviceType.Contains("managed host", StringComparison.OrdinalIgnoreCase))
        {
            AddFinding(result.UnknownFindings, "Device type is inferred from hostname, MAC vendor or open ports, not confirmed by vendor inventory.");
        }

        if (result.Interfaces.Count == 0 && !snmpResponded)
        {
            AddFinding(result.UnknownFindings, "Interface speed, duplex, counters and port state cannot be confirmed without SNMP IF-MIB access.");
        }

        ApplyNetworkDeviceOwnership(result, snmpResponded, hasOpenTcp);
        result.ClassificationConfidence = DetermineClassificationConfidence(result);
        result.Confidence = DetermineDiagnosticConfidence(result, snmpResponded, hasOpenTcp);
    }

    private static void ApplyNetworkDeviceOwnership(
        NetworkDeviceResult result,
        bool snmpResponded,
        bool hasOpenTcp)
    {
        if (string.Equals(result.DnsStatus, "Invalid target", StringComparison.OrdinalIgnoreCase))
        {
            result.AffectedLayer = "Target input";
            result.OwnerSuggestion = "Technician";
            return;
        }

        if (string.Equals(result.DnsStatus, "Rejected", StringComparison.OrdinalIgnoreCase))
        {
            result.AffectedLayer = "Target validation";
            result.OwnerSuggestion = "Technician";
            return;
        }

        if (string.Equals(result.SnmpStatus, "Credentials missing", StringComparison.OrdinalIgnoreCase))
        {
            result.AffectedLayer = "SNMP credentials";
            result.OwnerSuggestion = "Network";
            return;
        }

        if (string.Equals(result.DnsStatus, "Failed", StringComparison.OrdinalIgnoreCase))
        {
            result.AffectedLayer = "DNS";
            result.OwnerSuggestion = "Network / DNS";
            return;
        }

        if (result.PortsNeedingAttention.Any(item =>
                item.AttentionReason.Contains("100 Mbps", StringComparison.OrdinalIgnoreCase) ||
                item.AttentionReason.Contains("Errors", StringComparison.OrdinalIgnoreCase) ||
                item.AttentionReason.Contains("Discards", StringComparison.OrdinalIgnoreCase)))
        {
            result.AffectedLayer = "Switch port / Physical link";
            result.OwnerSuggestion = "Network";
            return;
        }

        if (result.PortsNeedingAttention.Any(item => item.AttentionReason.Contains("Port down", StringComparison.OrdinalIgnoreCase)))
        {
            result.AffectedLayer = "Device interface";
            result.OwnerSuggestion = "Network / Local Support";
            return;
        }

        if ((string.Equals(result.SnmpStatus, "Timeout", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(result.SnmpStatus, "Failed", StringComparison.OrdinalIgnoreCase)) &&
            (result.PingReachable || hasOpenTcp))
        {
            result.AffectedLayer = "SNMP / Device ACL";
            result.OwnerSuggestion = "Network";
            return;
        }

        if (!result.PingReachable && !hasOpenTcp && !snmpResponded)
        {
            result.AffectedLayer = "Network / Routing / Firewall";
            result.OwnerSuggestion = "Network / Firewall";
            return;
        }

        if (snmpResponded &&
            result.Interfaces.Count == 0 &&
            result.Verdict.Contains("IF-MIB", StringComparison.OrdinalIgnoreCase))
        {
            result.AffectedLayer = "SNMP / IF-MIB";
            result.OwnerSuggestion = "Network";
            return;
        }

        result.AffectedLayer = "Network device";
        result.OwnerSuggestion = result.DeviceType.Contains("Printer", StringComparison.OrdinalIgnoreCase)
            ? "Print / Network"
            : "Network";
    }

    private static string DetermineClassificationConfidence(NetworkDeviceResult result)
    {
        if (result.Identity?.Success == true &&
            !result.DeviceType.StartsWith("Possible", StringComparison.OrdinalIgnoreCase) &&
            !result.DeviceType.Contains("Unknown", StringComparison.OrdinalIgnoreCase))
        {
            return "High";
        }

        if (!string.IsNullOrWhiteSpace(result.ReverseDnsName) ||
            !string.IsNullOrWhiteSpace(result.MacVendor) ||
            result.Ports.Any(port => port.TcpSucceeded))
        {
            return "Medium";
        }

        return "Low";
    }

    private static string DetermineDiagnosticConfidence(
        NetworkDeviceResult result,
        bool snmpResponded,
        bool hasOpenTcp)
    {
        if (string.Equals(result.DnsStatus, "Invalid target", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(result.DnsStatus, "Rejected", StringComparison.OrdinalIgnoreCase))
        {
            return "High";
        }

        if (string.Equals(result.DnsStatus, "Failed", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(result.SnmpStatus, "Credentials missing", StringComparison.OrdinalIgnoreCase))
        {
            return "High";
        }

        if (snmpResponded && (result.Interfaces.Count > 0 || result.Identity?.Success == true))
        {
            return result.PortsNeedingAttention.Count > 0 ? "High" : "Medium";
        }

        if (result.PingReachable || hasOpenTcp)
        {
            return "Medium";
        }

        return "Low";
    }

    private static void AddFinding(List<string> findings, string finding)
    {
        if (!string.IsNullOrWhiteSpace(finding) &&
            !findings.Contains(finding, StringComparer.OrdinalIgnoreCase))
        {
            findings.Add(finding);
        }
    }

    private static SnmpDeviceInfo BuildIdentity(
        IPAddress address,
        IReadOnlyDictionary<string, string> values,
        string protocol)
    {
        var info = new SnmpDeviceInfo
        {
            Address = address.ToString(),
            Success = true,
            Protocol = protocol,
            SysDescr = GetValue(values, SysDescr),
            SysUpTime = HumanizeUpTime(GetValue(values, SysUpTime)),
            SysContact = GetValue(values, SysContact),
            SysName = GetValue(values, SysName),
            SysLocation = GetValue(values, SysLocation)
        };

        if (string.IsNullOrWhiteSpace(info.SysDescr) && string.IsNullOrWhiteSpace(info.SysName))
        {
            info.Success = false;
            info.Error = "SNMP responded but did not return system identity fields.";
        }

        return info;
    }

    private static SnmpSessionOptions NormalizeOptions(SnmpSessionOptions options)
    {
        var protocol = string.IsNullOrWhiteSpace(options.Protocol) ? SnmpProtocolVersion.V2C : options.Protocol;
        return new SnmpSessionOptions
        {
            Protocol = protocol,
            Target = options.Target.Trim(),
            Community = string.IsNullOrWhiteSpace(options.Community) ? "public" : options.Community.Trim(),
            UserName = options.UserName.Trim(),
            AuthProtocol = string.IsNullOrWhiteSpace(options.AuthProtocol) ? "SHA256" : options.AuthProtocol,
            AuthPassword = options.AuthPassword,
            PrivacyProtocol = string.IsNullOrWhiteSpace(options.PrivacyProtocol) ? "AES128" : options.PrivacyProtocol,
            PrivacyPassword = options.PrivacyPassword,
            TimeoutMs = options.TimeoutMs <= 0 ? 2500 : options.TimeoutMs
        };
    }

    private static SnmpSessionOptions ForTarget(SnmpSessionOptions options, string target)
    {
        return new SnmpSessionOptions
        {
            Protocol = options.Protocol,
            Target = target,
            Community = options.Community,
            UserName = options.UserName,
            AuthProtocol = options.AuthProtocol,
            AuthPassword = options.AuthPassword,
            PrivacyProtocol = options.PrivacyProtocol,
            PrivacyPassword = options.PrivacyPassword,
            TimeoutMs = options.TimeoutMs
        };
    }

    private async Task ApplyReachabilityDiagnosticsAsync(
        NetworkDeviceResult result,
        IPAddress address,
        bool includeDiagnostics,
        CancellationToken cancellationToken)
    {
        var ping = includeDiagnostics
            ? await ProbePingAsync(address, samples: 4, cancellationToken)
            : await ProbePingAsync(address, samples: 1, cancellationToken);

        result.PacketLoss = ping.PacketLoss;
        result.AverageLatencyMs = ping.AverageLatencyMs;
        result.PingStatus = ping.PacketLoss.Status;
        result.PingReachable = ping.PacketLoss.Received > 0;
        result.Evidence.Add(result.PingReachable
            ? $"ICMP ping responded: {ping.PacketLoss.Received}/{ping.PacketLoss.Sent}; average {result.LatencyText}; loss {result.LossText}."
            : "ICMP ping failed or was blocked.");

        if (!includeDiagnostics)
        {
            return;
        }

        result.ReverseDnsName = await TryReverseDnsAsync(address, cancellationToken);
        if (!string.IsNullOrWhiteSpace(result.ReverseDnsName))
        {
            result.Evidence.Add($"Reverse DNS name: {result.ReverseDnsName}.");
        }

        var neighbor = await TryGetNeighborAsync(address, cancellationToken);
        result.MacAddress = neighbor.MacAddress;
        result.NeighborState = neighbor.State;
        result.NeighborInterface = neighbor.InterfaceAlias;
        result.MacVendor = LookupMacVendor(neighbor.MacAddress);
        if (!string.IsNullOrWhiteSpace(result.MacAddress))
        {
            result.Evidence.Add($"ARP/neighbor entry: {result.MacAddress}; state {FirstNonEmpty(result.NeighborState, "Unknown")}; interface {FirstNonEmpty(result.NeighborInterface, "Unknown")}.");
        }

        result.Ports = (await Task.WhenAll(ManagementTcpPorts.Select(port => ProbeTcpPortAsync(address, port, cancellationToken))))
            .OrderBy(port => port.Port)
            .ToList();
        var openPorts = result.Ports.Where(port => port.TcpSucceeded).Select(port => port.Port).ToList();
        result.Evidence.Add(openPorts.Count > 0
            ? $"Open TCP management/service ports detected: {string.Join(", ", openPorts)}."
            : "No common TCP management/service ports responded.");
    }

    private static async Task<PingProbeResult> ProbePingAsync(
        IPAddress address,
        int samples,
        CancellationToken cancellationToken)
    {
        var sent = Math.Max(samples, 1);
        var received = 0;
        var latencies = new List<double>();
        try
        {
            using var ping = new Ping();
            for (var i = 0; i < sent; i++)
            {
                var reply = await ping.SendPingAsync(address, (int)PingTimeout.TotalMilliseconds)
                    .WaitAsync(PingTimeout + TimeSpan.FromMilliseconds(250), cancellationToken);
                if (reply.Status == IPStatus.Success)
                {
                    received++;
                    latencies.Add(reply.RoundtripTime);
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Treat ping errors as no ICMP response. TCP/SNMP may still prove reachability.
        }

        var loss = Math.Round(((sent - received) / (double)sent) * 100, 1);
        var status = received == sent ? "OK" : received > 0 ? "Warning" : "Critical";
        var average = latencies.Count > 0 ? Math.Round(latencies.Average(), 1) : (double?)null;
        return new PingProbeResult(
            new PacketLossResult
            {
                Target = address.ToString(),
                Status = status,
                Sent = sent,
                Received = received,
                LossPercent = loss,
                Details = $"{loss:N1}% packet loss ({received}/{sent} replies)."
            },
            average);
    }

    private static async Task<string> TryReverseDnsAsync(IPAddress address, CancellationToken cancellationToken)
    {
        try
        {
            var entry = await Dns.GetHostEntryAsync(address)
                .WaitAsync(ReverseDnsTimeout, cancellationToken);
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

    private async Task<NeighborInfo> TryGetNeighborAsync(IPAddress address, CancellationToken cancellationToken)
    {
        var safeIp = address.ToString().Replace("'", "''", StringComparison.Ordinal);
        var script = $$"""
$neighbor = Get-NetNeighbor -IPAddress '{{safeIp}}' -ErrorAction SilentlyContinue |
    Where-Object { $_.LinkLayerAddress -and $_.LinkLayerAddress -notmatch '^00([-:]00){5}$' } |
    Select-Object -First 1
if ($neighbor) {
    [pscustomobject]@{
        MacAddress = [string]$neighbor.LinkLayerAddress
        State = [string]$neighbor.State
        InterfaceAlias = [string]$neighbor.InterfaceAlias
    }
} else {
    [pscustomobject]@{
        MacAddress = ''
        State = 'Unavailable'
        InterfaceAlias = ''
    }
}
""";

        var (data, _) = await RunCollectorWithExecutionAsync<NeighborInfo>(
            script,
            TimeSpan.FromSeconds(4),
            cancellationToken);
        return data ?? new NeighborInfo { State = "Unavailable" };
    }

    private static async Task<PortProbeResult> ProbeTcpPortAsync(
        IPAddress address,
        int port,
        CancellationToken cancellationToken)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var client = new TcpClient();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TcpTimeout);
            await client.ConnectAsync(address, port, timeout.Token);
            stopwatch.Stop();
            return new PortProbeResult
            {
                Port = port,
                TcpSucceeded = client.Connected,
                LatencyMs = Math.Round(stopwatch.Elapsed.TotalMilliseconds, 1),
                Details = client.Connected ? "TCP port open." : "TCP connection did not complete."
            };
        }
        catch (OperationCanceledException)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            return new PortProbeResult
            {
                Port = port,
                TcpSucceeded = false,
                Details = "TCP port timed out."
            };
        }
        catch (TimeoutException)
        {
            return new PortProbeResult
            {
                Port = port,
                TcpSucceeded = false,
                Details = "TCP port timed out."
            };
        }
        catch (SocketException ex)
        {
            return new PortProbeResult
            {
                Port = port,
                TcpSucceeded = false,
                Details = ex.SocketErrorCode switch
                {
                    SocketError.ConnectionRefused => "TCP port closed or refused.",
                    SocketError.HostUnreachable => "TCP host unreachable.",
                    SocketError.NetworkUnreachable => "TCP network unreachable.",
                    _ => "TCP connection failed."
                }
            };
        }
        catch
        {
            return new PortProbeResult
            {
                Port = port,
                TcpSucceeded = false,
                Details = "TCP connection failed."
            };
        }
    }

    private static string MaskCredential(SnmpSessionOptions options)
    {
        if (options.Protocol == SnmpProtocolVersion.V3AuthPriv)
        {
            return string.IsNullOrWhiteSpace(options.UserName)
                ? "SNMPv3 credentials not set"
                : $"SNMPv3 user {options.UserName}; secrets masked";
        }

        return string.IsNullOrWhiteSpace(options.Community) ? "(default public)" : "********";
    }

    private static string SanitizeError(string message, SnmpSessionOptions? options = null)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return "Unknown SNMP error.";
        }

        if (options is null)
        {
            return message;
        }

        var sanitized = message;
        Redact(ref sanitized, options.Community);
        Redact(ref sanitized, options.UserName);
        Redact(ref sanitized, options.AuthPassword);
        Redact(ref sanitized, options.PrivacyPassword);
        return sanitized;
    }

    private static bool IsPrivateIpv4SafetyRejection(string message) =>
        message.Contains("private IPv4", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("authorized private", StringComparison.OrdinalIgnoreCase);

    private static void Redact(ref string message, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            message = message.Replace(value, "[redacted]", StringComparison.OrdinalIgnoreCase);
        }
    }

    private static double Delta(ulong previous, ulong current)
    {
        return current >= previous ? current - previous : 0;
    }

    /// <summary>
    /// Turns a raw SNMP sysUpTime value into "Xd Yh Zm". SNMP TimeTicks are hundredths
    /// of a second; libraries render them either as bare digits or as "(ticks) d:hh:mm:ss".
    /// We extract the first long, treat it as ticks, and humanize — falling back to the
    /// original text when it isn't parseable.
    /// </summary>
    private static string HumanizeUpTime(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "Unknown";

        var digits = new string(raw.TakeWhile(char.IsDigit).ToArray());
        if (digits.Length == 0)
        {
            var open = raw.IndexOf('(');
            var close = raw.IndexOf(')');
            if (open >= 0 && close > open)
            {
                digits = new string(raw[(open + 1)..close].Where(char.IsDigit).ToArray());
            }
        }

        if (!long.TryParse(digits, out var ticks) || ticks <= 0)
        {
            return raw.Trim();
        }

        var up = TimeSpan.FromSeconds(ticks / 100.0);
        return up.TotalDays >= 1
            ? $"{(int)up.TotalDays}d {up.Hours}h {up.Minutes}m"
            : up.TotalHours >= 1
                ? $"{up.Hours}h {up.Minutes}m"
                : $"{up.Minutes}m {up.Seconds}s";
    }

    private static string GetValue(IReadOnlyDictionary<string, string> values, string oid)
    {
        return values.TryGetValue(oid, out var value) ? value : string.Empty;
    }

    private static string GetValue(IReadOnlyDictionary<int, string> values, int index)
    {
        return values.TryGetValue(index, out var value) ? value : string.Empty;
    }

    private static long? TryParseLong(string value)
    {
        return long.TryParse(value, out var parsed) ? parsed : null;
    }

    private static ulong TryParseUlong(string value)
    {
        return ulong.TryParse(value, out var parsed) ? parsed : 0;
    }

    private static string FormatStatus(string value)
    {
        return value switch
        {
            "1" => "Up",
            "2" => "Down",
            "3" => "Testing",
            "4" => "Unknown",
            "5" => "Dormant",
            "6" => "Not present",
            "7" => "Lower layer down",
            _ => string.IsNullOrWhiteSpace(value) ? "Unknown" : value
        };
    }

    private static string FormatBytes(ulong value)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var size = (double)value;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return $"{size:N1} {units[unit]}";
    }

    private static string ClassifyDevice(SnmpDeviceInfo identity, NetworkDeviceResult result)
    {
        var text = $"{identity.SysDescr} {identity.SysName}".ToLowerInvariant();
        if (ContainsAny(text, "printer", "print", "laserjet", "officejet", "ricoh", "canon", "xerox", "brother", "kyocera", "epson", "konica", "lexmark", "zebra"))
        {
            return "Printer";
        }

        if (ContainsAny(text, "access point", "aironet", "aruba ap", "aruba iap", "instant ap", "ruckus", "unifi ap", "ubiquiti uap", "omada", "eap", "wifi", "wireless", "wlc", "wireless controller"))
        {
            return "Access point";
        }

        if (ContainsAny(text, "switch", "catalyst", "procurve", "arubaos-cx", "officeconnect", "edgeswitch", "unifi switch", "usw-", "powerconnect", "d-link", "netgear", "hpe ethernet"))
        {
            return "Switch";
        }

        if (ContainsAny(text, "firewall", "fortigate", "sophos", "pfsense", "opnsense", "router", "gateway", "mikrotik", "routeros", "routerboard", "edgerouter", "unifi security gateway", "sonicwall", "watchguard"))
        {
            return "Router / firewall";
        }

        if (ContainsAny(text, "windows", "linux", "ubuntu", "debian", "red hat", "vmware", "esxi", "synology", "qnap", "truenas", "freenas"))
        {
            return "Server / managed host";
        }

        return ClassifyDevice(result);
    }

    private static string ClassifyDevice(NetworkDeviceResult result)
    {
        var text = $"{result.Target} {result.ReverseDnsName} {result.MacVendor}".ToLowerInvariant();
        if (ContainsAny(text, "printer", "print", "laserjet", "officejet", "ricoh", "canon", "xerox", "brother", "kyocera", "epson", "konica", "lexmark", "zebra"))
        {
            return "Printer";
        }

        if (ContainsAny(text, "access point", "ap-", "-ap", "wifi", "wireless", "unifi ap", "ubiquiti", "ruckus", "omada", "eap"))
        {
            return "Possible access point";
        }

        if (ContainsAny(text, "switch", "sw-", "-sw", "cisco", "procurve", "aruba", "hpe", "d-link", "netgear"))
        {
            return "Possible switch";
        }

        if (ContainsAny(text, "router", "gateway", "firewall", "fortigate", "sophos", "mikrotik", "pfsense", "opnsense", "sonicwall", "watchguard"))
        {
            return "Possible router / firewall";
        }

        if (ContainsAny(text, "server", "srv", "dc-", "fs-", "nas", "synology", "qnap", "vmware", "esxi", "windows", "linux"))
        {
            return "Possible server / managed host";
        }

        if (result.Ports.Any(port => port.TcpSucceeded && port.Port is 22 or 23 or 80 or 443 or 8080 or 8443))
        {
            return "Network device or managed host";
        }

        return "Unknown network host";
    }

    private static string RefineDeviceTypeWithInterfaces(string currentType, NetworkDeviceResult result)
    {
        if (result.Interfaces.Count == 0)
        {
            return currentType;
        }

        if (!currentType.Contains("Unknown", StringComparison.OrdinalIgnoreCase) &&
            !currentType.Contains("managed host", StringComparison.OrdinalIgnoreCase))
        {
            return currentType;
        }

        var physicalLikePorts = result.Interfaces.Count(item =>
            item.SpeedMbps.HasValue ||
            item.Name.Contains("gi", StringComparison.OrdinalIgnoreCase) ||
            item.Name.Contains("eth", StringComparison.OrdinalIgnoreCase) ||
            item.Description.Contains("ethernet", StringComparison.OrdinalIgnoreCase));

        if (result.Interfaces.Count >= 8 || physicalLikePorts >= 8)
        {
            return "Switch / router";
        }

        if (result.Interfaces.Count >= 3)
        {
            return "Router / firewall or managed host";
        }

        return currentType;
    }

    private static string LookupMacVendor(string macAddress)
    {
        var prefix = NormalizeMacPrefix(macAddress);
        if (string.IsNullOrWhiteSpace(prefix))
        {
            return string.Empty;
        }

        var vendors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["00:1B:17"] = "Cisco",
            ["00:1D:A2"] = "Cisco",
            ["00:23:04"] = "Cisco",
            ["00:25:45"] = "Cisco",
            ["00:50:56"] = "VMware",
            ["00:0C:29"] = "VMware",
            ["08:00:27"] = "VirtualBox",
            ["00:15:5D"] = "Microsoft Hyper-V",
            ["9C:93:4E"] = "Xerox",
            ["00:80:77"] = "Brother",
            ["00:00:85"] = "Canon",
            ["00:26:73"] = "Ricoh",
            ["00:17:C8"] = "Kyocera",
            ["B4:FB:E4"] = "Ubiquiti",
            ["F0:9F:C2"] = "Ubiquiti",
            ["24:A4:3C"] = "Ubiquiti",
            ["EC:08:6B"] = "TP-Link",
            ["F4:F2:6D"] = "TP-Link",
            ["00:0C:42"] = "MikroTik",
            ["18:FD:74"] = "MikroTik",
            ["00:1F:33"] = "Netgear",
            ["00:24:B2"] = "Netgear",
            ["00:05:5D"] = "D-Link",
            ["00:1E:58"] = "D-Link"
        };

        return vendors.TryGetValue(prefix, out var vendor)
            ? vendor
            : "Unknown vendor";
    }

    private static string NormalizeMacPrefix(string macAddress)
    {
        var hex = new string(macAddress.Where(Uri.IsHexDigit).ToArray()).ToUpperInvariant();
        return hex.Length < 6
            ? string.Empty
            : $"{hex[..2]}:{hex.Substring(2, 2)}:{hex.Substring(4, 2)}";
    }

    private static bool ContainsAny(string value, params string[] candidates)
    {
        return candidates.Any(candidate => value.Contains(candidate, StringComparison.OrdinalIgnoreCase));
    }

    private static string FirstNonEmpty(params string[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
    }

    private static bool IsChildOid(string baseOid, string candidate)
    {
        return candidate.StartsWith(baseOid + ".", StringComparison.Ordinal);
    }

    private static int? ParseIndex(string baseOid, string oid)
    {
        if (!IsChildOid(baseOid, oid))
        {
            return null;
        }

        var suffix = oid[(baseOid.Length + 1)..];
        var firstPart = suffix.Split('.', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return int.TryParse(firstPart, out var index) ? index : null;
    }

    private static int EstimateUsableHosts(int prefix) => NetworkSafetyPolicy.EstimateUsableHosts(prefix);
    private static uint Mask(int prefix) => NetworkSafetyPolicy.Mask(prefix);
    private static uint ToUInt32(IPAddress address) => NetworkSafetyPolicy.ToUInt32(address);
    private static IPAddress UInt32ToIp(uint value) => NetworkSafetyPolicy.FromUInt32(value);
    private static bool IsApipa(IPAddress address) => NetworkSafetyPolicy.IsApipa(address);
    private static bool IsPrivateIpv4(IPAddress address) => NetworkSafetyPolicy.IsPrivateIpv4(address);

    private sealed record PingProbeResult(PacketLossResult PacketLoss, double? AverageLatencyMs);

    private sealed class NeighborInfo
    {
        public string MacAddress { get; set; } = string.Empty;
        public string State { get; set; } = string.Empty;
        public string InterfaceAlias { get; set; } = string.Empty;
    }
}
