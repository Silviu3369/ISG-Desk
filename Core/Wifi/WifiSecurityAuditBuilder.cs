using NetScopeDiagnosticCenter.Core.Models.Wifi;

namespace NetScopeDiagnosticCenter.Core.Wifi;

public static class WifiSecurityAuditBuilder
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
        "wifi"
    ];

    public static WifiSecurityAuditReport Build(
        WifiConnectionDetails connection,
        IReadOnlyList<WifiAccessPoint> visibleAps,
        IReadOnlyList<WifiSavedProfile> savedProfiles,
        WifiAdapterCapabilities capabilities,
        DateTimeOffset? generatedAt = null)
    {
        var now = generatedAt ?? DateTimeOffset.Now;
        var findings = new List<WifiSecurityAuditFinding>();

        AddConnectionSecurity(findings, connection, capabilities);
        AddHiddenSsidFinding(findings, connection);
        AddVisibleOpenNetworkFinding(findings, visibleAps);
        AddMixedSecuritySsidFinding(findings, connection, visibleAps);
        AddSavedProfileFindings(findings, savedProfiles);

        if (findings.Count == 0)
        {
            findings.Add(new WifiSecurityAuditFinding(
                Severity: "OK",
                Category: "Security posture",
                Finding: "No obvious Wi-Fi security issues detected from available local data.",
                Evidence: "Current security, nearby beacons and saved profile names did not trigger any audit finding.",
                Recommendation: "Keep router/AP firmware updated and prefer WPA3 where available."));
        }

        return new WifiSecurityAuditReport(
            Summary: BuildSummary(findings),
            Findings: findings,
            GeneratedAt: now);
    }

    private static void AddConnectionSecurity(
        List<WifiSecurityAuditFinding> findings,
        WifiConnectionDetails connection,
        WifiAdapterCapabilities capabilities)
    {
        if (!connection.IsConnected)
        {
            findings.Add(new WifiSecurityAuditFinding(
                Severity: "Info",
                Category: "Current connection",
                Finding: "No active Wi-Fi connection.",
                Evidence: "The adapter is not associated to a Wi-Fi network.",
                Recommendation: "Connect to the target Wi-Fi network before running the security audit."));
            return;
        }

        var security = connection.Security;
        if (security is null)
        {
            findings.Add(new WifiSecurityAuditFinding(
                Severity: "Warning",
                Category: "Current connection",
                Finding: "Windows did not report the current Wi-Fi security mode.",
                Evidence: $"SSID {ValueOrUnknown(connection.Ssid)} has no security profile in the current snapshot.",
                Recommendation: "Refresh the connection snapshot or verify security mode on the AP/controller."));
            return;
        }

        if (security.IsOpen)
        {
            findings.Add(new WifiSecurityAuditFinding(
                Severity: "Critical",
                Category: "Current connection",
                Finding: "Current Wi-Fi network is open.",
                Evidence: $"SSID {ValueOrUnknown(connection.Ssid)} uses {security.Label}.",
                Recommendation: "Do not use this network for sensitive work. Move users to WPA2-AES or WPA3."));
            return;
        }

        if (security.IsLegacyInsecure)
        {
            findings.Add(new WifiSecurityAuditFinding(
                Severity: "Critical",
                Category: "Current connection",
                Finding: "Current Wi-Fi network uses legacy or weak encryption.",
                Evidence: $"SSID {ValueOrUnknown(connection.Ssid)} uses {security.Label}.",
                Recommendation: "Disable WEP/WPA/TKIP on the AP and require WPA2-AES or WPA3."));
            return;
        }

        if (security.IsModernSecure && !security.HasForwardSecrecy)
        {
            var recommendation = capabilities.SupportsWpa3
                ? "Enable WPA3-Personal/SAE on the AP if supported by the client population."
                : "WPA2-AES is acceptable, but use WPA3 where client hardware supports it.";
            findings.Add(new WifiSecurityAuditFinding(
                Severity: "Warning",
                Category: "Current connection",
                Finding: "Current Wi-Fi network is modern but lacks WPA3 forward secrecy.",
                Evidence: $"SSID {ValueOrUnknown(connection.Ssid)} uses {security.Label}.",
                Recommendation: recommendation));
            return;
        }

        findings.Add(new WifiSecurityAuditFinding(
            Severity: "OK",
            Category: "Current connection",
            Finding: "Current Wi-Fi security is modern.",
            Evidence: $"SSID {ValueOrUnknown(connection.Ssid)} uses {security.Label}.",
            Recommendation: "Keep the AP firmware updated and keep legacy WPA/WEP/TKIP disabled."));
    }

    private static void AddHiddenSsidFinding(
        List<WifiSecurityAuditFinding> findings,
        WifiConnectionDetails connection)
    {
        if (!connection.IsConnected || !connection.IsHiddenSsid)
        {
            return;
        }

        findings.Add(new WifiSecurityAuditFinding(
            Severity: "Info",
            Category: "SSID visibility",
            Finding: "Connected SSID is hidden.",
            Evidence: "Windows reports the connected network as hidden.",
            Recommendation: "Hidden SSID is not a security control. Disable it unless policy requires it."));
    }

    private static void AddVisibleOpenNetworkFinding(
        List<WifiSecurityAuditFinding> findings,
        IReadOnlyList<WifiAccessPoint> visibleAps)
    {
        var openAps = (visibleAps ?? Array.Empty<WifiAccessPoint>())
            .Where(ap => ap.Security.IsOpen)
            .ToArray();
        if (openAps.Length == 0)
        {
            return;
        }

        var sample = string.Join(", ", openAps
            .Select(ap => ap.DisplaySsid)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(4));
        findings.Add(new WifiSecurityAuditFinding(
            Severity: "Warning",
            Category: "Nearby networks",
            Finding: $"{openAps.Length} open nearby AP(s) are visible.",
            Evidence: string.IsNullOrWhiteSpace(sample) ? "Open beacons were detected." : $"Examples: {sample}.",
            Recommendation: "Avoid connecting company devices to open nearby networks. Remove stale auto-connect profiles if present."));
    }

    private static void AddMixedSecuritySsidFinding(
        List<WifiSecurityAuditFinding> findings,
        WifiConnectionDetails connection,
        IReadOnlyList<WifiAccessPoint> visibleAps)
    {
        if (string.IsNullOrWhiteSpace(connection.Ssid))
        {
            return;
        }

        var sameSsid = (visibleAps ?? Array.Empty<WifiAccessPoint>())
            .Where(ap => !ap.IsHidden && string.Equals(ap.Ssid, connection.Ssid, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (sameSsid.Length < 2)
        {
            return;
        }

        var labels = sameSsid
            .Select(ap => ap.Security.Label)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var hasOpenAndSecured = sameSsid.Any(ap => ap.Security.IsOpen) && sameSsid.Any(ap => !ap.Security.IsOpen);
        if (!hasOpenAndSecured && labels.Length <= 1)
        {
            return;
        }

        findings.Add(new WifiSecurityAuditFinding(
            Severity: hasOpenAndSecured ? "Critical" : "Warning",
            Category: "SSID consistency",
            Finding: "Same SSID is advertised with different security modes.",
            Evidence: $"SSID {connection.Ssid} appears as {string.Join(", ", labels)} across {sameSsid.Length} AP(s).",
            Recommendation: "Verify AP/controller configuration and check for a possible rogue/evil-twin AP."));
    }

    private static void AddSavedProfileFindings(
        List<WifiSecurityAuditFinding> findings,
        IReadOnlyList<WifiSavedProfile> savedProfiles)
    {
        var profiles = savedProfiles ?? Array.Empty<WifiSavedProfile>();
        if (profiles.Count == 0)
        {
            findings.Add(new WifiSecurityAuditFinding(
                Severity: "OK",
                Category: "Saved profiles",
                Finding: "No saved Wi-Fi profiles were reported.",
                Evidence: "Windows WLAN service returned zero saved profile names for this adapter.",
                Recommendation: "No saved-profile cleanup is needed on this adapter."));
            return;
        }

        if (profiles.Count > 20)
        {
            findings.Add(new WifiSecurityAuditFinding(
                Severity: "Info",
                Category: "Saved profiles",
                Finding: "Many saved Wi-Fi profiles are present.",
                Evidence: $"{profiles.Count} saved profile name(s) were returned by Windows.",
                Recommendation: "Review and forget stale profiles to reduce accidental auto-connect risk."));
        }

        var publicLike = profiles
            .Where(profile => LooksPublic(profile.DisplayName))
            .Select(profile => profile.DisplayName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(5)
            .ToArray();
        if (publicLike.Length > 0)
        {
            findings.Add(new WifiSecurityAuditFinding(
                Severity: "Warning",
                Category: "Saved profiles",
                Finding: "Saved profile names suggest public or guest networks.",
                Evidence: $"Examples: {string.Join(", ", publicLike)}.",
                Recommendation: "Verify these profiles are still needed and remove auto-connect for untrusted public networks."));
        }
    }

    private static string BuildSummary(IReadOnlyList<WifiSecurityAuditFinding> findings)
    {
        var critical = findings.Count(f => f.Severity == "Critical");
        var warning = findings.Count(f => f.Severity == "Warning");
        if (critical > 0)
        {
            return $"{critical} critical security finding(s), {warning} warning(s).";
        }

        if (warning > 0)
        {
            return $"{warning} security warning(s).";
        }

        return "No critical Wi-Fi security issues detected from available local data.";
    }

    private static bool LooksPublic(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        return PublicProfileHints.Any(hint => name.Contains(hint, StringComparison.OrdinalIgnoreCase));
    }

    private static string ValueOrUnknown(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "(unknown)" : value.Trim();
}
