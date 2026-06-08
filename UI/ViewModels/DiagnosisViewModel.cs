using System.Windows.Input;
using NetScopeDiagnosticCenter.Collectors;
using NetScopeDiagnosticCenter.Core;
using NetScopeDiagnosticCenter.Core.Models;
using NetScopeDiagnosticCenter.Infrastructure;

namespace NetScopeDiagnosticCenter.UI.ViewModels;

/// <summary>
/// Diagnosis module ViewModel: Quick Diagnosis runner + manual TCP port test.
/// </summary>
/// <remarks>
/// Phase 2d migration. The user-facing Diagnosis page is now fused with Deep Ping & Path
/// through MainWindow bindings; this VM still owns only Quick Diagnosis and manual port test.
/// </remarks>
public sealed class DiagnosisViewModel : ObservableObject
{
    private static readonly IReadOnlyList<PortTestPortOption> DefaultPortTestPortOptions =
    [
        new(22, "SSH", "Remote shell / network appliance management over TCP."),
        new(23, "Telnet", "Legacy network appliance management. Use only on authorized internal targets."),
        new(53, "DNS", "DNS over TCP. Normal DNS is often UDP; TCP validates fallback/zone-transfer style reachability."),
        new(80, "HTTP", "Plain web service reachability."),
        new(88, "Kerberos", "Active Directory Kerberos authentication."),
        new(135, "RPC", "Windows RPC Endpoint Mapper."),
        new(139, "NetBIOS", "Legacy Windows file/printer sharing."),
        new(161, "SNMP", "SNMP is usually UDP; TCP/161 is uncommon but kept for compatibility with earlier presets."),
        new(389, "LDAP", "Active Directory LDAP."),
        new(443, "HTTPS", "Secure web/API service reachability."),
        new(445, "SMB", "Windows file share / print share access."),
        new(515, "LPR", "Legacy printer LPR/LPD service."),
        new(587, "SMTP submission", "Authenticated mail submission."),
        new(631, "IPP", "Internet Printing Protocol."),
        new(636, "LDAPS", "Secure Active Directory LDAP."),
        new(3389, "RDP", "Remote Desktop Protocol."),
        new(8080, "HTTP alternate", "Common alternate web/application port."),
        new(8443, "HTTPS alternate", "Common alternate secure web/application port."),
        new(9100, "Printer RAW", "Common direct TCP printing port.")
    ];

    public interface IHost
    {
        void NotifyStatus(string message);
        void NotifyBusy(bool busy);

        /// <summary>Cross-VM diagnosis state. Setting this triggers MainViewModel cascades (Reports/TechnicianHome/etc.).</summary>
        NetworkDiagnosisResult? LastDiagnosis { get; set; }

        /// <summary>Read-only views of complementary state owned by other VMs.</summary>
        PrinterDiscoveryResult? LastPrinterDiscovery { get; }
        NetworkDeviceResult? LastNetworkDevice { get; }
        NetworkDeviceScanResult? LastNetworkDeviceScan { get; }
        NetworkProfile NetworkProfile { get; }

        /// <summary>Tells the shell which page to show after Quick Diagnosis completes.</summary>
        string CurrentPage { get; set; }

        /// <summary>Re-raises LastDiagnosis-derived bindings after in-place mutation (port test path).</summary>
        void NotifyDiagnosisChanged();
    }

    private readonly DiagnosticEngine _diagnosticEngine;
    private readonly PortTestCollector _portTestCollector;
    private readonly HealthScoreCalculator _healthScoreCalculator;
    private readonly RuleEngine _ruleEngine;
    private readonly ILoggingService _logger;
    private readonly IHost _host;

    private const int MinimumTcpPort = 1;
    private const int MaximumTcpPort = 65535;

    private string _targetHost = "localhost";
    private int _targetPort = 445;
    private CancellationTokenSource? _diagnoseCancellation;
    private CancellationTokenSource? _portTestCancellation;
    private bool _isDiagnosisRunning;
    private bool _isPortTestRunning;

    public DiagnosisViewModel(
        DiagnosticEngine diagnosticEngine,
        PortTestCollector portTestCollector,
        HealthScoreCalculator healthScoreCalculator,
        RuleEngine ruleEngine,
        ILoggingService logger,
        IHost host)
    {
        _diagnosticEngine = diagnosticEngine ?? throw new ArgumentNullException(nameof(diagnosticEngine));
        _portTestCollector = portTestCollector ?? throw new ArgumentNullException(nameof(portTestCollector));
        _healthScoreCalculator = healthScoreCalculator ?? throw new ArgumentNullException(nameof(healthScoreCalculator));
        _ruleEngine = ruleEngine ?? throw new ArgumentNullException(nameof(ruleEngine));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _host = host ?? throw new ArgumentNullException(nameof(host));

        DiagnoseCommand = new AsyncRelayCommand(_ => DiagnoseAsync(), _ => !IsDiagnosisRunning);
        CancelDiagnoseCommand = new RelayCommand(_ => CancelDiagnose());
        RunPortTestCommand = new AsyncRelayCommand(_ => RunPortTestAsync(), _ => !IsPortTestRunning && CanRunPortTest);
        CancelPortTestCommand = new RelayCommand(_ => CancelPortTest());
        ApplyPortTestTargetPresetCommand = new RelayCommand(ApplyPortTestTargetPreset);
    }

    public string TargetHost
    {
        get => _targetHost;
        set
        {
            if (SetProperty(ref _targetHost, value))
            {
                RefreshPortTestValidationState();
            }
        }
    }

    public int TargetPort
    {
        get => _targetPort;
        set
        {
            if (SetProperty(ref _targetPort, value))
            {
                OnPropertyChanged(nameof(SelectedPortDescription));
                RefreshPortTestValidationState();
            }
        }
    }

    public bool IsDiagnosisRunning
    {
        get => _isDiagnosisRunning;
        private set
        {
            if (SetProperty(ref _isDiagnosisRunning, value))
            {
                OnPropertyChanged(nameof(DiagnoseButtonText));
                OnPropertyChanged(nameof(DiagnoseButtonIcon));
                (DiagnoseCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsPortTestRunning
    {
        get => _isPortTestRunning;
        private set
        {
            if (SetProperty(ref _isPortTestRunning, value))
            {
                OnPropertyChanged(nameof(PortTestButtonText));
                OnPropertyChanged(nameof(PortTestButtonIcon));
                (RunPortTestCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            }
        }
    }

    public string DiagnoseButtonText => IsDiagnosisRunning ? "Diagnosing..." : "Diagnose This PC";
    public string DiagnoseButtonIcon => IsDiagnosisRunning ? "\uE895" : "\uE9D9";
    public string PortTestButtonText => IsPortTestRunning ? "Testing..." : "Run Port Test";
    public string PortTestButtonIcon => IsPortTestRunning ? "\uE895" : "\uE8C8";
    public string SelectedPortDescription => PortTestPortOptions.FirstOrDefault(item => item.Port == TargetPort)?.Description
        ?? "Custom TCP port. Use only on hosts and services you are authorized to test.";
    public bool CanRunPortTest => ValidatePortTestInput(out _, out _);
    public string PortTestInputStatusText =>
        ValidatePortTestInput(out var normalizedTarget, out var reason)
            ? $"Ready to test {normalizedTarget}:{TargetPort}. Single-target TCP check only."
            : reason;
    public string PortTestInputSeverity => CanRunPortTest ? "OK" : "Warning";
    public string LastPortTestInterpretation => BuildPortTestInterpretation(_host.LastDiagnosis?.LastPortTest);

    public IReadOnlyList<PortTestPortOption> PortTestPortOptions => DefaultPortTestPortOptions;
    public IReadOnlyList<PortTestTargetOption> PortTestTargetOptions => BuildPortTestTargetOptions();

    public ICommand DiagnoseCommand { get; }
    public ICommand CancelDiagnoseCommand { get; }
    public ICommand RunPortTestCommand { get; }
    public ICommand CancelPortTestCommand { get; }
    public ICommand ApplyPortTestTargetPresetCommand { get; }

    public void NotifyDiagnosisContextChanged()
    {
        OnPropertyChanged(nameof(PortTestTargetOptions));
        OnPropertyChanged(nameof(LastPortTestInterpretation));
    }

    public async Task DiagnoseAsync()
    {
        CancelQuietly(_diagnoseCancellation);
        var diagnoseCancellation = new CancellationTokenSource();
        _diagnoseCancellation = diagnoseCancellation;
        var token = diagnoseCancellation.Token;

        _host.NotifyBusy(true);
        IsDiagnosisRunning = true;
        _host.NotifyStatus("Running quick diagnosis...");
        try
        {
            var result = await _diagnosticEngine.RunQuickDiagnosisAsync(token);
            // Carry over complementary state owned by other VMs so reports/Technician Home
            // show a coherent picture for the new diagnosis.
            if (_host.LastPrinterDiscovery is not null)
                result.LastPrinterDiscovery = _host.LastPrinterDiscovery;
            if (_host.LastNetworkDevice is not null)
                result.LastNetworkDevice = _host.LastNetworkDevice;
            if (_host.LastNetworkDeviceScan is not null)
                result.LastNetworkDeviceScan = _host.LastNetworkDeviceScan;
            _host.LastDiagnosis = result;
            _host.NotifyStatus("Diagnosis completed.");
            _host.CurrentPage = "Diagnosis";
        }
        catch (OperationCanceledException)
        {
            _host.NotifyStatus("Diagnosis cancelled.");
        }
        catch (Exception ex)
        {
            _logger.Error("Diagnosis command failed.", ex);
            _host.NotifyStatus($"Diagnosis failed: {ex.Message}");
        }
        finally
        {
            diagnoseCancellation.Dispose();
            if (ReferenceEquals(_diagnoseCancellation, diagnoseCancellation))
            {
                _diagnoseCancellation = null;
                IsDiagnosisRunning = false;
            }

            _host.NotifyBusy(false);
        }
    }

    public void CancelDiagnose()
    {
        // Cancel() throws ObjectDisposedException if the run already finished and the
        // finally disposed the CTS (fast Stop-after-complete) — swallow that benign race.
        CancelQuietly(_diagnoseCancellation);
        _host.NotifyStatus("Stopping diagnosis...");
    }

    public void CancelPortTest()
    {
        CancelQuietly(_portTestCancellation);
        _host.NotifyStatus("Stopping port test...");
    }

    public async Task RunPortTestAsync()
    {
        if (!ValidatePortTestInput(out var normalizedTarget, out var validationReason))
        {
            _host.NotifyStatus(validationReason);
            return;
        }

        // Bounded + cancellable: a filtered host makes Test-NetConnection hang; without a
        // hard cap + Stop the operation would block for the full collector budget.
        CancelQuietly(_portTestCancellation);
        var portTestCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        _portTestCancellation = portTestCancellation;
        var token = portTestCancellation.Token;

        _host.NotifyBusy(true);
        IsPortTestRunning = true;
        _host.NotifyStatus($"Testing {normalizedTarget}:{TargetPort}...");
        try
        {
            var portTest = await _portTestCollector.TestPortAsync(normalizedTarget, TargetPort, token);
            // Mutate the existing diagnosis (or create a fresh one) so port-only sessions
            // still produce a Verdict + HealthScore using the shared profile.
            var diagnosis = _host.LastDiagnosis ?? new NetworkDiagnosisResult();
            diagnosis.LastPortTest = portTest;
            diagnosis.Profile = _host.NetworkProfile;
            diagnosis.HealthScore = _healthScoreCalculator.Calculate(diagnosis);
            diagnosis.Verdict = _ruleEngine.Evaluate(diagnosis);

            if (!ReferenceEquals(_host.LastDiagnosis, diagnosis))
            {
                _host.LastDiagnosis = diagnosis;
            }
            else
            {
                _host.NotifyDiagnosisChanged();
            }

            _host.NotifyStatus(portTest.Verdict);
        }
        catch (OperationCanceledException)
        {
            _host.NotifyStatus("Port test cancelled / timed out.");
        }
        catch (Exception ex)
        {
            _logger.Error("Port test command failed.", ex);
            _host.NotifyStatus($"Port test failed: {ex.Message}");
        }
        finally
        {
            portTestCancellation.Dispose();
            if (ReferenceEquals(_portTestCancellation, portTestCancellation))
            {
                _portTestCancellation = null;
                IsPortTestRunning = false;
            }

            _host.NotifyBusy(false);
        }
    }

    private static void CancelQuietly(CancellationTokenSource? cancellation)
    {
        try { cancellation?.Cancel(); } catch (ObjectDisposedException) { }
    }

    private void RefreshPortTestValidationState()
    {
        OnPropertyChanged(nameof(CanRunPortTest));
        OnPropertyChanged(nameof(PortTestInputStatusText));
        OnPropertyChanged(nameof(PortTestInputSeverity));
        (RunPortTestCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
    }

    private bool ValidatePortTestInput(out string normalizedTarget, out string reason)
    {
        normalizedTarget = string.Empty;
        reason = string.Empty;

        if (string.IsNullOrWhiteSpace(TargetHost))
        {
            reason = "Enter a target host or IP before running a port test.";
            return false;
        }

        if (TargetPort is < MinimumTcpPort or > MaximumTcpPort)
        {
            reason = $"Enter a TCP port between {MinimumTcpPort} and {MaximumTcpPort}.";
            return false;
        }

        var candidate = NormalizeTargetValue(TargetHost);
        if (!DiagnosticTargetValidator.TryNormalizeHost(candidate, out normalizedTarget, out reason))
        {
            return false;
        }

        return true;
    }

    private void ApplyPortTestTargetPreset(object? parameter)
    {
        var target = parameter switch
        {
            PortTestTargetOption option => option.Target,
            string value => value,
            _ => string.Empty
        };

        target = NormalizeTargetValue(target);
        if (string.IsNullOrWhiteSpace(target))
        {
            return;
        }

        TargetHost = target;
        _host.NotifyStatus($"Port test target set to {TargetHost}.");
    }

    private IReadOnlyList<PortTestTargetOption> BuildPortTestTargetOptions()
    {
        var options = new List<PortTestTargetOption>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        AddTargetOption(options, seen, "Localhost", "localhost", "Loopback check on this PC.", "\uE8B7");
        AddTargetOption(options, seen, "This PC", Environment.MachineName, "Current Windows computer name.", "\uE770");

        var diagnosis = _host.LastDiagnosis;
        if (diagnosis is not null)
        {
            if (diagnosis.IpConfiguration.HasGateway)
            {
                AddTargetOption(options, seen, "Gateway", diagnosis.IpConfiguration.Gateway, "Default gateway from the latest Quick Diagnosis.", "\uE968");
            }

            var dnsIndex = 1;
            foreach (var dnsServer in diagnosis.IpConfiguration.DnsServers.Take(3))
            {
                AddTargetOption(options, seen, $"DNS {dnsIndex}", dnsServer, "Configured DNS server from the latest Quick Diagnosis.", "\uE774");
                dnsIndex++;
            }

            AddTargetOption(options, seen, "Logon server", diagnosis.Domain?.LogonServer, "Domain logon server from the latest Quick Diagnosis.", "\uE95B");
            AddTargetOption(options, seen, "Last target", diagnosis.LastPortTest?.Target, "Target used by the latest manual port test.", "\uE8C8");
            AddImportantServerOptions(options, seen, diagnosis.Profile.ImportantServers, "latest diagnosis profile");
        }

        AddImportantServerOptions(options, seen, _host.NetworkProfile.ImportantServers, "active network profile");

        return options;
    }

    private static void AddImportantServerOptions(
        List<PortTestTargetOption> options,
        HashSet<string> seen,
        IEnumerable<DiagnosticTarget> servers,
        string source)
    {
        foreach (var server in servers.Take(4))
        {
            var label = string.IsNullOrWhiteSpace(server.Name) ? "Important server" : server.Name;
            var description = string.IsNullOrWhiteSpace(server.Purpose)
                ? $"Important server from the {source}."
                : server.Purpose;
            AddTargetOption(options, seen, label, server.Host, description, "\uE968");
        }
    }

    private static void AddTargetOption(
        List<PortTestTargetOption> options,
        HashSet<string> seen,
        string label,
        string? target,
        string description,
        string icon)
    {
        target = NormalizeTargetValue(target);
        if (string.IsNullOrWhiteSpace(target) ||
            target.Equals("Unknown", StringComparison.OrdinalIgnoreCase) ||
            target.Equals("Not domain joined", StringComparison.OrdinalIgnoreCase) ||
            target.Equals("None", StringComparison.OrdinalIgnoreCase) ||
            !seen.Add(target))
        {
            return;
        }

        options.Add(new PortTestTargetOption(label, target, description, icon));
    }

    private static string NormalizeTargetValue(string? target) =>
        string.IsNullOrWhiteSpace(target)
            ? string.Empty
            : target.Trim().TrimStart('\\');

    private static string BuildPortTestInterpretation(PortTestResult? result)
    {
        if (result is null)
        {
            return "No manual TCP port result yet.";
        }

        return (result.PingSucceeded, result.TcpSucceeded) switch
        {
            (true, true) => "Host responds to ICMP and the TCP service is reachable. This path looks usable.",
            (false, true) => "TCP works even though ICMP ping failed. Treat this as service reachable; ICMP may be filtered.",
            (true, false) => "Host responds to ICMP, but the TCP port is closed, filtered, or the service is not listening.",
            _ => "No ICMP or TCP response. Check target, routing, firewall, VPN, VLAN and whether the host is online."
        };
    }
}

public sealed record PortTestPortOption(int Port, string ServiceName, string Description)
{
    public string DisplayText => $"{Port} - {ServiceName}";
}

public sealed record PortTestTargetOption(string Label, string Target, string Description, string Icon);
