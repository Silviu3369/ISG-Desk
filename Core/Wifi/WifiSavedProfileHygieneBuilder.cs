using NetScopeDiagnosticCenter.Core.Models.Wifi;

namespace NetScopeDiagnosticCenter.Core.Wifi;

public static class WifiSavedProfileHygieneBuilder
{
    private static readonly string[] PublicProfileHints =
    [
        "guest",
        "public",
        "free",
        "hotel",
        "airport",
        "cafe",
        "coffee",
        "open",
        "wifi",
        "hotspot",
        "mall",
        "train",
        "station"
    ];

    public static WifiSavedProfileHygieneReport Build(
        WifiConnectionDetails connection,
        IReadOnlyList<WifiAccessPoint> visibleAps,
        IReadOnlyList<WifiSavedProfile> savedProfiles,
        DateTimeOffset? generatedAt = null)
    {
        var now = generatedAt ?? DateTimeOffset.Now;
        var profiles = savedProfiles ?? Array.Empty<WifiSavedProfile>();
        if (profiles.Count == 0)
        {
            return new WifiSavedProfileHygieneReport(
                Summary: "No saved Wi-Fi profiles were reported by Windows WLAN service.",
                Items: Array.Empty<WifiSavedProfileHygieneItem>(),
                GeneratedAt: now);
        }

        var visibleBySsid = (visibleAps ?? Array.Empty<WifiAccessPoint>())
            .Where(ap => !ap.IsHidden && !string.IsNullOrWhiteSpace(ap.Ssid))
            .GroupBy(ap => ap.Ssid.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.OrdinalIgnoreCase);

        var items = profiles
            .Select(profile => BuildItem(connection, visibleBySsid, profile))
            .OrderByDescending(item => SeverityRank(item.SeverityDisplay))
            .ThenBy(item => item.ProfileName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new WifiSavedProfileHygieneReport(
            Summary: BuildSummary(items, profiles.Count),
            Items: items,
            GeneratedAt: now);
    }

    private static WifiSavedProfileHygieneItem BuildItem(
        WifiConnectionDetails connection,
        IReadOnlyDictionary<string, WifiAccessPoint[]> visibleBySsid,
        WifiSavedProfile profile)
    {
        var name = profile.DisplayName.Trim();
        visibleBySsid.TryGetValue(name, out var matches);
        matches ??= Array.Empty<WifiAccessPoint>();

        var isCurrent = IsCurrentProfile(connection, name);
        var isVisible = matches.Length > 0;
        var state = isCurrent ? "Current" : isVisible ? "Visible now" : "Not visible";
        var security = BuildSecurityText(matches);
        var looksPublic = LooksPublic(name);
        var labels = matches
            .Select(ap => ap.Security.Label)
            .Where(label => !string.IsNullOrWhiteSpace(label))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var hasOpen = matches.Any(ap => ap.Security.IsOpen);
        var hasSecured = matches.Any(ap => !ap.Security.IsOpen);
        var hasLegacy = matches.Any(ap => ap.Security.IsLegacyInsecure);

        if (hasOpen && hasSecured)
        {
            return new WifiSavedProfileHygieneItem(
                Severity: "Critical",
                ProfileName: name,
                State: state,
                Security: security,
                Evidence: $"Visible as mixed security modes: {string.Join(", ", labels)}.",
                Recommendation: "Check AP/controller configuration and remove the saved profile until a rogue/evil-twin risk is ruled out.");
        }

        if (hasOpen)
        {
            return new WifiSavedProfileHygieneItem(
                Severity: isCurrent ? "Critical" : "Warning",
                ProfileName: name,
                State: state,
                Security: security,
                Evidence: isCurrent
                    ? "This PC is currently connected to a saved open Wi-Fi profile."
                    : "Saved profile matches a currently visible open Wi-Fi network.",
                Recommendation: "Remove this saved profile unless it is explicitly required; avoid auto-connect to open networks.");
        }

        if (hasLegacy)
        {
            return new WifiSavedProfileHygieneItem(
                Severity: isCurrent ? "Critical" : "Warning",
                ProfileName: name,
                State: state,
                Security: security,
                Evidence: "Visible AP security includes legacy or weak encryption.",
                Recommendation: "Move the network to WPA2-AES/WPA3 and remove the saved profile after migration if it is no longer needed.");
        }

        if (looksPublic)
        {
            return new WifiSavedProfileHygieneItem(
                Severity: "Warning",
                ProfileName: name,
                State: state,
                Security: security,
                Evidence: "Profile name looks like a public, guest or hotspot network.",
                Recommendation: "Verify business need and remove the profile if it is not trusted or no longer used.");
        }

        if (!isVisible)
        {
            return new WifiSavedProfileHygieneItem(
                Severity: "Info",
                ProfileName: name,
                State: state,
                Security: security,
                Evidence: "Profile was saved on this PC but was not seen in the latest active scan.",
                Recommendation: "Review as a stale-profile candidate; keep it only if the user still needs it.");
        }

        return new WifiSavedProfileHygieneItem(
            Severity: "OK",
            ProfileName: name,
            State: state,
            Security: security,
            Evidence: isCurrent
                ? "This is the active Wi-Fi profile and the visible security mode is not open or legacy."
                : "Profile is visible and no obvious saved-profile risk was detected.",
            Recommendation: "No cleanup needed from available local data.");
    }

    private static string BuildSummary(IReadOnlyList<WifiSavedProfileHygieneItem> items, int profileCount)
    {
        var critical = items.Count(item => item.SeverityDisplay == "Critical");
        var warning = items.Count(item => item.SeverityDisplay == "Warning");
        var info = items.Count(item => item.SeverityDisplay == "Info");

        var prefix = profileCount == 1
            ? "1 saved Wi-Fi profile audited"
            : $"{profileCount} saved Wi-Fi profiles audited";

        if (critical > 0)
        {
            return $"{prefix}: {critical} critical, {warning} warning, {info} informational.";
        }

        if (warning > 0)
        {
            return $"{prefix}: {warning} warning, {info} informational.";
        }

        if (info > 0)
        {
            return $"{prefix}: no high-risk profiles, {info} stale-profile candidate(s).";
        }

        return $"{prefix}: no saved-profile hygiene issues detected.";
    }

    private static string BuildSecurityText(IReadOnlyList<WifiAccessPoint> matches)
    {
        if (matches.Count == 0)
        {
            return "Not observed";
        }

        var labels = matches
            .Select(ap => ap.Security.Label)
            .Where(label => !string.IsNullOrWhiteSpace(label))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(label => label, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return labels.Length == 0 ? "Unknown" : string.Join(", ", labels);
    }

    private static bool IsCurrentProfile(WifiConnectionDetails connection, string profileName)
    {
        if (!connection.IsConnected || string.IsNullOrWhiteSpace(profileName))
        {
            return false;
        }

        return string.Equals(connection.ProfileName, profileName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(connection.Ssid, profileName, StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksPublic(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        return PublicProfileHints.Any(hint => name.Contains(hint, StringComparison.OrdinalIgnoreCase));
    }

    private static int SeverityRank(string severity) => severity switch
    {
        "Critical" => 4,
        "Warning" => 3,
        "Info" => 2,
        "OK" => 1,
        _ => 0,
    };
}
