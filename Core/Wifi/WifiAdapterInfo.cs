using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace NetScopeDiagnosticCenter.Core.Wifi;

/// <summary>
/// Pulls L3 / adapter facts for the active Wi-Fi NIC straight from
/// <see cref="NetworkInterface"/> (pure managed BCL — no PowerShell, no P/Invoke, no
/// admin). This is the reliable source for:
///   • IPv4 address / subnet mask / default gateway / DNS servers
///   • the adapter's physical (MAC) address + friendly description + speed
///
/// The Wi-Fi NIC is identified as the first operational
/// <see cref="NetworkInterfaceType.Wireless80211"/> interface. We deliberately do NOT
/// use the WLAN GUID to match — NetworkInterface.Id is the GUID string on Windows, but
/// matching by type is simpler and works even when only one wireless NIC exists (the
/// overwhelming common case).
/// </summary>
public static class WifiAdapterInfo
{
    public sealed record Snapshot(
        string? Ipv4Address,
        string? Ipv4SubnetMask,
        string? Ipv4Gateway,
        IReadOnlyList<string> DnsServers,
        string? MacAddress,
        string? Description);

    /// <summary>
    /// Returns the active Wi-Fi adapter's L3 + identity facts, or an all-null snapshot
    /// when no operational wireless NIC exists (desktop on Ethernet, radio off, etc.).
    /// Never throws — any reflection/enumeration failure yields the empty snapshot.
    /// </summary>
    public static Snapshot GetActiveWifi(Guid? preferGuid = null)
    {
        try
        {
            var nics = NetworkInterface.GetAllNetworkInterfaces();

            // Prefer the NIC whose GUID matches the WLAN interface (NetworkInterface.Id is
            // "{GUID}" on Windows); fall back to first operational Wireless80211.
            NetworkInterface? nic = null;
            if (preferGuid is { } g)
            {
                var idStr = "{" + g.ToString().ToUpperInvariant() + "}";
                nic = nics.FirstOrDefault(n =>
                    string.Equals(n.Id, idStr, StringComparison.OrdinalIgnoreCase));
            }
            nic ??= nics.FirstOrDefault(n =>
                        n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 &&
                        n.OperationalStatus == OperationalStatus.Up)
                    ?? nics.FirstOrDefault(n =>
                        n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211);

            if (nic is null)
            {
                return new Snapshot(null, null, null, Array.Empty<string>(), null, null);
            }

            var props = nic.GetIPProperties();

            var ipv4 = props.UnicastAddresses
                .FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork);
            var gateway = props.GatewayAddresses
                .FirstOrDefault(gw => gw.Address.AddressFamily == AddressFamily.InterNetwork);
            var dns = props.DnsAddresses
                .Where(a => a.AddressFamily == AddressFamily.InterNetwork)
                .Select(a => a.ToString())
                .ToArray();

            var mac = FormatMac(nic.GetPhysicalAddress());

            return new Snapshot(
                Ipv4Address: ipv4?.Address.ToString(),
                Ipv4SubnetMask: ipv4 is not null ? PrefixToMask(ipv4.PrefixLength) : null,
                Ipv4Gateway: gateway?.Address.ToString(),
                DnsServers: dns,
                MacAddress: mac,
                Description: nic.Description);
        }
        catch
        {
            return new Snapshot(null, null, null, Array.Empty<string>(), null, null);
        }
    }

    /// <summary>
    /// Cumulative byte counters for the active Wi-Fi NIC, used to derive REAL throughput
    /// (the sampler diffs two readings: Δbytes × 8 ÷ Δseconds = bits/s). Returns null when
    /// no wireless NIC is up or the OS doesn't expose statistics. Never throws.
    /// </summary>
    public static (long RxBytes, long TxBytes)? GetTrafficCounters(Guid? preferGuid = null)
    {
        try
        {
            var nic = FindWifiNic(NetworkInterface.GetAllNetworkInterfaces(), preferGuid);
            if (nic is null) return null;

            // IPv4InterfaceStatistics is the broadest counter (all L3 traffic on the NIC);
            // GetIPStatistics() on Windows surfaces the same Bytes* fields for Wi-Fi.
            var stats = nic.GetIPStatistics();
            return (stats.BytesReceived, stats.BytesSent);
        }
        catch
        {
            return null;
        }
    }

    private static NetworkInterface? FindWifiNic(NetworkInterface[] nics, Guid? preferGuid)
    {
        NetworkInterface? nic = null;
        if (preferGuid is { } g)
        {
            var idStr = "{" + g.ToString().ToUpperInvariant() + "}";
            nic = nics.FirstOrDefault(n =>
                string.Equals(n.Id, idStr, StringComparison.OrdinalIgnoreCase));
        }
        return nic
               ?? nics.FirstOrDefault(n =>
                   n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 &&
                   n.OperationalStatus == OperationalStatus.Up)
               ?? nics.FirstOrDefault(n =>
                   n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211);
    }

    private static string? FormatMac(PhysicalAddress addr)
    {
        var bytes = addr.GetAddressBytes();
        if (bytes.Length != 6) return null;
        return string.Join(":", bytes.Select(b => b.ToString("X2")));
    }

    /// <summary>CIDR prefix length → dotted subnet mask ("24" → "255.255.255.0").</summary>
    private static string PrefixToMask(int prefixLength)
    {
        if (prefixLength is < 0 or > 32) return string.Empty;
        uint mask = prefixLength == 0 ? 0u : 0xFFFFFFFFu << (32 - prefixLength);
        return $"{(mask >> 24) & 0xFF}.{(mask >> 16) & 0xFF}.{(mask >> 8) & 0xFF}.{mask & 0xFF}";
    }
}
