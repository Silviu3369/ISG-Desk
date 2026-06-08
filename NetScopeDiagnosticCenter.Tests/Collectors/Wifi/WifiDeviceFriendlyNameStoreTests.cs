using System.IO;
using NetScopeDiagnosticCenter.Collectors.Wifi;
using NetScopeDiagnosticCenter.Infrastructure;

namespace NetScopeDiagnosticCenter.Tests.Collectors.Wifi;

public sealed class WifiDeviceFriendlyNameStoreTests : IDisposable
{
    private readonly string _isolatedAppData;

    public WifiDeviceFriendlyNameStoreTests()
    {
        _isolatedAppData = Path.Combine(
            Path.GetTempPath(),
            "ISG-Desk-WifiFriendlyNames-" + Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_isolatedAppData, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void SaveName_LoadsTrimmedNameAndPersistsAcrossStoreInstances()
    {
        var storage = new AppStorageService(_isolatedAppData);
        var key = WifiDeviceFriendlyNameStore.BuildKey("8c-55-70-b6-f4-27", "192.168.0.102");
        var store = new WifiDeviceFriendlyNameStore(storage);

        store.SaveName(key, "  Living\u0001 room TV  ");

        store.Load()[key].Should().Be("Living room TV");
        new WifiDeviceFriendlyNameStore(storage).Load()[key].Should().Be("Living room TV");
    }

    [Fact]
    public void SaveName_WithBlankName_RemovesExistingName()
    {
        var storage = new AppStorageService(_isolatedAppData);
        var store = new WifiDeviceFriendlyNameStore(storage);
        var key = WifiDeviceFriendlyNameStore.BuildKey("AA:BB:CC:DD:EE:FF", "192.168.0.50");

        store.SaveName(key, "Office printer");
        store.SaveName(key, "   ");

        store.Load().Should().NotContainKey(key);
        new WifiDeviceFriendlyNameStore(storage).Load().Should().NotContainKey(key);
    }
}
