using System.Text;
using NetScopeDiagnosticCenter.Core.Models.Wifi;

namespace NetScopeDiagnosticCenter.Core.Wifi;

public static class WifiDeviceInventoryBuilder
{
    public static IReadOnlyList<WifiDeviceInventoryItem> Build(
        IEnumerable<WifiLanDevice> localDevices,
        IEnumerable<WifiRouterClient> routerClients,
        IReadOnlyDictionary<string, string> friendlyNames,
        DateTimeOffset? observedAt = null)
        => Build(localDevices, routerClients, friendlyNames, new Dictionary<string, string>(), observedAt);

    public static IReadOnlyList<WifiDeviceInventoryItem> Build(
        IEnumerable<WifiLanDevice> localDevices,
        IEnumerable<WifiRouterClient> routerClients,
        IReadOnlyDictionary<string, string> friendlyNames,
        IReadOnlyDictionary<string, string> manualDeviceTypes,
        DateTimeOffset? observedAt = null)
        => Build(localDevices, routerClients, friendlyNames, manualDeviceTypes, new Dictionary<string, WifiDevicePresence>(), observedAt);

    public static IReadOnlyList<WifiDeviceInventoryItem> Build(
        IEnumerable<WifiLanDevice> localDevices,
        IEnumerable<WifiRouterClient> routerClients,
        IReadOnlyDictionary<string, string> friendlyNames,
        IReadOnlyDictionary<string, string> manualDeviceTypes,
        IReadOnlyDictionary<string, WifiDevicePresence> devicePresence,
        DateTimeOffset? observedAt = null)
    {
        var snapshotAt = observedAt ?? DateTimeOffset.Now;
        var entries = new Dictionary<string, InventoryEntry>(StringComparer.OrdinalIgnoreCase);

        foreach (var local in localDevices ?? Array.Empty<WifiLanDevice>())
        {
            var key = BuildStableKey(local.MacAddress, local.IpAddress);
            var entry = GetOrCreate(entries, key, local.IpAddress, local.MacAddress);
            entry.Local = local;
            entry.IpAddress = PreferIp(entry.IpAddress, local.IpAddress);
            entry.MacAddress = PreferMac(entry.MacAddress, local.MacAddress);
            entry.Vendor ??= NullIfPlaceholder(local.Vendor);
        }

        foreach (var router in routerClients ?? Array.Empty<WifiRouterClient>())
        {
            var key = BuildStableKey(router.MacAddress, router.IpAddress);
            var entry = GetOrCreate(entries, key, router.IpAddress, router.MacAddress);
            entry.Router = router;
            entry.IpAddress = PreferIp(entry.IpAddress, router.IpAddress);
            entry.MacAddress = PreferMac(entry.MacAddress, router.MacAddress);
            entry.Vendor ??= NullIfPlaceholder(router.Vendor);
        }

        return entries.Values
            .Select(entry => ToInventoryItem(entry, friendlyNames, manualDeviceTypes, devicePresence, snapshotAt))
            .OrderBy(item => LastOctet(item.IpAddress))
            .ThenBy(item => item.IpAddress, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static string BuildStableKey(string? macAddress, string? ipAddress)
    {
        var mac = NormalizeMac(macAddress);
        if (!string.IsNullOrWhiteSpace(mac))
        {
            return "mac:" + mac;
        }

        var ip = string.IsNullOrWhiteSpace(ipAddress) ? "unknown" : ipAddress.Trim();
        return "ip:" + ip;
    }

    private static InventoryEntry GetOrCreate(
        Dictionary<string, InventoryEntry> entries,
        string key,
        string ip,
        string? mac)
    {
        if (entries.TryGetValue(key, out var entry))
        {
            return entry;
        }

        entry = new InventoryEntry
        {
            Key = key,
            IpAddress = ip,
            MacAddress = NormalizeMac(mac)
        };
        entries[key] = entry;
        return entry;
    }

    private static WifiDeviceInventoryItem ToInventoryItem(
        InventoryEntry entry,
        IReadOnlyDictionary<string, string> friendlyNames,
        IReadOnlyDictionary<string, string> manualDeviceTypes,
        IReadOnlyDictionary<string, WifiDevicePresence> devicePresence,
        DateTimeOffset observedAt)
    {
        var manualName = friendlyNames.TryGetValue(entry.Key, out var savedName)
            ? NullIfPlaceholder(savedName)
            : null;
        var manualDeviceType = manualDeviceTypes.TryGetValue(entry.Key, out var savedType)
            ? WifiDeviceTypeCatalog.NormalizeManualType(savedType)
            : null;
        var learnedName = BestLearnedName(entry);
        var displayName = manualName ?? learnedName ?? "Unnamed device";
        var role = entry.Local?.RoleLabel ?? string.Empty;
        var vendor = entry.Vendor ?? NullIfPlaceholder(entry.Local?.Vendor) ?? NullIfPlaceholder(entry.Router?.Vendor);
        var (autoDeviceType, autoConfidence, fingerprintEvidence) = Fingerprint(entry, displayName, vendor, role, manualName is not null);
        var deviceType = manualDeviceType ?? autoDeviceType;
        var confidence = manualDeviceType is null ? autoConfidence : "Manual";
        var typeSource = manualDeviceType is null ? "Automatic fingerprint" : "Manual override";
        var sources = BuildSources(entry, manualName is not null, manualDeviceType is not null);
        var evidence = BuildEvidence(entry, fingerprintEvidence, manualDeviceType, autoDeviceType);
        var (wirelessStatus, wirelessEvidence, isWirelessCandidate) = ClassifyWireless(entry, autoDeviceType, role);
        var presence = devicePresence.TryGetValue(entry.Key, out var sessionPresence)
            ? sessionPresence
            : null;

        return new WifiDeviceInventoryItem(
            Key: entry.Key,
            IpAddress: entry.IpAddress,
            MacAddress: entry.MacAddress,
            DisplayName: displayName,
            LearnedName: learnedName,
            ManualName: manualName,
            Vendor: vendor,
            DeviceType: deviceType,
            AutoDeviceType: autoDeviceType,
            ManualDeviceType: manualDeviceType,
            DeviceTypeSource: typeSource,
            Confidence: confidence,
            Sources: sources,
            Role: role,
            Evidence: evidence,
            IsLocalDetected: entry.Local is not null,
            IsRouterReported: entry.Router is not null,
            ObservedAt: observedAt,
            FirstSeenAt: presence?.FirstSeenAt ?? observedAt,
            LastSeenAt: presence?.LastSeenAt ?? observedAt,
            SeenCount: presence?.SeenCount ?? 1,
            WirelessStatus: wirelessStatus,
            WirelessEvidence: wirelessEvidence,
            IsWirelessCandidate: isWirelessCandidate);
    }

    private static string? BestLearnedName(InventoryEntry entry)
    {
        var local = NullIfPlaceholder(entry.Local?.HostName);
        if (!string.IsNullOrWhiteSpace(local))
        {
            return local;
        }

        return NullIfPlaceholder(entry.Router?.HostName);
    }

    private static (string Type, string Confidence, string Evidence) Fingerprint(
        InventoryEntry entry,
        string displayName,
        string? vendor,
        string role,
        bool hasManualName)
    {
        var haystack = $"{displayName} {vendor} {role} {entry.Local?.NameSource} {entry.Router?.Source}".ToUpperInvariant();

        if (role.Contains("GATEWAY", StringComparison.OrdinalIgnoreCase) ||
            role.Contains("WI-FI AP", StringComparison.OrdinalIgnoreCase))
        {
            return ("Router/AP", "High", "Role evidence from gateway/BSSID detection.");
        }

        if (ContainsAny(haystack, "PRINTER", "IPP", "LASERJET", "OFFICEJET", "PIXMA", "MFP"))
        {
            return ("Printer", "High", "Printer name/service evidence.");
        }

        if (ContainsAny(haystack, "TV", "CHROMECAST", "GOOGLECAST", "ROKU", "AIRPLAY"))
        {
            return ("TV / Media", "High", "Media service/name evidence.");
        }

        if (ContainsAny(haystack, "XBOX", "PLAYSTATION", "NINTENDO"))
        {
            return ("Console", "High", "Console name/vendor evidence.");
        }

        if (ContainsAny(haystack, "NAS", "SYNOLOGY", "QNAP", "TRUENAS"))
        {
            return ("NAS", "High", "Storage device name/vendor evidence.");
        }

        if (ContainsAny(haystack, "CAMERA", "ONVIF", "NVR", "HIKVISION", "DAHUA"))
        {
            return ("Camera / NVR", "High", "Camera/NVR name/vendor evidence.");
        }

        if (ContainsAny(haystack, "APPLE", "IPHONE", "IPAD"))
        {
            return ("Apple device", hasManualName ? "High" : "Medium", "Apple vendor/name evidence.");
        }

        if (ContainsAny(haystack, "ANDROID", "SAMSUNG", "HUAWEI", "XIAOMI", "ONEPLUS"))
        {
            return ("Phone / Tablet", hasManualName ? "High" : "Medium", "Mobile vendor/name evidence.");
        }

        if (ContainsAny(haystack, "WINDOWS", "DESKTOP", "LAPTOP", "DELL", "LENOVO", "HP", "MICROSOFT", "INTEL"))
        {
            return ("PC / Laptop", hasManualName ? "High" : "Medium", "Computer vendor/name evidence.");
        }

        if (entry.Router is not null && entry.Local is null)
        {
            return ("Network client", "Medium", "Reported by router/AP only.");
        }

        return ("Unknown", hasManualName ? "Medium" : "Low", "Insufficient fingerprint evidence.");
    }

    private static (string Status, string Evidence, bool IsCandidate) ClassifyWireless(
        InventoryEntry entry,
        string deviceType,
        string role)
    {
        if (entry.Local?.IsThisPc == true)
        {
            return ("Confirmed wireless", "This PC is connected through the active Wi-Fi adapter.", true);
        }

        if (entry.Local?.IsWifiAccessPoint == true)
        {
            return ("Wi-Fi infrastructure", "Device MAC matches the connected Wi-Fi BSSID/AP.", true);
        }

        if (entry.Local?.IsLikelyWifiAccessPoint == true)
        {
            return ("Likely Wi-Fi infrastructure", "Device MAC is related to the connected Wi-Fi BSSID/AP.", true);
        }

        if (entry.Router?.IsWirelessAssociation == true)
        {
            return ("Confirmed wireless client", $"AP/controller reported this MAC from {entry.Router.SourceDisplay}.", true);
        }

        if (IsLikelyWirelessDeviceType(deviceType))
        {
            return ("Likely wireless client", $"{deviceType} fingerprint is commonly wireless; AP association was not confirmed.", true);
        }

        if (!string.IsNullOrWhiteSpace(role)
            && role.Contains("WI-FI", StringComparison.OrdinalIgnoreCase))
        {
            return ("Likely wireless infrastructure", $"Role evidence: {role}.", true);
        }

        return ("Not wireless-proven", "ARP/DNS/mDNS/SSDP/SNMP ARP data can prove LAN presence, not Wi-Fi association.", false);
    }

    private static bool IsLikelyWirelessDeviceType(string deviceType) =>
        deviceType is "Phone / Tablet" or "TV / Media" or "Console";

    private static string BuildSources(InventoryEntry entry, bool hasManualName, bool hasManualType)
    {
        var sources = new List<string>();
        if (entry.Local is not null)
        {
            sources.Add("Local");
            var nameSource = NullIfPlaceholder(entry.Local.NameSource);
            if (!string.IsNullOrWhiteSpace(nameSource))
            {
                sources.Add(nameSource);
            }
        }

        if (entry.Router is not null)
        {
            sources.Add("Router/AP");
            sources.Add("SNMP");
        }

        if (hasManualName)
        {
            sources.Add("Manual");
        }

        if (hasManualType)
        {
            sources.Add("Manual type");
        }

        return string.Join(" + ", sources.Distinct(StringComparer.OrdinalIgnoreCase));
    }

    private static string BuildEvidence(
        InventoryEntry entry,
        string fingerprintEvidence,
        string? manualDeviceType,
        string autoDeviceType)
    {
        var sb = new StringBuilder(fingerprintEvidence);
        if (!string.IsNullOrWhiteSpace(manualDeviceType))
        {
            sb.Append(" Manual type override: ").Append(manualDeviceType)
              .Append("; automatic fingerprint was ").Append(autoDeviceType).Append('.');
        }
        if (!string.IsNullOrWhiteSpace(entry.Local?.NameSourceDisplay))
        {
            sb.Append(" Name source: ").Append(entry.Local.NameSourceDisplay).Append('.');
        }
        if (!string.IsNullOrWhiteSpace(entry.Local?.RoleSourceDisplay))
        {
            sb.Append(" Role: ").Append(entry.Local.RoleSourceDisplay).Append('.');
        }
        if (entry.Router is not null)
        {
            sb.Append(" Router source: ").Append(entry.Router.SourceDisplay)
              .Append("; ifIndex ").Append(entry.Router.InterfaceIndex).Append('.');
        }

        return sb.ToString();
    }

    private static string? NullIfPlaceholder(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed is "-" or "Name not reported" or "No advertised name"
            ? null
            : trimmed;
    }

    private static string? PreferMac(string? current, string? next) => NormalizeMac(current) ?? NormalizeMac(next);

    private static string PreferIp(string current, string next) =>
        string.IsNullOrWhiteSpace(current) ? next : current;

    private static string? NormalizeMac(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var pairs = raw
            .Replace('-', ':')
            .Split(':', StringSplitOptions.RemoveEmptyEntries)
            .Where(part => part.Length == 2)
            .Select(part => part.ToUpperInvariant())
            .ToArray();

        return pairs.Length == 6 ? string.Join(':', pairs) : null;
    }

    private static bool ContainsAny(string haystack, params string[] needles) =>
        needles.Any(needle => haystack.Contains(needle, StringComparison.OrdinalIgnoreCase));

    private static int LastOctet(string ip)
    {
        var dot = ip.LastIndexOf('.');
        return dot >= 0 && int.TryParse(ip[(dot + 1)..], out var value) ? value : 0;
    }

    private sealed class InventoryEntry
    {
        public string Key { get; set; } = string.Empty;
        public string IpAddress { get; set; } = string.Empty;
        public string? MacAddress { get; set; }
        public string? Vendor { get; set; }
        public WifiLanDevice? Local { get; set; }
        public WifiRouterClient? Router { get; set; }
    }
}
