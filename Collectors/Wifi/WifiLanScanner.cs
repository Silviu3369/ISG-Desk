using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Serialization;
using System.Xml.Linq;
using NetScopeDiagnosticCenter.Core;
using NetScopeDiagnosticCenter.Core.Models.Wifi;
using NetScopeDiagnosticCenter.Core.Wifi;
using NetScopeDiagnosticCenter.Infrastructure;

namespace NetScopeDiagnosticCenter.Collectors.Wifi;

/// <summary>
/// Discovers devices on the local Wi-Fi subnet: a bounded parallel ping sweep of the /24
/// to populate the OS ARP cache, then neighbour-table readout enriched with device names
/// and OUI vendor data.
/// </summary>
public sealed class WifiLanScanner : JsonCollectorBase
{
    private static readonly TimeSpan ReverseDnsTimeout = TimeSpan.FromMilliseconds(700);
    private static readonly TimeSpan PingNameTimeout = TimeSpan.FromMilliseconds(800);
    private static readonly TimeSpan NetBiosTimeout = TimeSpan.FromMilliseconds(900);
    private static readonly TimeSpan MulticastDiscoveryWindow = TimeSpan.FromMilliseconds(2200);
    private static readonly TimeSpan SsdpDescriptionTimeout = TimeSpan.FromMilliseconds(900);

    public WifiLanScanner(PowerShellRunner powerShell, TimeProvider? time = null)
        : base(powerShell)
    {
        _ = time;
    }

    public Task<IReadOnlyList<WifiLanDevice>> ScanAsync(CancellationToken cancellationToken) =>
        ScanAsync(connection: null, cancellationToken);

    public async Task<IReadOnlyList<WifiLanDevice>> ScanAsync(
        WifiConnectionDetails? connection,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var info = WifiAdapterInfo.GetActiveWifi();
        if (string.IsNullOrEmpty(info.Ipv4Address) || !IsPrivateIpv4(info.Ipv4Address))
        {
            return Array.Empty<WifiLanDevice>();
        }

        var myIp = info.Ipv4Address!;
        var gatewayIp = info.Ipv4Gateway;
        var connectedBssid = NormalizeMac(connection?.Bssid);
        var basePrefix = myIp[..(myIp.LastIndexOf('.') + 1)];

        await PingSweepAsync(basePrefix, cancellationToken).ConfigureAwait(false);

        const string script = """
$ErrorActionPreference = 'SilentlyContinue'
$rows = Get-NetNeighbor -AddressFamily IPv4 -ErrorAction SilentlyContinue |
    Where-Object {
        $_.LinkLayerAddress -and
        $_.LinkLayerAddress -ne '00-00-00-00-00-00' -and
        $_.State -in 'Reachable','Stale','Permanent','Delay','Probe'
    } |
    ForEach-Object { [pscustomobject]@{ Ip = [string]$_.IPAddress; Mac = [string]$_.LinkLayerAddress } }
$json = $rows | ConvertTo-Json -Depth 3 -Compress
if (-not $json) { '[]' } elseif ($json[0] -ne '[') { "[$json]" } else { $json }
""";

        NeighborRow[] rows;
        try
        {
            rows = await RunCollectorAsync<NeighborRow[]>(script, TimeSpan.FromSeconds(8), cancellationToken)
                       .ConfigureAwait(false)
                   ?? Array.Empty<NeighborRow>();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            rows = Array.Empty<NeighborRow>();
        }

        var byIp = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            if (string.IsNullOrEmpty(row.Ip) || !row.Ip.StartsWith(basePrefix, StringComparison.Ordinal))
            {
                continue;
            }

            if (row.Ip.EndsWith(".255", StringComparison.Ordinal))
            {
                continue;
            }

            byIp[row.Ip] = NormalizeMac(row.Mac);
        }

        byIp.TryAdd(myIp, NormalizeMac(info.MacAddress));
        if (!string.IsNullOrEmpty(gatewayIp))
        {
            byIp.TryAdd(gatewayIp!, null);
        }

        var advertisedNames = await DiscoverAdvertisedDeviceNamesAsync(basePrefix, cancellationToken).ConfigureAwait(false);
        var cachedNames = await ReadCachedDeviceNamesAsync(basePrefix, cancellationToken).ConfigureAwait(false);

        using var nameThrottle = new SemaphoreSlim(16);
        var nameTasks = byIp.Keys.ToDictionary(
            ip => ip,
            ip => ResolveDeviceNameAsync(
                ip,
                isGateway: !string.IsNullOrEmpty(gatewayIp) && ip == gatewayIp,
                isThisPc: ip == myIp,
                advertisedNames,
                cachedNames,
                nameThrottle,
                cancellationToken));
        await Task.WhenAll(nameTasks.Values).ConfigureAwait(false);

        var result = new List<WifiLanDevice>(byIp.Count);
        foreach (var (ip, mac) in byIp)
        {
            var resolvedName = nameTasks[ip].Result;
            var role = DetectNetworkRole(
                ip,
                mac,
                gatewayIp,
                connectedBssid,
                isThisPc: ip == myIp);
            result.Add(new WifiLanDevice(
                IpAddress: ip,
                MacAddress: mac,
                HostName: resolvedName?.Name,
                Vendor: WifiOuiLookup.DescribeVendor(mac),
                IsGateway: !string.IsNullOrEmpty(gatewayIp) && ip == gatewayIp,
                IsThisPc: ip == myIp,
                NameSource: resolvedName?.Source,
                IsWifiAccessPoint: role.IsWifiAccessPoint,
                IsLikelyWifiAccessPoint: role.IsLikelyWifiAccessPoint,
                AccessPointBssid: role.AccessPointBssid,
                RoleSource: role.Source));
        }

        result.Sort((a, b) => a.LastOctet.CompareTo(b.LastOctet));
        return result;
    }

    private async Task<IReadOnlyDictionary<string, DeviceNameResult>> ReadCachedDeviceNamesAsync(
        string basePrefix,
        CancellationToken cancellationToken)
    {
        var prefixLiteral = PsSingleQuote(basePrefix);
        var script = $$"""
$ErrorActionPreference = 'SilentlyContinue'
$prefix = '{{prefixLiteral}}'
$rows = @()
Get-DnsClientCache -ErrorAction SilentlyContinue | ForEach-Object {
    $entry = [string]$_.Entry
    if ([string]::IsNullOrWhiteSpace($entry)) { $entry = [string]$_.RecordName }
    foreach ($rawData in @($_.Data)) {
        $data = [string]$rawData
        if ([string]::IsNullOrWhiteSpace($data)) { continue }
        if ($data.StartsWith($prefix)) {
            $rows += [pscustomobject]@{ Ip = $data; Name = $entry }
            continue
        }
        if ($entry -match '^(\d+)\.(\d+)\.(\d+)\.(\d+)\.in-addr\.arpa\.?$') {
            $ip = "$($Matches[4]).$($Matches[3]).$($Matches[2]).$($Matches[1])"
            if ($ip.StartsWith($prefix)) {
                $rows += [pscustomobject]@{ Ip = $ip; Name = $data }
            }
        }
    }
}
$json = $rows | ConvertTo-Json -Depth 3 -Compress
if (-not $json) { '[]' } elseif ($json[0] -ne '[') { "[$json]" } else { $json }
""";

        DnsCacheRow[] rows;
        try
        {
            rows = await RunCollectorAsync<DnsCacheRow[]>(script, TimeSpan.FromSeconds(5), cancellationToken)
                       .ConfigureAwait(false)
                   ?? Array.Empty<DnsCacheRow>();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            rows = Array.Empty<DnsCacheRow>();
        }

        var names = new Dictionary<string, DeviceNameResult>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            if (string.IsNullOrWhiteSpace(row.Ip) ||
                !row.Ip.StartsWith(basePrefix, StringComparison.Ordinal) ||
                row.Name?.Contains("in-addr.arpa", StringComparison.OrdinalIgnoreCase) == true)
            {
                continue;
            }

            var name = TryNormalizeDeviceName(row.Name);
            if (!string.IsNullOrWhiteSpace(name))
            {
                names.TryAdd(row.Ip, new DeviceNameResult(name, "Windows name cache"));
            }
        }

        return names;
    }

    private static NetworkRoleResult DetectNetworkRole(
        string ip,
        string? mac,
        string? gatewayIp,
        string? connectedBssid,
        bool isThisPc)
    {
        var isGateway = !string.IsNullOrWhiteSpace(gatewayIp)
                        && string.Equals(ip, gatewayIp, StringComparison.OrdinalIgnoreCase);
        if (isThisPc)
        {
            return NetworkRoleResult.None;
        }

        var normalizedMac = NormalizeMac(mac);
        var normalizedBssid = NormalizeMac(connectedBssid);
        var hasBssid = !string.IsNullOrWhiteSpace(normalizedBssid);
        var exactBssidMatch = hasBssid
                              && !string.IsNullOrWhiteSpace(normalizedMac)
                              && string.Equals(normalizedMac, normalizedBssid, StringComparison.OrdinalIgnoreCase);
        var sameOui = !exactBssidMatch
                      && isGateway
                      && SameOui(normalizedMac, normalizedBssid);

        if (isGateway && exactBssidMatch)
        {
            return new NetworkRoleResult(
                IsWifiAccessPoint: true,
                IsLikelyWifiAccessPoint: false,
                AccessPointBssid: normalizedBssid,
                Source: $"Default gateway MAC matches the connected Wi-Fi BSSID ({normalizedBssid}).");
        }

        if (isGateway && sameOui)
        {
            return new NetworkRoleResult(
                IsWifiAccessPoint: false,
                IsLikelyWifiAccessPoint: true,
                AccessPointBssid: normalizedBssid,
                Source: $"Default gateway MAC and connected Wi-Fi BSSID share OUI {MacOui(normalizedBssid)}; many routers use separate LAN and Wi-Fi MACs.");
        }

        if (isGateway)
        {
            var bssidText = hasBssid
                ? $" Connected Wi-Fi AP BSSID: {normalizedBssid}."
                : string.Empty;
            return new NetworkRoleResult(
                IsWifiAccessPoint: false,
                IsLikelyWifiAccessPoint: false,
                AccessPointBssid: normalizedBssid,
                Source: $"Default IPv4 gateway for this Wi-Fi connection.{bssidText}");
        }

        if (exactBssidMatch)
        {
            return new NetworkRoleResult(
                IsWifiAccessPoint: true,
                IsLikelyWifiAccessPoint: false,
                AccessPointBssid: normalizedBssid,
                Source: $"Device MAC matches the connected Wi-Fi BSSID ({normalizedBssid}).");
        }

        return NetworkRoleResult.None;
    }

    private static bool SameOui(string? leftMac, string? rightMac)
    {
        var left = MacOui(leftMac);
        var right = MacOui(rightMac);
        return !string.IsNullOrWhiteSpace(left)
               && string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    }

    private static string? MacOui(string? mac)
    {
        var normalized = NormalizeMac(mac);
        return normalized is { Length: >= 8 } ? normalized[..8] : null;
    }

    private static async Task<IReadOnlyDictionary<string, DeviceNameResult>> DiscoverAdvertisedDeviceNamesAsync(
        string basePrefix,
        CancellationToken cancellationToken)
    {
        var names = new Dictionary<string, DeviceNameResult>(StringComparer.OrdinalIgnoreCase);

        var mdnsTask = DiscoverMdnsNamesAsync(basePrefix, cancellationToken);
        var ssdpTask = DiscoverSsdpNamesAsync(basePrefix, cancellationToken);

        await Task.WhenAll(mdnsTask, ssdpTask).ConfigureAwait(false);

        foreach (var pair in mdnsTask.Result)
        {
            AddNameCandidate(names, pair.Key, pair.Value.Name, pair.Value.Source);
        }

        foreach (var pair in ssdpTask.Result)
        {
            AddNameCandidate(names, pair.Key, pair.Value.Name, pair.Value.Source);
        }

        return names;
    }

    private static async Task<IReadOnlyDictionary<string, DeviceNameResult>> DiscoverMdnsNamesAsync(
        string basePrefix,
        CancellationToken cancellationToken)
    {
        var names = new Dictionary<string, DeviceNameResult>(StringComparer.OrdinalIgnoreCase);

        try
        {
            using var udp = new UdpClient(AddressFamily.InterNetwork);
            udp.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 1);
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, 0));

            var query = BuildMdnsQuery();
            await udp.SendAsync(query, query.Length, new IPEndPoint(IPAddress.Parse("224.0.0.251"), 5353))
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);

            var deadline = DateTimeOffset.UtcNow + MulticastDiscoveryWindow;
            while (DateTimeOffset.UtcNow < deadline)
            {
                var remaining = deadline - DateTimeOffset.UtcNow;
                if (remaining <= TimeSpan.Zero)
                {
                    break;
                }

                // Observed: when the deadline wins the race below, the abandoned receive
                // faults later (client disposed) and must not go unobserved.
                var receiveTask = TaskFaultObserver.Observe(udp.ReceiveAsync(cancellationToken).AsTask());
                var delayTask = Task.Delay(remaining, cancellationToken);
                var completed = await Task.WhenAny(receiveTask, delayTask).ConfigureAwait(false);
                if (completed != receiveTask)
                {
                    break;
                }

                var response = await receiveTask.ConfigureAwait(false);
                var sourceIp = response.RemoteEndPoint.Address.ToString();
                if (!sourceIp.StartsWith(basePrefix, StringComparison.Ordinal))
                {
                    continue;
                }

                foreach (var candidate in ParseMdnsNameCandidates(response.Buffer))
                {
                    AddNameCandidate(names, sourceIp, candidate, "mDNS / Bonjour");
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // mDNS/Bonjour is best-effort. Networks without multicast support simply skip this source.
        }

        return names;
    }

    private static byte[] BuildMdnsQuery()
    {
        string[] services =
        [
            "_services._dns-sd._udp.local",
            "_device-info._tcp.local",
            "_workstation._tcp.local",
            "_smb._tcp.local",
            "_googlecast._tcp.local",
            "_airplay._tcp.local",
            "_raop._tcp.local",
            "_ipp._tcp.local",
            "_printer._tcp.local",
            "_http._tcp.local"
        ];

        using var stream = new MemoryStream();
        WriteUInt16(stream, 0);
        WriteUInt16(stream, 0);
        WriteUInt16(stream, (ushort)services.Length);
        WriteUInt16(stream, 0);
        WriteUInt16(stream, 0);
        WriteUInt16(stream, 0);

        foreach (var service in services)
        {
            WriteDnsName(stream, service);
            WriteUInt16(stream, 12);       // PTR
            WriteUInt16(stream, 0x8001);   // IN + unicast-response requested
        }

        return stream.ToArray();
    }

    private static IEnumerable<string> ParseMdnsNameCandidates(byte[] packet)
    {
        if (packet.Length < 12)
        {
            yield break;
        }

        var questionCount = ReadUInt16(packet, 4);
        var answerCount = ReadUInt16(packet, 6);
        var authorityCount = ReadUInt16(packet, 8);
        var additionalCount = ReadUInt16(packet, 10);
        var offset = 12;

        for (var i = 0; i < questionCount; i++)
        {
            if (!TryReadDnsName(packet, ref offset, out _) || offset + 4 > packet.Length)
            {
                yield break;
            }
            offset += 4;
        }

        var recordCount = answerCount + authorityCount + additionalCount;
        for (var i = 0; i < recordCount; i++)
        {
            if (!TryReadDnsName(packet, ref offset, out var ownerName) || offset + 10 > packet.Length)
            {
                yield break;
            }

            var type = ReadUInt16(packet, offset);
            offset += 2; // type
            offset += 2; // class
            offset += 4; // ttl
            var dataLength = ReadUInt16(packet, offset);
            offset += 2;
            if (offset + dataLength > packet.Length)
            {
                yield break;
            }

            var dataOffset = offset;
            if (type == 12)
            {
                var ptrOffset = dataOffset;
                if (TryReadDnsName(packet, ref ptrOffset, out var ptrName))
                {
                    var candidate = ExtractMdnsFriendlyName(ptrName);
                    if (!string.IsNullOrWhiteSpace(candidate))
                    {
                        yield return candidate;
                    }
                }
            }
            else if (type == 33 && dataLength >= 6)
            {
                var owner = ExtractMdnsFriendlyName(ownerName);
                if (!string.IsNullOrWhiteSpace(owner))
                {
                    yield return owner;
                }

                var srvOffset = dataOffset + 6;
                if (TryReadDnsName(packet, ref srvOffset, out var targetName))
                {
                    var target = ExtractMdnsFriendlyName(targetName);
                    if (!string.IsNullOrWhiteSpace(target))
                    {
                        yield return target;
                    }
                }
            }
            else if (type == 1)
            {
                var host = ExtractMdnsFriendlyName(ownerName);
                if (!string.IsNullOrWhiteSpace(host))
                {
                    yield return host;
                }
            }

            offset += dataLength;
        }
    }

    private static string? ExtractMdnsFriendlyName(string? rawName)
    {
        if (string.IsNullOrWhiteSpace(rawName))
        {
            return null;
        }

        var name = rawName.Trim().TrimEnd('.');
        if (name.StartsWith("_", StringComparison.Ordinal))
        {
            return null;
        }

        var serviceMarker = name.IndexOf("._", StringComparison.Ordinal);
        if (serviceMarker > 0)
        {
            name = name[..serviceMarker];
        }
        else if (name.EndsWith(".local", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^".local".Length];
        }

        return TryNormalizeDeviceName(name);
    }

    private static async Task<IReadOnlyDictionary<string, DeviceNameResult>> DiscoverSsdpNamesAsync(
        string basePrefix,
        CancellationToken cancellationToken)
    {
        var responses = new Dictionary<string, Uri>(StringComparer.OrdinalIgnoreCase);

        try
        {
            using var udp = new UdpClient(AddressFamily.InterNetwork);
            udp.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 2);
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, 0));

            var query = Encoding.ASCII.GetBytes(
                "M-SEARCH * HTTP/1.1\r\n" +
                "HOST: 239.255.255.250:1900\r\n" +
                "MAN: \"ssdp:discover\"\r\n" +
                "MX: 1\r\n" +
                "ST: ssdp:all\r\n" +
                "USER-AGENT: ISGDesk/2.0 UPnP/1.1\r\n\r\n");
            await udp.SendAsync(query, query.Length, new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900))
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);

            var deadline = DateTimeOffset.UtcNow + MulticastDiscoveryWindow;
            while (DateTimeOffset.UtcNow < deadline)
            {
                var remaining = deadline - DateTimeOffset.UtcNow;
                if (remaining <= TimeSpan.Zero)
                {
                    break;
                }

                // Observed: when the deadline wins the race below, the abandoned receive
                // faults later (client disposed) and must not go unobserved.
                var receiveTask = TaskFaultObserver.Observe(udp.ReceiveAsync(cancellationToken).AsTask());
                var delayTask = Task.Delay(remaining, cancellationToken);
                var completed = await Task.WhenAny(receiveTask, delayTask).ConfigureAwait(false);
                if (completed != receiveTask)
                {
                    break;
                }

                var response = await receiveTask.ConfigureAwait(false);
                var sourceIp = response.RemoteEndPoint.Address.ToString();
                if (!sourceIp.StartsWith(basePrefix, StringComparison.Ordinal))
                {
                    continue;
                }

                var text = Encoding.UTF8.GetString(response.Buffer);
                var headers = ParseSsdpHeaders(text);
                if (headers.TryGetValue("LOCATION", out var location) &&
                    TryCreateSafePrivateUri(location, basePrefix, out var uri))
                {
                    responses.TryAdd(sourceIp, uri);
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // SSDP/UPnP is best-effort and may be disabled by the network or host firewall.
        }

        return await ResolveSsdpDescriptionNamesAsync(responses, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyDictionary<string, DeviceNameResult>> ResolveSsdpDescriptionNamesAsync(
        IReadOnlyDictionary<string, Uri> locations,
        CancellationToken cancellationToken)
    {
        var names = new Dictionary<string, DeviceNameResult>(StringComparer.OrdinalIgnoreCase);
        if (locations.Count == 0)
        {
            return names;
        }

        using var http = new HttpClient { Timeout = SsdpDescriptionTimeout };
        using var throttle = new SemaphoreSlim(4);
        var tasks = locations.Select(async pair =>
        {
            await throttle.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var name = await FetchSsdpFriendlyNameAsync(http, pair.Value, cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(name))
                {
                    lock (names)
                    {
                        AddNameCandidate(names, pair.Key, name, "SSDP / UPnP");
                    }
                }
            }
            finally
            {
                throttle.Release();
            }
        });

        await Task.WhenAll(tasks).ConfigureAwait(false);
        return names;
    }

    private static async Task<string?> FetchSsdpFriendlyNameAsync(
        HttpClient http,
        Uri uri,
        CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(SsdpDescriptionTimeout);

            using var response = await http.GetAsync(uri, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode ||
                response.Content.Headers.ContentLength is > 128 * 1024)
            {
                return null;
            }

            var xml = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            if (xml.Length > 128 * 1024)
            {
                return null;
            }

            return ParseSsdpFriendlyName(xml);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch
        {
            return null;
        }
    }

    private static string? ParseSsdpFriendlyName(string? xml)
    {
        if (string.IsNullOrWhiteSpace(xml))
        {
            return null;
        }

        try
        {
            var doc = XDocument.Parse(xml);
            var device = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "device");
            var friendly = device?.Elements().FirstOrDefault(e => e.Name.LocalName == "friendlyName")?.Value;
            var model = device?.Elements().FirstOrDefault(e => e.Name.LocalName == "modelName")?.Value;
            var manufacturer = device?.Elements().FirstOrDefault(e => e.Name.LocalName == "manufacturer")?.Value;

            return TryNormalizeDeviceName(friendly)
                   ?? TryNormalizeDeviceName(model)
                   ?? TryNormalizeDeviceName(manufacturer);
        }
        catch
        {
            return null;
        }
    }

    private static Dictionary<string, string> ParseSsdpHeaders(string response)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(response))
        {
            return headers;
        }

        foreach (var rawLine in response.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
        {
            var index = rawLine.IndexOf(':');
            if (index <= 0)
            {
                continue;
            }

            var name = rawLine[..index].Trim();
            var value = rawLine[(index + 1)..].Trim();
            if (name.Length > 0 && value.Length > 0)
            {
                headers[name] = value;
            }
        }

        return headers;
    }

    private static bool TryCreateSafePrivateUri(string? rawLocation, string basePrefix, out Uri uri)
    {
        uri = null!;
        if (!Uri.TryCreate(rawLocation, UriKind.Absolute, out var parsed) ||
            parsed.Scheme != Uri.UriSchemeHttp ||
            !IPAddress.TryParse(parsed.Host, out var address))
        {
            return false;
        }

        var host = address.ToString();
        if (!host.StartsWith(basePrefix, StringComparison.Ordinal) || !IsPrivateIpv4(host))
        {
            return false;
        }

        uri = parsed;
        return true;
    }

    private static void AddNameCandidate(
        IDictionary<string, DeviceNameResult> names,
        string? ip,
        string? rawName,
        string source)
    {
        if (string.IsNullOrWhiteSpace(ip) || !IPAddress.TryParse(ip, out _))
        {
            return;
        }

        var name = TryNormalizeDeviceName(rawName);
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        if (!names.TryGetValue(ip, out var existing) ||
            IsBetterDeviceName(name, existing.Name))
        {
            names[ip] = new DeviceNameResult(name, source);
        }
    }

    private static bool IsBetterDeviceName(string candidate, string existing)
    {
        if (candidate.Length > existing.Length && existing.Length <= 4)
        {
            return true;
        }

        return candidate.Any(char.IsWhiteSpace) && !existing.Any(char.IsWhiteSpace);
    }

    private static void WriteDnsName(Stream stream, string name)
    {
        foreach (var label in name.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            var bytes = Encoding.ASCII.GetBytes(label);
            stream.WriteByte((byte)Math.Min(bytes.Length, 63));
            stream.Write(bytes, 0, Math.Min(bytes.Length, 63));
        }
        stream.WriteByte(0);
    }

    private static void WriteUInt16(Stream stream, ushort value)
    {
        stream.WriteByte((byte)(value >> 8));
        stream.WriteByte((byte)(value & 0xFF));
    }

    private static ushort ReadUInt16(byte[] packet, int offset) =>
        offset + 1 < packet.Length
            ? (ushort)((packet[offset] << 8) | packet[offset + 1])
            : (ushort)0;

    private static bool TryReadDnsName(byte[] packet, ref int offset, out string name)
    {
        var labels = new List<string>();
        var cursor = offset;
        var jumped = false;
        var jumps = 0;

        while (cursor < packet.Length)
        {
            var length = packet[cursor];
            if (length == 0)
            {
                cursor++;
                if (!jumped)
                {
                    offset = cursor;
                }
                name = string.Join('.', labels);
                return true;
            }

            if ((length & 0xC0) == 0xC0)
            {
                if (cursor + 1 >= packet.Length || ++jumps > 12)
                {
                    break;
                }

                var pointer = ((length & 0x3F) << 8) | packet[cursor + 1];
                if (!jumped)
                {
                    offset = cursor + 2;
                }

                cursor = pointer;
                jumped = true;
                continue;
            }

            if ((length & 0xC0) != 0 || cursor + 1 + length > packet.Length)
            {
                break;
            }

            cursor++;
            labels.Add(Encoding.UTF8.GetString(packet, cursor, length));
            cursor += length;
        }

        name = string.Empty;
        return false;
    }

    private static async Task PingSweepAsync(string basePrefix, CancellationToken ct)
    {
        using var throttle = new SemaphoreSlim(96);
        var tasks = new List<Task>(254);

        for (var host = 1; host <= 254; host++)
        {
            var ip = basePrefix + host;
            tasks.Add(Task.Run(async () =>
            {
                await throttle.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    using var ping = new Ping();
                    var first = await ping.SendPingAsync(ip, 400).ConfigureAwait(false);
                    if (first.Status != IPStatus.Success && !ct.IsCancellationRequested)
                    {
                        await Task.Delay(120, ct).ConfigureAwait(false);
                        await ping.SendPingAsync(ip, 400).ConfigureAwait(false);
                    }
                }
                catch
                {
                    // Unreachable hosts are expected during a subnet sweep.
                }
                finally
                {
                    throttle.Release();
                }
            }, ct));
        }

        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Cancellation can leave a partial ARP cache; the caller token will stop later steps.
        }
    }

    private static async Task<DeviceNameResult?> ResolveDeviceNameAsync(
        string ip,
        bool isGateway,
        bool isThisPc,
        IReadOnlyDictionary<string, DeviceNameResult> advertisedNames,
        IReadOnlyDictionary<string, DeviceNameResult> cachedNames,
        SemaphoreSlim nameThrottle,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (isThisPc)
        {
            var localName = TryNormalizeDeviceName(Environment.MachineName);
            if (!string.IsNullOrWhiteSpace(localName))
            {
                return new DeviceNameResult(localName, "Local machine name");
            }
        }

        if (advertisedNames.TryGetValue(ip, out var advertisedName))
        {
            return advertisedName;
        }

        if (cachedNames.TryGetValue(ip, out var cachedName))
        {
            return cachedName;
        }

        var dnsName = await ResolveHostAsync(ip, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(dnsName))
        {
            return new DeviceNameResult(dnsName, "Reverse DNS");
        }

        await nameThrottle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var pingName = await ResolvePingNameAsync(ip, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(pingName))
            {
                return new DeviceNameResult(pingName, "Ping reverse lookup");
            }

            var netBiosName = await ResolveNetBiosNameAsync(ip, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(netBiosName))
            {
                return new DeviceNameResult(netBiosName, "NetBIOS");
            }
        }
        finally
        {
            nameThrottle.Release();
        }

        return isGateway ? new DeviceNameResult("Internet gateway", "Network role") : null;
    }

    private static async Task<string?> ResolveHostAsync(string ip, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Observed because the lookup commonly faults with "no such host" for LAN
            // IPs without a PTR record — also after losing the timeout race below.
            var dns = TaskFaultObserver.Observe(Dns.GetHostEntryAsync(ip));
            var done = await Task.WhenAny(dns, Task.Delay(ReverseDnsTimeout, cancellationToken))
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            return done == dns && dns.IsCompletedSuccessfully
                ? TryNormalizeDeviceName(dns.Result.HostName)
                : null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<string?> ResolvePingNameAsync(string ip, CancellationToken cancellationToken)
    {
        if (!IPAddress.TryParse(ip, out var address) || address.GetAddressBytes().Length != 4)
        {
            return null;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(PingNameTimeout);

        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = "ping",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        process.StartInfo.ArgumentList.Add("-a");
        process.StartInfo.ArgumentList.Add("-n");
        process.StartInfo.ArgumentList.Add("1");
        process.StartInfo.ArgumentList.Add("-w");
        process.StartInfo.ArgumentList.Add("500");
        process.StartInfo.ArgumentList.Add(address.ToString());

        try
        {
            if (!process.Start())
            {
                return null;
            }

            var outputTask = process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            var output = await outputTask.ConfigureAwait(false);
            return ParsePingName(output, address.ToString());
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            return null;
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }
        catch
        {
            return null;
        }
    }

    private static string? ParsePingName(string? output, string ip)
    {
        if (string.IsNullOrWhiteSpace(output) || string.IsNullOrWhiteSpace(ip))
        {
            return null;
        }

        var addressMarker = "[" + ip.Trim() + "]";
        foreach (var rawLine in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            var marker = line.IndexOf(addressMarker, StringComparison.OrdinalIgnoreCase);
            if (marker <= 0)
            {
                continue;
            }

            var name = line[..marker].Trim();
            if (name.StartsWith("Pinging ", StringComparison.OrdinalIgnoreCase))
            {
                name = name["Pinging ".Length..].Trim();
            }

            return TryNormalizeDeviceName(name);
        }

        return null;
    }

    private static async Task<string?> ResolveNetBiosNameAsync(string ip, CancellationToken cancellationToken)
    {
        if (!IPAddress.TryParse(ip, out var address) || address.GetAddressBytes().Length != 4)
        {
            return null;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(NetBiosTimeout);

        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = "nbtstat",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        process.StartInfo.ArgumentList.Add("-A");
        process.StartInfo.ArgumentList.Add(address.ToString());

        try
        {
            if (!process.Start())
            {
                return null;
            }

            var outputTask = process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                return null;
            }

            var output = await outputTask.ConfigureAwait(false);
            return ParseNetBiosName(output);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            return null;
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }
        catch
        {
            return null;
        }
    }

    private static string? ParseNetBiosName(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return null;
        }

        string? serverFallback = null;
        foreach (var rawLine in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (line.IndexOf("UNIQUE", StringComparison.OrdinalIgnoreCase) < 0)
            {
                continue;
            }

            var workstationMarker = line.IndexOf("<00>", StringComparison.OrdinalIgnoreCase);
            if (workstationMarker > 0)
            {
                var workstationName = line[..workstationMarker].Trim();
                if (IsUsableNetBiosName(workstationName))
                {
                    return workstationName;
                }
            }

            var serverMarker = line.IndexOf("<20>", StringComparison.OrdinalIgnoreCase);
            if (serverMarker > 0 && serverFallback is null)
            {
                var serverName = line[..serverMarker].Trim();
                if (IsUsableNetBiosName(serverName))
                {
                    serverFallback = serverName;
                }
            }
        }

        return serverFallback;
    }

    private static bool IsUsableNetBiosName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        name = name.Trim();
        if (name.Equals("__MSBROWSE__", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (IPAddress.TryParse(name, out _))
        {
            return false;
        }

        if (name.Contains('<') || name.Contains('>'))
        {
            return false;
        }

        return name.Any(char.IsLetterOrDigit);
    }

    private static string? TryNormalizeDeviceName(string? rawName)
    {
        if (string.IsNullOrWhiteSpace(rawName))
        {
            return null;
        }

        var name = rawName.Trim().TrimEnd('.');
        if (IPAddress.TryParse(name, out _))
        {
            return null;
        }

        if (name.Contains("in-addr.arpa", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var dot = name.IndexOf('.');
        if (dot > 0)
        {
            name = name[..dot];
        }

        return IsUsableNetBiosName(name) ? name : null;
    }

    private static bool IsPrivateIpv4(string ip)
    {
        if (!IPAddress.TryParse(ip, out var addr)) return false;
        var b = addr.GetAddressBytes();
        if (b.Length != 4) return false;
        return b[0] == 10
               || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
               || (b[0] == 192 && b[1] == 168);
    }

    private static string? NormalizeMac(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        return raw.Replace('-', ':').ToUpperInvariant();
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Best effort timeout cleanup only.
        }
    }

    private sealed class NeighborRow
    {
        [JsonPropertyName("Ip")] public string? Ip { get; set; }
        [JsonPropertyName("Mac")] public string? Mac { get; set; }
    }

    private sealed class DnsCacheRow
    {
        [JsonPropertyName("Ip")] public string? Ip { get; set; }
        [JsonPropertyName("Name")] public string? Name { get; set; }
    }

    private sealed record DeviceNameResult(string Name, string Source);

    private sealed record NetworkRoleResult(
        bool IsWifiAccessPoint,
        bool IsLikelyWifiAccessPoint,
        string? AccessPointBssid,
        string Source)
    {
        public static NetworkRoleResult None { get; } = new(
            IsWifiAccessPoint: false,
            IsLikelyWifiAccessPoint: false,
            AccessPointBssid: null,
            Source: string.Empty);
    }
}
