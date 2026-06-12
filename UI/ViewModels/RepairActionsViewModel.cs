using System.Security.Principal;
using System.Windows;
using System.Windows.Input;
using NetScopeDiagnosticCenter.Collectors;
using NetScopeDiagnosticCenter.Core.Models;
using NetScopeDiagnosticCenter.Infrastructure;

namespace NetScopeDiagnosticCenter.UI.ViewModels;

/// <summary>
/// Repair Actions section on the Diagnosis page: one-click network first aid
/// (flush DNS, renew DHCP lease, reset Winsock, restart the active adapter).
/// Destructive-ish actions confirm first; the confirmation is injectable so
/// tests never pop a MessageBox.
/// </summary>
public sealed class RepairActionsViewModel : ObservableObject
{
    public interface IHost
    {
        void NotifyStatus(string message);
        void NotifyBusy(bool busy);

        /// <summary>Active adapter name from the latest Quick Diagnosis (null/empty when unknown).</summary>
        string? ActiveAdapterName { get; }
    }

    private readonly NetworkRepairService _repairService;
    private readonly ILoggingService _logger;
    private readonly IHost _host;
    private readonly Func<string, string, bool> _confirm;

    private RepairActionResult? _lastResult;
    private CancellationTokenSource? _repairCancellation;
    private bool _isRepairRunning;

    public RepairActionsViewModel(
        NetworkRepairService repairService,
        ILoggingService logger,
        IHost host,
        Func<string, string, bool>? confirm = null)
    {
        _repairService = repairService ?? throw new ArgumentNullException(nameof(repairService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _confirm = confirm ?? ShowConfirmationDialog;

        FlushDnsCommand = new AsyncRelayCommand(
            _ => RunRepairAsync("Flush DNS cache", null, token => _repairService.FlushDnsAsync(token)),
            _ => !IsRepairRunning);
        RenewDhcpCommand = new AsyncRelayCommand(
            _ => RunRepairAsync(
                "Renew DHCP lease",
                "Release and renew the DHCP lease now?\n\nConnectivity drops for a few seconds while the new lease is requested.",
                token => _repairService.RenewDhcpLeaseAsync(token)),
            _ => !IsRepairRunning);
        ResetWinsockCommand = new AsyncRelayCommand(
            _ => RunRepairAsync(
                "Reset Winsock",
                "Reset the Winsock catalog?\n\nThis repairs corrupted socket registrations (often after malware or VPN removal). Windows must be RESTARTED afterwards, and the action needs administrator rights.",
                token => _repairService.ResetWinsockAsync(token)),
            _ => !IsRepairRunning);
        RestartAdapterCommand = new AsyncRelayCommand(
            _ => RunRepairAsync(
                $"Restart adapter '{ActiveAdapterName}'",
                $"Restart the network adapter '{ActiveAdapterName}'?\n\nThe connection drops for several seconds. This action needs administrator rights.",
                token => _repairService.RestartAdapterAsync(ActiveAdapterName ?? string.Empty, token)),
            _ => !IsRepairRunning && HasActiveAdapter);
        CancelRepairCommand = new RelayCommand(_ => CancelRepair());
    }

    public ICommand FlushDnsCommand { get; }
    public ICommand RenewDhcpCommand { get; }
    public ICommand ResetWinsockCommand { get; }
    public ICommand RestartAdapterCommand { get; }
    public ICommand CancelRepairCommand { get; }

    public bool IsRepairRunning
    {
        get => _isRepairRunning;
        private set
        {
            if (SetProperty(ref _isRepairRunning, value))
            {
                (FlushDnsCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                (RenewDhcpCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                (ResetWinsockCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                (RestartAdapterCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            }
        }
    }

    public RepairActionResult? LastResult
    {
        get => _lastResult;
        private set
        {
            if (SetProperty(ref _lastResult, value))
            {
                OnPropertyChanged(nameof(HasLastResult));
                OnPropertyChanged(nameof(NoLastResult));
            }
        }
    }

    public bool HasLastResult => _lastResult is not null;
    public bool NoLastResult => _lastResult is null;

    public string? ActiveAdapterName => _host.ActiveAdapterName;

    public bool HasActiveAdapter => !string.IsNullOrWhiteSpace(ActiveAdapterName);

    public string RestartAdapterButtonText => HasActiveAdapter
        ? $"Restart Adapter ({ActiveAdapterName})"
        : "Restart Adapter";

    /// <summary>True when the process is NOT elevated — drives the "needs admin" hint row.</summary>
    public bool ShowAdminHint
    {
        get
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                return !new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>Called by the host after a new Quick Diagnosis changes the active adapter.</summary>
    public void NotifyDiagnosisContextChanged()
    {
        OnPropertyChanged(nameof(ActiveAdapterName));
        OnPropertyChanged(nameof(HasActiveAdapter));
        OnPropertyChanged(nameof(RestartAdapterButtonText));
        (RestartAdapterCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
    }

    private async Task RunRepairAsync(
        string actionName,
        string? confirmationMessage,
        Func<CancellationToken, Task<RepairActionResult>> action)
    {
        if (confirmationMessage is not null && !_confirm(actionName, confirmationMessage))
        {
            _host.NotifyStatus($"{actionName} cancelled.");
            return;
        }

        CancelQuietly(_repairCancellation);
        var repairCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        _repairCancellation = repairCancellation;
        var token = repairCancellation.Token;

        _host.NotifyBusy(true);
        IsRepairRunning = true;
        _host.NotifyStatus($"{actionName}...");
        try
        {
            var result = await action(token);
            LastResult = result;
            var statusLine = result.RestartRequired
                ? $"{result.Summary} (restart required)"
                : result.Summary;
            _host.NotifyStatus(statusLine);
            if (result.Success && !result.RestartRequired)
            {
                _host.NotifyStatus("Re-run Quick Diagnosis to confirm the repair helped.");
            }
        }
        catch (OperationCanceledException)
        {
            LastResult = new RepairActionResult
            {
                ActionName = actionName,
                Success = false,
                Summary = "The repair was cancelled or timed out."
            };
            _host.NotifyStatus($"{actionName} cancelled / timed out.");
        }
        catch (Exception ex)
        {
            _logger.Error($"Repair action failed: {actionName}", ex);
            LastResult = new RepairActionResult
            {
                ActionName = actionName,
                Success = false,
                Summary = $"Unexpected error: {ex.Message}"
            };
            _host.NotifyStatus($"{actionName} failed: {ex.Message}");
        }
        finally
        {
            repairCancellation.Dispose();
            if (ReferenceEquals(_repairCancellation, repairCancellation))
            {
                _repairCancellation = null;
                IsRepairRunning = false;
            }

            _host.NotifyBusy(false);
        }
    }

    private void CancelRepair()
    {
        CancelQuietly(_repairCancellation);
        _host.NotifyStatus("Stopping repair action...");
    }

    private static void CancelQuietly(CancellationTokenSource? cancellation)
    {
        try { cancellation?.Cancel(); } catch (ObjectDisposedException) { }
    }

    private static bool ShowConfirmationDialog(string title, string message) =>
        MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
}
