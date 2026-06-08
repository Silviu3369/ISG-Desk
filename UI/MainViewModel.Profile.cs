using NetScopeDiagnosticCenter.Core.Models;

namespace NetScopeDiagnosticCenter.UI;

/// <summary>
/// Internal runtime-only network defaults used by diagnostics, targeted tests and reports.
/// This is not a user-facing Settings module and is never loaded from disk.
/// </summary>
public sealed partial class MainViewModel
{
    private NetworkProfile _networkProfile = new();

    /// <summary>
    /// Recent target history is cross-cutting (Targeted Tests / Printers consume it) and
    /// stays here while the shell migration is incomplete.
    /// </summary>
    private RecentTargets _recentTargets = new();

    public NetworkProfile NetworkProfile => _networkProfile;

    private void LoadNetworkProfile()
    {
        _networkProfile = _appStorage.LoadProfile();
    }
}
