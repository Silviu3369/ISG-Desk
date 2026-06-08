using NetScopeDiagnosticCenter.Core.Models;
using NetScopeDiagnosticCenter.UI.ViewModels;

namespace NetScopeDiagnosticCenter.UI;

/// <summary>
/// Activity feed glue: the sidebar binds to <c>Activity.LastEntry</c>, while module host
/// callbacks publish status messages here so the live monitor reflects real work.
/// </summary>
public sealed partial class MainViewModel : ActivityViewModel.IHost
{
    /// <summary>The dedicated activity feed VM. Constructed in MainViewModel.cs ctor.</summary>
    public ActivityViewModel Activity { get; private set; } = null!;

    private void PublishStatus(ActivitySourceModule sourceModule, string message)
    {
        StatusMessage = message;
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        _activityFeedService.Add(
            sourceModule,
            ActivityStatus.Info,
            "Status",
            message);
    }

    // === ActivityViewModel.IHost (explicit to keep the rest of the host interface tidy) ===
    void ActivityViewModel.IHost.NotifyStatus(string message) => StatusMessage = message;
}
