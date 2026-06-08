using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace NetScopeDiagnosticCenter.Core.Monitoring;

/// <summary>
/// Lightweight default-gateway lookup, used as the initial target for the live status
/// monitor in the sidebar. Returns the IPv4 gateway of the first UP, non-loopback adapter.
/// </summary>
public static class GatewayDetector
{
    /// <summary>
    /// Finds the first IPv4 default gateway on an UP interface. Skips loopback and
    /// virtual adapters that commonly sit on top (Hyper-V, VMware, VPN tunnels).
    /// Returns <c>null</c> when no usable gateway is reachable.
    /// </summary>
    public static string? DetectDefaultGateway()
    {
        IEnumerable<NetworkInterface> interfaces;
        try
        {
            interfaces = NetworkInterface.GetAllNetworkInterfaces();
        }
        catch
        {
            return null;
        }

        foreach (var nic in interfaces)
        {
            try
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                // Skip pseudo / virtual adapters that often advertise gateways but are not
                // representative of the real upstream link.
                if (IsVirtualAdapter(nic.Name) || IsVirtualAdapter(nic.Description)) continue;

                var gateway = nic.GetIPProperties().GatewayAddresses
                    .FirstOrDefault(g => g.Address.AddressFamily == AddressFamily.InterNetwork
                                      && !IPAddress.None.Equals(g.Address)
                                      && !IPAddress.Any.Equals(g.Address));
                if (gateway is not null)
                {
                    return gateway.Address.ToString();
                }
            }
            catch
            {
                // A single broken adapter should not block app startup.
            }
        }
        return null;
    }

    private static bool IsVirtualAdapter(string text) =>
        text.Contains("Loopback", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("vEthernet", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("VirtualBox", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("VMware", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("Docker", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("WSL", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("TAP", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("TUN", StringComparison.OrdinalIgnoreCase);
}
