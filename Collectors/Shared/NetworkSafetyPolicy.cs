using System.Net;
using System.Net.Sockets;

namespace NetScopeDiagnosticCenter.Collectors.Shared;

/// <summary>
/// Validates and constrains IP addresses/ranges for safe scanning.
/// Extracted from duplicate logic in PrinterDiscoveryCollector and NetworkDeviceCollector.
/// </summary>
public static class NetworkSafetyPolicy
{
    /// <summary>Returns <c>true</c> when the address is in a private IPv4 range (RFC 1918).</summary>
    public static bool IsPrivateIpv4(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes[0] == 10 ||
               (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) ||
               (bytes[0] == 192 && bytes[1] == 168);
    }

    /// <summary>Returns <c>true</c> when the address is in the APIPA range (169.254.x.x).</summary>
    public static bool IsApipa(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes[0] == 169 && bytes[1] == 254;
    }

    /// <summary>Converts an IPv4 address to a host-order 32-bit unsigned integer.</summary>
    public static uint ToUInt32(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
    }

    /// <summary>Converts a host-order 32-bit unsigned integer to an IPv4 address.</summary>
    public static IPAddress FromUInt32(uint value)
    {
        return new IPAddress(
        [
            (byte)(value >> 24),
            (byte)(value >> 16),
            (byte)(value >> 8),
            (byte)value
        ]);
    }

    /// <summary>Returns the subnet mask for the given CIDR prefix length.</summary>
    public static uint Mask(int prefix)
    {
        return prefix == 0 ? 0 : uint.MaxValue << (32 - prefix);
    }

    /// <summary>Estimates the number of usable hosts in a CIDR prefix.</summary>
    public static int EstimateUsableHosts(int prefix)
    {
        if (prefix >= 32) return 1;
        if (prefix == 31) return 2;
        if (prefix <= 0) return int.MaxValue;

        var total = 1L << (32 - prefix);
        var usable = Math.Max(0, total - 2);
        return usable > int.MaxValue ? int.MaxValue : (int)usable;
    }

    /// <summary>
    /// Throws <see cref="InvalidOperationException"/> when the address is not private.
    /// </summary>
    public static void EnsurePrivate(IPAddress address)
    {
        if (!IsPrivateIpv4(address))
            throw new InvalidOperationException(
                "Public IP scanning is not allowed. Enter an authorized private IPv4 target.");
    }

    /// <summary>
    /// Returns <c>true</c> when the address is a valid private IPv4 that is not APIPA.
    /// </summary>
    public static bool IsScannable(IPAddress address)
    {
        return address.AddressFamily == AddressFamily.InterNetwork &&
               IsPrivateIpv4(address) &&
               !IsApipa(address);
    }
}
