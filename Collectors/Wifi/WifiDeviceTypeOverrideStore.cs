using System.IO;
using System.Text.Json;
using NetScopeDiagnosticCenter.Core.Wifi;
using NetScopeDiagnosticCenter.Infrastructure;

namespace NetScopeDiagnosticCenter.Collectors.Wifi;

public sealed class WifiDeviceTypeOverrideStore
{
    private readonly AppStorageService _storage;
    private readonly object _sync = new();
    private Dictionary<string, string>? _cache;

    public WifiDeviceTypeOverrideStore(AppStorageService storage)
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

    public void SaveType(string key, string? deviceType)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        var normalized = WifiDeviceTypeCatalog.NormalizeManualType(deviceType);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            RemoveType(key);
            return;
        }

        lock (_sync)
        {
            var data = EnsureLoaded();
            data[key] = normalized;
            Persist(data);
        }
    }

    public void RemoveType(string key)
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
        var path = TypeOverridesPath;
        try
        {
            if (File.Exists(path))
            {
                var loaded = JsonSerializer.Deserialize<Dictionary<string, string>>(
                    File.ReadAllText(path));
                _cache = loaded is null
                    ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, string>(
                        loaded
                            .Select(pair => new KeyValuePair<string, string>(
                                pair.Key,
                                WifiDeviceTypeCatalog.NormalizeManualType(pair.Value) ?? string.Empty))
                            .Where(pair => !string.IsNullOrWhiteSpace(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value)),
                        StringComparer.OrdinalIgnoreCase);
                return _cache;
            }
        }
        catch
        {
            // Corrupt or locked file: start empty. Next save rewrites it with valid values.
        }

        _cache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        return _cache;
    }

    private string TypeOverridesPath => _storage.GetDataFilePath("wifi-device-type-overrides.json");

    private void Persist(Dictionary<string, string> data)
    {
        _storage.EnsureFolders();
        var path = TypeOverridesPath;
        var json = JsonSerializer.Serialize(
            data.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase),
            new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, json);
    }
}
