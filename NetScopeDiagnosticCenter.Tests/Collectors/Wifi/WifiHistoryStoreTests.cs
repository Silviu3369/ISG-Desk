using System.IO;
using FluentAssertions;
using NetScopeDiagnosticCenter.Collectors.Wifi;

namespace NetScopeDiagnosticCenter.Tests.Collectors.Wifi;

public sealed class WifiHistoryStoreTests : IDisposable
{
    private readonly string _tempDirectory = Path.Combine(
        Path.GetTempPath(),
        "ISG-Desk-WifiHistoryTests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDirectory))
            {
                Directory.Delete(_tempDirectory, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup for locked temp files.
        }
    }

    [Fact]
    public void DefaultHistoryDirectory_UsesCurrentProductFolder()
    {
        WifiHistoryStore.DefaultHistoryDirectory
            .Should()
            .EndWith(Path.Combine("ISG Desk", "WifiHistory"));
    }

    [Fact]
    public void Flush_WritesCsvIntoInjectedHistoryDirectory()
    {
        using var store = new WifiHistoryStore(_tempDirectory);

        store.Record(new WifiHistoryStore.Row(
            DateTimeOffset.Parse("2026-05-24T10:00:00+02:00"),
            "Office, WiFi",
            "AA:BB:CC:DD:EE:FF",
            -55,
            12.5,
            3.25,
            866,
            433));

        store.Flush();

        var file = Directory.GetFiles(_tempDirectory, "wifi-*.csv").Should().ContainSingle().Subject;
        var text = File.ReadAllText(file);

        text.Should().Contain("Timestamp,Ssid,Bssid,RssiDbm,RxThroughputMbps,TxThroughputMbps,RxPhyMbps,TxPhyMbps");
        text.Should().Contain("\"Office, WiFi\"");
        text.Should().Contain("AA:BB:CC:DD:EE:FF");
    }

    [Theory]
    [InlineData("=SUM(1,1)")]
    [InlineData("+cmd")]
    [InlineData("-cmd")]
    [InlineData("@cmd")]
    [InlineData("   =cmd")]
    public void Flush_NeutralizesSpreadsheetFormulaSsid(string ssid)
    {
        using var store = new WifiHistoryStore(_tempDirectory);

        store.Record(new WifiHistoryStore.Row(
            DateTimeOffset.Parse("2026-05-24T10:00:00+02:00"),
            ssid,
            "AA:BB:CC:DD:EE:FF",
            -55,
            12.5,
            3.25,
            866,
            433));

        store.Flush();

        var file = Directory.GetFiles(_tempDirectory, "wifi-*.csv").Should().ContainSingle().Subject;
        var text = File.ReadAllText(file);

        text.Should().Contain("'" + ssid);
    }
}
