using NetScopeDiagnosticCenter.Core;
using NetScopeDiagnosticCenter.Core.Models;
using NetScopeDiagnosticCenter.UI.ViewModels;

namespace NetScopeDiagnosticCenter.UI;

/// <summary>
/// Repair Actions host glue. The Diagnosis page binds <c>RepairActions.*</c> directly;
/// MainViewModel supplies status/busy plumbing plus the active adapter name, and owns
/// the previous-vs-current diagnosis comparison shown on the verdict card.
/// </summary>
public sealed partial class MainViewModel : RepairActionsViewModel.IHost
{
    /// <summary>Repair Actions VM. Constructed in MainViewModel.cs ctor.</summary>
    public RepairActionsViewModel RepairActions { get; private set; } = null!;

    /// <summary>The Quick Diagnosis run that the current one replaced (session-scoped).</summary>
    private NetworkDiagnosisResult? _previousDiagnosis;

    /// <summary>"Previous run 14:02: 92/100 (Warning — …) · score +8 — improved" or empty.</summary>
    public string PreviousDiagnosisComparisonText =>
        DiagnosticHelpers.BuildDiagnosisComparison(_previousDiagnosis, LastDiagnosis);

    public bool HasPreviousDiagnosisComparison => !string.IsNullOrEmpty(PreviousDiagnosisComparisonText);

    // === RepairActionsViewModel.IHost ===
    void RepairActionsViewModel.IHost.NotifyStatus(string message) =>
        PublishStatus(ActivitySourceModule.QuickDiagnosis, message);

    void RepairActionsViewModel.IHost.NotifyBusy(bool busy) => SetBusyState(busy);

    string? RepairActionsViewModel.IHost.ActiveAdapterName
    {
        get
        {
            var name = LastDiagnosis?.Adapter.Name;
            if (string.IsNullOrWhiteSpace(name) ||
                name.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return name.Trim();
        }
    }
}
