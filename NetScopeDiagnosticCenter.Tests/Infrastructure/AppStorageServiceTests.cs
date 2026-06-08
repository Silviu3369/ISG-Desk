using System.IO;
using NetScopeDiagnosticCenter.Infrastructure;

namespace NetScopeDiagnosticCenter.Tests.Infrastructure;

public sealed class AppStorageServiceTests : IDisposable
{
    private readonly string _isolatedAppData;

    public AppStorageServiceTests()
    {
        _isolatedAppData = Path.Combine(Path.GetTempPath(), "ISG-Desk-StorageTests-" + Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_isolatedAppData, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void EnsureFolders_CreatesDataFolderWithoutCreatingSettingsFolder()
    {
        var storage = new AppStorageService(_isolatedAppData);

        storage.EnsureFolders();

        Directory.Exists(storage.DataFolder).Should().BeTrue();
        Directory.Exists(storage.LogFolder).Should().BeTrue();
        Directory.Exists(storage.ReportFolder).Should().BeTrue();
        Directory.Exists(Path.Combine(_isolatedAppData, "Settings")).Should().BeFalse();
    }

    [Fact]
    public void EnsureFolders_RemovesLegacyRuntimeStateAndMigratesCredentialsOnly()
    {
        var legacyFolder = Path.Combine(_isolatedAppData, "Settings");
        Directory.CreateDirectory(legacyFolder);
        File.WriteAllText(Path.Combine(legacyFolder, "network-profile.json"), "{}");
        File.WriteAllText(Path.Combine(legacyFolder, "recent-targets.json"), "{}");
        File.WriteAllBytes(Path.Combine(legacyFolder, "snmp-credential.bin"), new byte[] { 1, 2, 3 });

        var storage = new AppStorageService(_isolatedAppData);

        storage.EnsureFolders();

        File.Exists(Path.Combine(storage.DataFolder, "network-profile.json")).Should().BeFalse();
        File.Exists(Path.Combine(storage.DataFolder, "recent-targets.json")).Should().BeFalse();
        File.Exists(Path.Combine(legacyFolder, "network-profile.json")).Should().BeFalse();
        File.Exists(Path.Combine(legacyFolder, "recent-targets.json")).Should().BeFalse();
        File.ReadAllBytes(Path.Combine(storage.DataFolder, "snmp-credential.bin")).Should().Equal(new byte[] { 1, 2, 3 });
    }

    [Fact]
    public void LoadAndSaveProfile_AreSessionOnlyAndDeletePersistedProfiles()
    {
        var storage = new AppStorageService(_isolatedAppData);
        Directory.CreateDirectory(storage.DataFolder);
        var legacyFolder = Path.Combine(_isolatedAppData, "Settings");
        Directory.CreateDirectory(legacyFolder);
        File.WriteAllText(storage.ProfileFilePath, """{"ProfileName":"Work"}""");
        File.WriteAllText(Path.Combine(legacyFolder, "network-profile.json"), """{"ProfileName":"LegacyWork"}""");

        var profile = storage.LoadProfile();
        storage.SaveProfile(profile);

        profile.ProfileName.Should().Be("Default Network");
        File.Exists(storage.ProfileFilePath).Should().BeFalse();
        File.Exists(Path.Combine(legacyFolder, "network-profile.json")).Should().BeFalse();
    }

    [Fact]
    public void LoadRecentTargets_RemovesCurrentAndLegacyRecentTargets()
    {
        var storage = new AppStorageService(_isolatedAppData);
        Directory.CreateDirectory(storage.DataFolder);
        var legacyFolder = Path.Combine(_isolatedAppData, "Settings");
        Directory.CreateDirectory(legacyFolder);
        File.WriteAllText(storage.RecentTargetsFilePath, "{}");
        File.WriteAllText(Path.Combine(legacyFolder, "recent-targets.json"), "{}");

        var recentTargets = storage.LoadRecentTargets();

        recentTargets.ServerTargets.Should().BeEmpty();
        File.Exists(storage.RecentTargetsFilePath).Should().BeFalse();
        File.Exists(Path.Combine(legacyFolder, "recent-targets.json")).Should().BeFalse();
    }
}
