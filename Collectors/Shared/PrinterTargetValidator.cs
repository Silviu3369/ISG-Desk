using System.Net;
using System.Net.Sockets;

namespace NetScopeDiagnosticCenter.Collectors.Shared;

public static class PrinterTargetValidator
{
    private const int MaxPrintServerLength = 63;
    private const int MaxShareNameLength = 128;

    public static bool TryNormalizePrintServer(string? value, out string normalized, out string reason)
    {
        normalized = string.Empty;
        reason = string.Empty;

        if (string.IsNullOrWhiteSpace(value))
        {
            reason = "Print server target is empty.";
            return false;
        }

        var trimmed = value.Trim().TrimStart('\\');
        if (trimmed.Length == 0 || trimmed.Length > MaxPrintServerLength)
        {
            reason = $"Print server target must be 1-{MaxPrintServerLength} characters.";
            return false;
        }

        if (trimmed.Any(char.IsControl) || trimmed.Any(char.IsWhiteSpace))
        {
            reason = "Print server target must not contain spaces or control characters.";
            return false;
        }

        if (trimmed.Contains('\\') || trimmed.Contains('/'))
        {
            reason = "Enter only the print server name or private IPv4 address, not a queue path.";
            return false;
        }

        if (IPAddress.TryParse(trimmed, out var ip))
        {
            if (ip.AddressFamily == AddressFamily.InterNetwork && NetworkSafetyPolicy.IsPrivateIpv4(ip))
            {
                normalized = ip.ToString();
                return true;
            }

            reason = "Print server IP must be a private IPv4 (10/8, 172.16/12, 192.168/16).";
            return false;
        }

        if (trimmed.Contains('.'))
        {
            reason = "Print server target must be a single-label LAN name (no dots) or a private IPv4; public FQDNs are not allowed.";
            return false;
        }

        foreach (var c in trimmed)
        {
            if (!(char.IsLetterOrDigit(c) || c is '-' or '_'))
            {
                reason = "Print server name may contain only letters, digits, '-' or '_'.";
                return false;
            }
        }

        normalized = trimmed;
        return true;
    }

    public static bool TryNormalizeSharedQueueConnection(string? value, out string normalized, out string reason)
    {
        normalized = string.Empty;
        reason = string.Empty;

        if (string.IsNullOrWhiteSpace(value))
        {
            reason = "A valid shared printer connection is required.";
            return false;
        }

        var trimmed = value.Trim();
        if (!trimmed.StartsWith(@"\\", StringComparison.Ordinal))
        {
            reason = @"Expected format: \\PrintServer\ShareName.";
            return false;
        }

        var parts = trimmed[2..].Split('\\', StringSplitOptions.None);
        if (parts.Length != 2)
        {
            reason = @"Expected format: \\PrintServer\ShareName.";
            return false;
        }

        if (!TryNormalizePrintServer(parts[0], out var server, out reason))
        {
            return false;
        }

        var share = parts[1].Trim();
        if (share.Length == 0 || share.Length > MaxShareNameLength)
        {
            reason = $"Printer share name must be 1-{MaxShareNameLength} characters.";
            return false;
        }

        if (share.Any(char.IsControl))
        {
            reason = "Printer share name must not contain control characters.";
            return false;
        }

        normalized = $@"\\{server}\{share}";
        return true;
    }
}
