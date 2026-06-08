using System.IO;
using NetScopeDiagnosticCenter.Collectors.Wifi;
using NetScopeDiagnosticCenter.Infrastructure;

namespace NetScopeDiagnosticCenter.Tests.Collectors.Wifi;

public sealed class WifiDeviceTypeOverrideStoreTests : IDisposable
{
    private readonly string _isolatedAppData;

    public WifiDeviceTypeOverrideStoreTests()
    {
        _isolatedAppData = Path.Combine(
            Path.GetTempPath(),
            "ISG-Desk-WifiDeviceTypes-" + Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_isolatedAppData, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void SaveType_PersistsSupportedTypeAcrossStoreInstances()
    {
        var storage = new AppStorageService(_isolatedAppData);
        var key = WifiDeviceTypeOverrideStore.BuildKey("8c-55-70-b6-f4-27", "192.168.0.102");
        var store = new WifiDeviceTypeOverrideStore(storage);

        store.SaveType(key, " printer ");

        store.Load()[key].Should().Be("Printer");
        new WifiDeviceTypeOverrideStore(storage).Load()[key].Should().Be("Printer");
    }

    [Fact]
    public void SaveType_WithUnsupportedOrBlankType_DoesNotPersist()
    {
        var storage = new AppStorageService(_isolatedAppData);
        var store = new WifiDeviceTypeOverrideStore(storage);
        var key = WifiDeviceTypeOverrideStore.BuildKey("AA:BB:CC:DD:EE:FF", "192.168.0.50");

        store.SaveType(key, "Coffee machine");
        store.Load().Should().NotContainKey(key);

        store.SaveType(key, "NAS");
        store.SaveType(key, "   ");

        store.Load().Should().NotContainKey(key);
        new WifiDeviceTypeOverrideStore(storage).Load().Should().NotContainKey(key);
    }
}
