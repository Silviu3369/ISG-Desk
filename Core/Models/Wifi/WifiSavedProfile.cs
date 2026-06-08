namespace NetScopeDiagnosticCenter.Core.Models.Wifi;

/// <summary>
/// One saved Wi-Fi profile name read from the Windows WLAN service.
/// </summary>
public sealed record WifiSavedProfile(string DisplayName)
{
    public static IReadOnlyList<WifiSavedProfile> FromNames(IEnumerable<string>? names)
    {
        if (names is null)
        {
            return Array.Empty<WifiSavedProfile>();
        }

        return names
            .Select(name => name?.Trim())
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .Select(name => new WifiSavedProfile(name!))
            .ToList();
    }
}
