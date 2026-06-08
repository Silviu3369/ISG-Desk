using System.IO;
using NetScopeDiagnosticCenter.Core.Models;

namespace NetScopeDiagnosticCenter.Infrastructure;

public sealed class AppStorageService
{
    private const string LegacySettingsFolderName = "Settings";

    private static readonly string[] LegacyCredentialFiles =
    [
        "snmpv3-credential.bin",
        "snmp-credential.bin"
    ];

    /// <summary>
    /// Default constructor: stores under <c>%LOCALAPPDATA%\ISG Desk</c>.
    /// </summary>
    public AppStorageService()
    {
        ApplicationFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ISG Desk");
    }

    /// <summary>
    /// Test-only constructor: pins the application folder to the supplied path.
    /// Production code uses the parameterless constructor.
    /// </summary>
    public AppStorageService(string applicationFolder)
    {
        if (string.IsNullOrWhiteSpace(applicationFolder))
            throw new ArgumentException("Application folder must be a non-empty path.", nameof(applicationFolder));
        ApplicationFolder = applicationFolder;
    }

    public string ApplicationFolder { get; }

    public string ReportFolder => Path.Combine(ApplicationFolder, "Reports");
    public string LogFolder => Path.Combine(ApplicationFolder, "Logs");
    public string DataFolder => Path.Combine(ApplicationFolder, "Data");
    public string LogFilePath => Path.Combine(LogFolder, "diagnostic-center.log");
    public string ProfileFilePath => GetDataFilePath("network-profile.json");
    public string RecentTargetsFilePath => GetDataFilePath("recent-targets.json");

    private string LegacySettingsFolder => Path.Combine(ApplicationFolder, LegacySettingsFolderName);

    private volatile bool _foldersEnsured;

    public void EnsureFolders()
    {
        if (_foldersEnsured) return;
        Directory.CreateDirectory(ApplicationFolder);
        Directory.CreateDirectory(ReportFolder);
        Directory.CreateDirectory(LogFolder);
        Directory.CreateDirectory(DataFolder);
        RemovePersistedRuntimeState();
        MigrateLegacyCredentialFiles();
        _foldersEnsured = true;
    }

    public string GetDataFilePath(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            throw new ArgumentException("File name must be a non-empty value.", nameof(fileName));
        if (Path.GetFileName(fileName) != fileName)
            throw new ArgumentException("Only file names are allowed for app data files.", nameof(fileName));

        return Path.Combine(DataFolder, fileName);
    }

    public NetworkProfile LoadProfile()
    {
        EnsureFolders();
        TryDelete(ProfileFilePath);
        TryDelete(Path.Combine(LegacySettingsFolder, "network-profile.json"));
        return new NetworkProfile();
    }

    public void SaveProfile(NetworkProfile profile)
    {
        EnsureFolders();
        TryDelete(ProfileFilePath);
        TryDelete(Path.Combine(LegacySettingsFolder, "network-profile.json"));
    }

    /// <summary>
    /// Recent typed targets (server / service / printer names + last print server) are
    /// SESSION-ONLY by design - the app must start clean on every launch and never carry
    /// one technician's / one PC's typed history into the next run. So this always returns
    /// a fresh empty set and also deletes stale <c>recent-targets.json</c> files left by
    /// older builds, so a previously remembered customer name cannot linger.
    /// </summary>
    public RecentTargets LoadRecentTargets()
    {
        TryDelete(RecentTargetsFilePath);
        TryDelete(Path.Combine(LegacySettingsFolder, "recent-targets.json"));

        return new RecentTargets();
    }

    /// <summary>
    /// No-op: recent targets are intentionally NOT persisted (see <see cref="LoadRecentTargets"/>).
    /// The in-memory list still powers the "recent" combos for the CURRENT session only.
    /// </summary>
    public void SaveRecentTargets(RecentTargets recentTargets)
    {
        // Intentionally does not touch disk - the app starts clean every launch.
    }

    private void RemovePersistedRuntimeState()
    {
        TryDelete(ProfileFilePath);
        TryDelete(RecentTargetsFilePath);
        TryDelete(Path.Combine(LegacySettingsFolder, "network-profile.json"));
        TryDelete(Path.Combine(LegacySettingsFolder, "recent-targets.json"));
    }

    private void MigrateLegacyCredentialFiles()
    {
        foreach (var fileName in LegacyCredentialFiles)
        {
            var legacyPath = Path.Combine(LegacySettingsFolder, fileName);
            var currentPath = GetDataFilePath(fileName);
            if (!File.Exists(legacyPath) || File.Exists(currentPath))
            {
                continue;
            }

            try
            {
                File.Copy(legacyPath, currentPath);
            }
            catch
            {
                // Best-effort migration. A locked/corrupt legacy file must not block startup.
            }
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best-effort cleanup only.
        }
    }
}
