using System.Net;
using Lextm.SharpSnmpLib;
using Lextm.SharpSnmpLib.Messaging;
using Lextm.SharpSnmpLib.Security;
using NetScopeDiagnosticCenter.Core.Models;

namespace NetScopeDiagnosticCenter.Infrastructure;

#pragma warning disable CS0618 // Compatibility options are exposed explicitly; secure defaults remain SHA256 + AES128.
public sealed class SnmpClientService
{
    private const int SnmpPort = 161;
    private const int MaxWalkRows = 512;

    public async Task<IPAddress> ResolveIpv4Async(string target, CancellationToken cancellationToken = default)
    {
        if (IPAddress.TryParse(target, out var parsed) && parsed.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            EnsurePrivateIpv4(parsed);
            return parsed;
        }

        var addresses = await Dns.GetHostAddressesAsync(target, cancellationToken);
        var address = addresses.FirstOrDefault(address => address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            ?? throw new InvalidOperationException("No IPv4 address was returned for target.");
        EnsurePrivateIpv4(address);
        return address;
    }

    public async Task<Dictionary<string, string>> GetAsync(
        SnmpSessionOptions options,
        IEnumerable<string> oids,
        CancellationToken cancellationToken = default)
    {
        var address = await ResolveIpv4Async(options.Target, cancellationToken);
        var endpoint = new IPEndPoint(address, SnmpPort);
        var variables = oids
            .Select(oid => new Variable(new ObjectIdentifier(oid), new Null()))
            .ToList();

        IList<Variable> result = options.Protocol == SnmpProtocolVersion.V3AuthPriv
            ? await GetV3Async(options, endpoint, variables, cancellationToken)
            : await Messenger.GetAsync(
                    VersionCode.V2,
                    endpoint,
                    new OctetString(string.IsNullOrWhiteSpace(options.Community) ? "public" : options.Community),
                    variables,
                    cancellationToken)
                .WaitAsync(TimeSpan.FromMilliseconds(options.TimeoutMs), cancellationToken);

        return result.ToDictionary(
            variable => variable.Id.ToString(),
            variable => FormatSnmpValue(variable.Data),
            StringComparer.Ordinal);
    }

    public async Task<Dictionary<string, string>> WalkRawAsync(
        SnmpSessionOptions options,
        string baseOid,
        CancellationToken cancellationToken = default)
    {
        var address = await ResolveIpv4Async(options.Target, cancellationToken);
        var endpoint = new IPEndPoint(address, SnmpPort);
        var variables = new List<Variable>();

        if (options.Protocol == SnmpProtocolVersion.V3AuthPriv)
        {
            var privacy = CreatePrivacyProvider(options);
            var report = await CreateV3DiscoveryReportAsync(endpoint, options.TimeoutMs, cancellationToken);
            await Messenger.BulkWalkAsync(
                    VersionCode.V3,
                    endpoint,
                    new OctetString(options.UserName),
                    OctetString.Empty,
                    new ObjectIdentifier(baseOid),
                    variables,
                    10,
                    WalkMode.WithinSubtree,
                    privacy,
                    report,
                    cancellationToken)
                .WaitAsync(TimeSpan.FromMilliseconds(options.TimeoutMs), cancellationToken);
        }
        else
        {
            await Messenger.WalkAsync(
                    VersionCode.V2,
                    endpoint,
                    new OctetString(string.IsNullOrWhiteSpace(options.Community) ? "public" : options.Community),
                    new ObjectIdentifier(baseOid),
                    variables,
                    WalkMode.WithinSubtree,
                    cancellationToken)
                .WaitAsync(TimeSpan.FromMilliseconds(options.TimeoutMs), cancellationToken);
        }

        return variables
            .Take(MaxWalkRows)
            .ToDictionary(
                variable => variable.Id.ToString(),
                variable => FormatSnmpValue(variable.Data),
                StringComparer.Ordinal);
    }

    private static async Task<IList<Variable>> GetV3Async(
        SnmpSessionOptions options,
        IPEndPoint endpoint,
        IList<Variable> variables,
        CancellationToken cancellationToken)
    {
        var privacy = CreatePrivacyProvider(options);
        var report = await CreateV3DiscoveryReportAsync(endpoint, options.TimeoutMs, cancellationToken);
        var request = new GetRequestMessage(
            VersionCode.V3,
            Messenger.NextMessageId,
            Messenger.NextRequestId,
            new OctetString(options.UserName),
            variables,
            privacy,
            Messenger.MaxMessageSize,
            report);
        var registry = new UserRegistry();
        registry.Add(new OctetString(options.UserName), privacy);
        var reply = await request.GetResponseAsync(endpoint, registry, cancellationToken)
            .WaitAsync(TimeSpan.FromMilliseconds(options.TimeoutMs), cancellationToken);
        return reply.Pdu().Variables;
    }

    private static async Task<ISnmpMessage> CreateV3DiscoveryReportAsync(
        IPEndPoint endpoint,
        int timeoutMs,
        CancellationToken cancellationToken)
    {
        var discovery = Messenger.GetNextDiscovery(SnmpType.GetRequestPdu);
        return await discovery.GetResponseAsync(endpoint, cancellationToken)
            .WaitAsync(TimeSpan.FromMilliseconds(timeoutMs), cancellationToken);
    }

    private static IPrivacyProvider CreatePrivacyProvider(SnmpSessionOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.UserName) ||
            string.IsNullOrWhiteSpace(options.AuthPassword) ||
            string.IsNullOrWhiteSpace(options.PrivacyPassword))
        {
            throw new InvalidOperationException("SNMPv3 authPriv requires username, authentication password and privacy password.");
        }

        var auth = CreateAuthenticationProvider(options.AuthProtocol, options.AuthPassword);
        var privacyPassword = new OctetString(options.PrivacyPassword);
        return options.PrivacyProtocol.ToUpperInvariant() switch
        {
            "AES256" => new AES256PrivacyProvider(privacyPassword, auth),
            "AES192" => new AES192PrivacyProvider(privacyPassword, auth),
            "AES128" or "AES" => new AESPrivacyProvider(privacyPassword, auth),
            "DES" => new DESPrivacyProvider(privacyPassword, auth),
            _ => new AESPrivacyProvider(privacyPassword, auth)
        };
    }

    private static IAuthenticationProvider CreateAuthenticationProvider(string protocol, string password)
    {
        var phrase = new OctetString(password);
        return protocol.ToUpperInvariant() switch
        {
            "SHA512" => new SHA512AuthenticationProvider(phrase),
            "SHA384" => new SHA384AuthenticationProvider(phrase),
            "SHA256" => new SHA256AuthenticationProvider(phrase),
            "SHA1" => new SHA1AuthenticationProvider(phrase),
            "MD5" => new MD5AuthenticationProvider(phrase),
            _ => new SHA256AuthenticationProvider(phrase)
        };
    }

    private static string FormatSnmpValue(ISnmpData data)
    {
        var text = data.ToString();
        return string.Equals(text, "Null", StringComparison.OrdinalIgnoreCase) ? string.Empty : text;
    }

    private static void EnsurePrivateIpv4(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        var isPrivate =
            bytes[0] == 10 ||
            (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) ||
            (bytes[0] == 192 && bytes[1] == 168);
        if (!isPrivate)
        {
            throw new InvalidOperationException("SNMP target must resolve to an authorized private IPv4 address.");
        }
    }
}
#pragma warning restore CS0618
