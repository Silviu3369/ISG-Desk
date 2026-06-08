using System.IO;
using System.Text.Json;
using NetScopeDiagnosticCenter.Core.Wifi;
using NetScopeDiagnosticCenter.Infrastructure;

namespace NetScopeDiagnosticCenter.Collectors.Wifi;

public sealed class WifiDeviceFriendlyNameStore
{
    private readonly AppStorageService _storage;
    private readonly object _sync = new();
    private Dictionary<string, string>? _cache;

    public WifiDeviceFriendlyNameStore(AppStorageService storage)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
    }

    public IReadOnlyDictionary<string, string> Load()
    {
        lock (_sync)
        {
            return new Dictionary<string, string>(EnsureLoaded(), StringComparer.OrdinalIgnoreCase);
        }
    }

    public void SaveName(string key, string friendlyName)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        var name = NormalizeFriendlyName(friendlyName);
        if (string.IsNullOrWhiteSpace(name))
        {
            RemoveName(key);
            return;
        }

        lock (_sync)
        {
            var data = EnsureLoaded();
            data[key] = name;
            Persist(data);
        }
    }

    public void RemoveName(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        lock (_sync)
        {
            var data = EnsureLoaded();
            if (data.Remove(key))
            {
                Persist(data);
            }
        }
    }

    public static string BuildKey(string? macAddress, string? ipAddress) =>
        WifiDeviceInventoryBuilder.BuildStableKey(macAddress, ipAddress);

    private Dictionary<string, string> EnsureLoaded()
    {
        if (_cache is not null)
        {
            return _cache;
        }

        _storage.EnsureFolders();
        var path = FriendlyNamesPath;
        try
        {
            if (File.Exists(path))
            {
                var loaded = JsonSerializer.Deserialize<Dictionary<string, string>>(
                    File.ReadAllText(path));
                _cache = loaded is null
                    ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, string>(
                        loaded.Where(pair => !string.IsNullOrWhiteSpace(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value)),
                        StringComparer.OrdinalIgnoreCase);
                return _cache;
            }
        }
        catch
        {
            // Corrupt or locked file: start with an empty in-memory set. Next save rewrites it.
        }

        _cache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        return _cache;
    }

    private string FriendlyNamesPath => _storage.GetDataFilePath("wifi-device-friendly-names.json");

    private void Persist(Dictionary<string, string> data)
    {
        _storage.EnsureFolders();
        var path = FriendlyNamesPath;
        var json = JsonSerializer.Serialize(
            data.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase),
            new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, json);
    }

    private static string? NormalizeFriendlyName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var cleaned = new string(value.Trim().Where(c => !char.IsControl(c)).ToArray());
        return cleaned.Length > 80 ? cleaned[..80] : cleaned;
    }
}
