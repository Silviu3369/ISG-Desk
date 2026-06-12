using System.IO;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using NetScopeDiagnosticCenter.Collectors;
using NetScopeDiagnosticCenter.Collectors.Wifi;
using NetScopeDiagnosticCenter.Core;
using NetScopeDiagnosticCenter.Core.Monitoring;
using NetScopeDiagnosticCenter.Core.Wifi;
using NetScopeDiagnosticCenter.Infrastructure;
using NetScopeDiagnosticCenter.Infrastructure.Wlan;
using NetScopeDiagnosticCenter.Reports;
using NetScopeDiagnosticCenter.UI;
using NetScopeDiagnosticCenter.UI.ViewModels;

namespace NetScopeDiagnosticCenter;

public partial class App : Application
{
    /// <summary>
    /// Root service provider built at startup. Exposed as a static for code paths
    /// (e.g. XAML designer fallbacks) that cannot receive constructor injection.
    /// Phase 2c onwards should prefer constructor injection via DI registrations.
    /// </summary>
    public static IServiceProvider? Services { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        var services = new ServiceCollection();
        ConfigureServices(services);
        Services = services.BuildServiceProvider();

        var mainWindow = Services.GetRequiredService<MainWindow>();
        MainWindow = mainWindow;

        // Default landing page → kick off the Technician Home snapshot load now
        // (local-only, non-blocking, single-load guarded). Lives here rather than in
        // the MainViewModel constructor so constructing the VM — e.g. in the DI
        // composition tests — has no process-spawning side effect.
        _ = Services.GetRequiredService<MainViewModel>().StartInitialLoadAsync();

        mainWindow.Show();
    }

    /// <summary>
    /// Registers all application services with their lifetimes.
    /// Phase 2a baseline; sub-ViewModels are added incrementally in phases 2b/2c/2d.
    /// </summary>
    private static void ConfigureServices(IServiceCollection services)
    {
        // === Infrastructure (singletons — shared state, expensive setup) ===
        services.AddSingleton<AppStorageService>();
        services.AddSingleton<ILoggingService>(sp =>
        {
            var storage = sp.GetRequiredService<AppStorageService>();
            storage.EnsureFolders();
            return new LoggingService(storage);
        });
        services.AddSingleton<SecureCredentialService>();
        services.AddSingleton<SnmpCredentialStore>();
        services.AddSingleton<IPowerShellRunner, PowerShellRunner>();
        services.AddSingleton<SnmpClientService>();
        services.AddSingleton<ActivityFeedService>();

        // Backwards-compat: existing collectors take the concrete PowerShellRunner.
        // Keep both registrations until every consumer has moved to IPowerShellRunner.
        services.AddSingleton(sp => (PowerShellRunner)sp.GetRequiredService<IPowerShellRunner>());

        // Backwards-compat: existing call sites take concrete LoggingService.
        services.AddSingleton(sp => (LoggingService)sp.GetRequiredService<ILoggingService>());

        // === Collectors (transient — stateless, cheap to create) ===
        services.AddTransient<NetworkAdapterCollector>();
        services.AddTransient<IpConfigurationCollector>();
        services.AddTransient<DnsCollector>();
        services.AddTransient<TraceRouteCollector>();
        services.AddTransient<WifiCollector>();
        services.AddTransient<PortTestCollector>();
        services.AddTransient<TargetConnectivityCollector>();
        services.AddTransient<TargetShareDiscoveryCollector>();
        services.AddTransient<TargetServiceDiscoveryCollector>();
        services.AddTransient<DomainControllerDiscoveryCollector>();
        services.AddTransient<DomainCollector>();
        services.AddTransient<DhcpCollector>();
        services.AddTransient<PcContextCollector>();
        services.AddTransient<LocalPrinterCollector>();
        services.AddTransient<PrintServerCollector>();
        services.AddTransient<PrinterDiscoveryCollector>();
        services.AddTransient<SnmpPrinterCollector>();
        services.AddTransient<PrinterQueueInstaller>();
        services.AddTransient<NetworkDeviceCollector>();

        // Diagnosis page repair actions — the only collectors that CHANGE system state
        // (flush DNS, renew DHCP, reset Winsock, restart adapter). Explicit, time-boxed.
        services.AddTransient<NetworkRepairService>();

        // Technician Home: read-only local system snapshot (identity, OS, network, org,
        // security posture). No network activity — safe on page load.
        services.AddTransient<SystemOverviewCollector>();
        services.AddTransient<ISystemOverviewCollector>(sp => sp.GetRequiredService<SystemOverviewCollector>());

        // === Core engines (singletons — stateless once constructed) ===
        services.AddSingleton<RuleEngine>();
        services.AddSingleton<HealthScoreCalculator>();
        services.AddSingleton<ScenarioEngine>();
        services.AddSingleton<DiagnosticEngine>();

        // === Monitoring (factory pattern, NOT singleton sessions) ===
        // Probe is shared (cheap to share); sessions are per-ViewModel and disposable.
        services.AddSingleton<IPingProbe, IcmpPingProbe>();
        services.AddSingleton<IMonitoringSessionFactory, MonitoringSessionFactory>();

        // === Reports ===
        services.AddSingleton<HtmlReportBuilder>();
        services.AddSingleton<TextSummaryBuilder>();
        services.AddSingleton<ReportStorageService>();

        // === Wi-Fi Analyzer ===
        // P/Invoke WLAN handle (singleton — owns the unmanaged HANDLE for app lifetime).
        services.AddSingleton<IWlanApi, WlanApiNative>();

        // Single scanner implementation backed by the WLAN P/Invoke layer. When the WLAN
        // AutoConfig service is unavailable, WlanApiNative.IsAvailable returns false and
        // WlanApiScanner cleanly returns empty results; the UI then shows an honest
        // "No Wi-Fi adapter / WLAN unavailable" state. (A netsh fallback was removed — it
        // could only ever see the connected network, so it added no real value while
        // implying a working degraded mode that did not exist.)
        services.AddSingleton<IWifiScanner, WlanApiScanner>();

        // Live monitoring sampler — singleton because subscribers attach once at VM ctor.
        services.AddSingleton<WifiSampler>();

        // On-disk rolling history of live samples (singleton — owns a flush timer +
        // LocalAppData CSV files; DI disposes it at app shutdown for a final flush).
        services.AddSingleton<WifiHistoryStore>();
        services.AddSingleton<WifiDeviceFriendlyNameStore>();
        services.AddSingleton<WifiDeviceTypeOverrideStore>();

        // One-shot probes — transient (each call creates a fresh HttpClient / Powershell run).
        services.AddTransient<WifiCapabilityProbe>();
        services.AddTransient<WifiPublicIpProbe>();

        // On-demand LAN device scanner (ping sweep + ARP/neighbour read + reverse DNS).
        // Transient — each scan is a fresh, self-contained PowerShell + BCL run.
        services.AddTransient<WifiLanScanner>();
        services.AddTransient<WifiRouterClientCollector>();

        // Pure-logic engine — singleton, stateless.
        services.AddSingleton<WifiAnalyzerEngine>();

        // The Wi-Fi page VM — singleton so navigation in/out preserves state (samples,
        // last scan, roaming history).
        services.AddSingleton<WifiAnalyzerViewModel>();

        // === UI ===
        services.AddSingleton<MainViewModel>();

        // Window: transient so it can be re-created if explicitly requested,
        // but typically resolved once at startup.
        services.AddTransient<MainWindow>();
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogStartupException(e.Exception);
        MessageBox.Show(
            $"ISG Desk failed to start or continue.\n\n{e.Exception.Message}",
            "ISG Desk",
            MessageBoxButton.OK,
            MessageBoxImage.Error);

        // Mark handled so WPF does not pop a second crash dialog on the same exception,
        // then shut the process down explicitly. Without Shutdown() a XAML-load failure
        // can spawn cascade exceptions and leave several zombie processes behind, each
        // holding their own modal dialog open and locking the binary so the next build
        // cannot replace it.
        e.Handled = true;
        Current?.Shutdown(exitCode: 1);
    }

    private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
        {
            LogStartupException(exception);
        }
    }

    private static void OnUnobservedTaskException(object? sender, System.Threading.Tasks.UnobservedTaskExceptionEventArgs e)
    {
        LogStartupException(e.Exception);
        e.SetObserved();
    }

    private static void LogStartupException(Exception exception)
    {
        try
        {
            var logFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ISG Desk",
                "Logs");
            Directory.CreateDirectory(logFolder);
            File.AppendAllText(
                Path.Combine(logFolder, "startup-errors.log"),
                $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}{Environment.NewLine}{exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // Startup error logging must never trigger a second crash.
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (Services is IDisposable disposable)
        {
            disposable.Dispose();
        }
        Services = null;
        base.OnExit(e);
    }
}
