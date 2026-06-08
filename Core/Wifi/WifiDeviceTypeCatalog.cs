namespace NetScopeDiagnosticCenter.Core.Wifi;

public static class WifiDeviceTypeCatalog
{
    public static readonly IReadOnlyList<string> ManualTypes =
    [
        "Router/AP",
        "PC / Laptop",
        "Phone / Tablet",
        "Printer",
        "TV / Media",
        "Console",
        "NAS",
        "Camera / NVR",
        "Apple device",
        "IoT / Smart Home",
        "Network client",
        "Unknown"
    ];

    public static string? NormalizeManualType(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return ManualTypes.FirstOrDefault(
            candidate => string.Equals(candidate, trimmed, StringComparison.OrdinalIgnoreCase));
    }
}
