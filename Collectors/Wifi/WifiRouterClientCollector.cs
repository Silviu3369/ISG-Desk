using System.Net;
using System.Text.RegularExpressions;
using NetScopeDiagnosticCenter.Core.Models;
using NetScopeDiagnosticCenter.Core.Models.Wifi;
using NetScopeDiagnosticCenter.Core.Wifi;
using NetScopeDiagnosticCenter.Infrastructure;

namespace NetScopeDiagnosticCenter.Collectors.Wifi;

/// <summary>
/// Reads clients known by the router/AP through read-only standard management data.
/// The first production source is SNMP ipNetToMedia (router ARP table), scoped to the
/// current Wi-Fi subnet and private gateway only.
/// </summary>
public sealed class WifiRouterClientCollector
{
    private const string IpNetToMediaPhysAddress = "1.3.6.1.2.1.4.22.1.2";
    private readonly SnmpClientService _snmpClient;

    public WifiRouterClientCollector(SnmpClientService snmpClient)
    {
        _snmpClient = snmpClient ?? throw new ArgumentNullException(nameof(snmpClient));
    }

    public async Task<WifiRouterClientScanResult> ReadSnmpClientsAsync(
        WifiConnectionDetails connection,
        string? community,
        IReadOnlyCollection<WifiLanDevice> localDevices,
        CancellationToken cancellationToken = default)
        => await ReadSnmpClientsAsync(
            connection,
            new SnmpSessionOptions
            {
                Protocol = SnmpProtocolVersion.V2C,
                Community = string.IsNullOrWhiteSpace(community) ? "public" : community.Trim(),
                TimeoutMs = 3500
            },
            localDevices,
            cancellationToken).ConfigureAwait(false);

    public async Task<WifiRouterClientScanResult> ReadSnmpClientsAsync(
        WifiConnectionDetails connection,
        SnmpSessionOptions snmpOptions,
        IReadOnlyCollection<WifiLanDevice> localDevices,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!connection.IsConnected)
        {
            return WifiRouterClientScanResult.Skipped("No active Wi-Fi connection.");
        }

        if (string.IsNullOrWhiteSpace(connection.Ipv4Address) ||
            string.IsNullOrWhiteSpace(connection.Ipv4Gateway))
        {
            return WifiRouterClientScanResult.Skipped("No IPv4 gateway detected for this Wi-Fi connection.");
        }

        if (!IsPrivateIpv4(connection.Ipv4Gateway))
        {
            return WifiRouterClientScanResult.Skipped("Gateway is not a private IPv4 address; router client read was not attempted.");
        }

        var basePrefix = connection.Ipv4Address[..(connection.Ipv4Address.LastIndexOf('.') + 1)];
        var protocol = string.Equals(snmpOptions.Protocol, SnmpProtocolVersion.V3AuthPriv, StringComparison.Ordinal)
            ? SnmpProtocolVersion.V3AuthPriv
            : SnmpProtocolVersion.V2C;
        if (protocol == SnmpProtocolVersion.V3AuthPriv &&
            (string.IsNullOrWhiteSpace(snmpOptions.UserName) ||
             string.IsNullOrWhiteSpace(snmpOptions.AuthPassword) ||
             string.IsNullOrWhiteSpace(snmpOptions.PrivacyPassword)))
        {
            return WifiRouterClientScanResult.Skipped("SNMPv3 authPriv requires username, authentication password and privacy password.");
        }

        var options = new SnmpSessionOptions
        {
            Target = connection.Ipv4Gateway,
            Protocol = protocol,
            Community = string.IsNullOrWhiteSpace(snmpOptions.Community) ? "public" : snmpOptions.Community.Trim(),
            UserName = snmpOptions.UserName,
            AuthProtocol = string.IsNullOrWhiteSpace(snmpOptions.AuthProtocol) ? "SHA256" : snmpOptions.AuthProtocol,
            AuthPassword = snmpOptions.AuthPassword,
            PrivacyProtocol = string.IsNullOrWhiteSpace(snmpOptions.PrivacyProtocol) ? "AES128" : snmpOptions.PrivacyProtocol,
            PrivacyPassword = snmpOptions.PrivacyPassword,
            TimeoutMs = snmpOptions.TimeoutMs > 0 ? snmpOptions.TimeoutMs : 3500
        };
        var source = $"{protocol} ipNetToMedia";

        try
        {
            var rows = await _snmpClient.WalkRawAsync(options, IpNetToMediaPhysAddress, cancellationToken)
                .ConfigureAwait(false);
            var clients = ParseIpNetToMediaClients(
                    rows,
                    basePrefix,
                    localDevices ?? Array.Empty<WifiLanDevice>(),
                    source)
                .OrderBy(client => LastOctet(client.IpAddress))
                .ThenBy(client => client.IpAddress, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var status = clients.Length == 0
                ? $"Router/AP {connection.Ipv4Gateway} responded, but no clients from subnet {basePrefix}0/24 were reported."
                : $"Router/AP {connection.Ipv4Gateway} reported {clients.Length} client(s) via {source}.";

            return new WifiRouterClientScanResult(
                GatewayIp: connection.Ipv4Gateway,
                Source: source,
                Status: status,
                Clients: clients,
                IsSuccess: true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new WifiRouterClientScanResult(
                GatewayIp: connection.Ipv4Gateway,
                Source: source,
                Status: $"Router/AP did not provide SNMP client data: {SanitizeError(ex.Message, options)}",
                Clients: Array.Empty<WifiRouterClient>(),
                IsSuccess: false);
        }
    }

    private static IReadOnlyList<WifiRouterClient> ParseIpNetToMediaClients(
        IReadOnlyDictionary<string, string> rawRows,
        string basePrefix,
        IReadOnlyCollection<WifiLanDevice> localDevices)
        => ParseIpNetToMediaClients(rawRows, basePrefix, localDevices, "SNMP ipNetToMedia");

    private static IReadOnlyList<WifiRouterClient> ParseIpNetToMediaClients(
        IReadOnlyDictionary<string, string> rawRows,
        string basePrefix,
        IReadOnlyCollection<WifiLanDevice> localDevices,
        string source)
    {
        var localByIp = localDevices
            .GroupBy(device => device.IpAddress, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        var clients = new Dictionary<string, WifiRouterClient>(StringComparer.OrdinalIgnoreCase);
        foreach (var (oid, rawMac) in rawRows)
        {
            if (!TryParseIpNetToMediaOid(oid, out var interfaceIndex, out var ip) ||
                !ip.StartsWith(basePrefix, StringComparison.Ordinal) ||
                ip.EndsWith(".0", StringComparison.Ordinal) ||
                ip.EndsWith(".255", StringComparison.Ordinal))
            {
                continue;
            }

            var mac = NormalizeSnmpMac(rawMac);
            if (string.IsNullOrWhiteSpace(mac))
            {
                continue;
            }

            localByIp.TryGetValue(ip, out var local);
            clients[ip] = new WifiRouterClient(
                IpAddress: ip,
                MacAddress: mac,
                HostName: local?.HostName,
                Vendor: local?.Vendor ?? WifiOuiLookup.DescribeVendor(mac),
                InterfaceIndex: interfaceIndex,
                IsAlsoDetectedLocally: local is not null,
                Source: $"Router/AP {source}");
        }

        return clients.Values.ToArray();
    }

    private static bool TryParseIpNetToMediaOid(string oid, out string interfaceIndex, out string ipAddress)
    {
        interfaceIndex = string.Empty;
        ipAddress = string.Empty;

        if (string.IsNullOrWhiteSpace(oid) ||
            !oid.StartsWith(IpNetToMediaPhysAddress + ".", StringComparison.Ordinal))
        {
            return false;
        }

        var suffix = oid[(IpNetToMediaPhysAddress.Length + 1)..]
            .Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (suffix.Length < 5 ||
            !suffix.TakeLast(4).All(part => byte.TryParse(part, out _)))
        {
            return false;
        }

        interfaceIndex = string.Join('.', suffix[..^4]);
        ipAddress = string.Join('.', suffix[^4..]);
        return IPAddress.TryParse(ipAddress, out var parsed) &&
               parsed.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork;
    }

    private static string? NormalizeSnmpMac(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var text = raw.Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            text = text[2..];
        }

        var hex = Regex.Matches(text, "[0-9A-Fa-f]{2}")
            .Select(match => match.Value.ToUpperInvariant())
            .ToArray();
        if (hex.Length < 6 && Regex.IsMatch(text, "^[0-9A-Fa-f]{12}$"))
        {
            hex = Enumerable.Range(0, 6)
                .Select(i => text.Substring(i * 2, 2).ToUpperInvariant())
                .ToArray();
        }

        if (hex.Length < 6)
        {
            return null;
        }

        var mac = string.Join(':', hex.Take(6));
        return mac == "00:00:00:00:00:00" || mac == "FF:FF:FF:FF:FF:FF"
            ? null
            : mac;
    }

    private static int LastOctet(string ip)
    {
        var dot = ip.LastIndexOf('.');
        return dot >= 0 && int.TryParse(ip[(dot + 1)..], out var value) ? value : 0;
    }

    private static bool IsPrivateIpv4(string ip)
    {
        if (!IPAddress.TryParse(ip, out var address))
        {
            return false;
        }

        var bytes = address.GetAddressBytes();
        return bytes.Length == 4 &&
               (bytes[0] == 10 ||
                bytes[0] == 172 && bytes[1] is >= 16 and <= 31 ||
                bytes[0] == 192 && bytes[1] == 168);
    }

    private static string SanitizeError(string message, SnmpSessionOptions? options = null)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return "no response or access denied.";
        }

        var oneLine = message.Replace('\r', ' ').Replace('\n', ' ').Trim();
        foreach (var secret in new[]
        {
            options?.Community,
            options?.UserName,
            options?.AuthPassword,
            options?.PrivacyPassword
        })
        {
            if (!string.IsNullOrWhiteSpace(secret))
            {
                oneLine = oneLine.Replace(secret, "***", StringComparison.OrdinalIgnoreCase);
            }
        }

        return oneLine.Length <= 160 ? oneLine : oneLine[..160] + "...";
    }
}
