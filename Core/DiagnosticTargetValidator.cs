using System.Net;

namespace NetScopeDiagnosticCenter.Core;

public static class DiagnosticTargetValidator
{
    private const int MaxHostLength = 253;
    private const int MaxSharePathLength = 512;

    public static bool TryNormalizeHostOrShare(string? value, out string normalized, out string reason)
    {
        return TryNormalizeHostOrShare(value, out normalized, out _, out reason);
    }

    public static bool TryNormalizeHostOrShare(
        string? value,
        out string normalizedHost,
        out string normalizedSharePath,
        out string reason)
    {
        normalizedHost = string.Empty;
        normalizedSharePath = string.Empty;
        reason = string.Empty;

        if (string.IsNullOrWhiteSpace(value))
        {
            reason = "Enter a gateway, host name or IP address.";
            return false;
        }

        var candidate = value.Trim();
        if (candidate.StartsWith(@"\\", StringComparison.Ordinal))
        {
            candidate = candidate.TrimStart('\\');
            var slashIndex = candidate.IndexOf('\\', StringComparison.Ordinal);
            var host = slashIndex >= 0 ? candidate[..slashIndex] : candidate;
            if (!TryNormalizeHost(host, out normalizedHost, out reason))
            {
                return false;
            }

            if (slashIndex < 0)
            {
                return true;
            }

            var shareRemainder = candidate[(slashIndex + 1)..].TrimEnd('\\');
            if (string.IsNullOrWhiteSpace(shareRemainder))
            {
                return true;
            }

            if (shareRemainder.Length > MaxSharePathLength)
            {
                reason = $"Share path must be {MaxSharePathLength} characters or less.";
                return false;
            }

            if (shareRemainder.Any(char.IsControl))
            {
                reason = "Share path must not contain control characters.";
                return false;
            }

            if (shareRemainder.Any(IsInvalidSharePathChar) ||
                shareRemainder.Split('\\').Any(segment => segment.Length == 0))
            {
                reason = "Share path contains invalid characters.";
                return false;
            }

            normalizedSharePath = $@"\\{normalizedHost}\{shareRemainder}";
            return true;
        }

        return TryNormalizeHost(candidate, out normalizedHost, out reason);
    }

    public static bool TryNormalizeHost(string? value, out string normalized, out string reason)
    {
        normalized = string.Empty;
        reason = string.Empty;

        if (string.IsNullOrWhiteSpace(value))
        {
            reason = "Enter a gateway, host name or IP address.";
            return false;
        }

        normalized = value.Trim();
        if (normalized.Length > MaxHostLength)
        {
            reason = $"Target must be {MaxHostLength} characters or less.";
            return false;
        }

        if (normalized.Any(char.IsControl))
        {
            reason = "Target must not contain control characters.";
            return false;
        }

        if (normalized.Any(char.IsWhiteSpace))
        {
            reason = "Target must be a host name or IP address without spaces.";
            return false;
        }

        if (IPAddress.TryParse(normalized, out _))
        {
            return true;
        }

        if (IsValidHostName(normalized))
        {
            return true;
        }

        reason = "Target must be a host name or IP address without protocol, path or special characters.";
        return false;
    }

    private static bool IsValidHostName(string value)
    {
        var host = value.EndsWith(".", StringComparison.Ordinal) ? value[..^1] : value;
        if (host.Length == 0 || host.Length > MaxHostLength)
        {
            return false;
        }

        var labels = host.Split('.');
        foreach (var label in labels)
        {
            if (label.Length is 0 or > 63 ||
                label[0] == '-' ||
                label[^1] == '-')
            {
                return false;
            }

            foreach (var ch in label)
            {
                if (!IsHostNameChar(ch))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static bool IsHostNameChar(char value) =>
        value is >= 'a' and <= 'z' ||
        value is >= 'A' and <= 'Z' ||
        value is >= '0' and <= '9' ||
        value is '-' or '_';

    private static bool IsInvalidSharePathChar(char value) =>
        value is '<' or '>' or ':' or '"' or '|' or '?' or '*';
}
