using System.Reflection;

namespace NetScopeDiagnosticCenter.UI.ViewModels;

/// <summary>
/// Display-only model for <see cref="AboutWindow"/>. The About dialog is a product page:
/// it tells the technician what ISG Desk is and what it can do, not a dump of build/runtime
/// internals.
/// </summary>
public sealed class AboutViewModel
{
    public AboutViewModel()
    {
        var asm = typeof(AboutViewModel).Assembly;
        Version = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                  ?? asm.GetName().Version?.ToString()
                  ?? "Unknown";
    }

    public string Version { get; }
    public string VersionLabel => $"Version {Version}";

    public string Tagline => "IT Support & Network Diagnostics";

    /// <summary>Short, plain description of what the app is for.</summary>
    public string Intro =>
        "ISG Desk helps IT helpdesk technicians find and explain network problems on a Windows PC - "
        + "with clear evidence, confidence levels and ticket-ready reports.";

    public string Purpose =>
        "The purpose of the application is to give a technician one local console for first-line triage, "
        + "targeted network checks, printer discovery, network-device inspection, Wi-Fi analysis and report export. "
        + "It is designed to reduce guesswork during support calls by showing what was tested, what evidence was found "
        + "and what the recommended next action is.";

    public IReadOnlyList<AboutWorkflowStep> WorkflowSteps { get; } =
    [
        new("1", "Start on Technician Home",
            "Confirm the PC identity, Windows context, network posture, security state and last diagnosis summary."),
        new("2", "Run Diagnosis",
            "Use Quick Diagnosis to establish the baseline: adapter, IP, gateway, DNS, internet reachability and confidence."),
        new("3", "Use focused modules",
            "Open Targeted Tests, Printers, Network Devices or Wi-Fi Analyzer only when the incident points to that area."),
        new("4", "Export evidence",
            "Use Report Center or module copy/export actions to attach clear results to a ticket or escalation.")
    ];

    public IReadOnlyList<string> SafetyNotes { get; } =
    [
        "Default workflows are local-first and read-only where possible.",
        "SNMP reads use explicit credentials and should stay on private trusted networks.",
        "Actions that can change Windows state, such as printer install or forgetting a Wi-Fi profile, are separated behind clear buttons.",
        "The app does not replace enterprise monitoring; it provides technician-side evidence for faster troubleshooting."
    ];

    public IReadOnlyList<string> ReportOutputs { get; } =
    [
        "HTML report for readable ticket attachment.",
        "TXT summary for quick paste into service-desk notes.",
        "JSON evidence for structured handoff or later automation.",
        "WLAN report when Windows wireless diagnostics are needed."
    ];

    /// <summary>Footer credit line. Year is current so it never goes stale.</summary>
    public string Copyright => $"Copyright {DateTime.Now.Year} ISG Desk - Ionel Silviu Ghimpau";

    /// <summary>Single compact open-source attribution line shown above the footer.</summary>
    public string CreditsLine =>
        "Built with Serilog, SharpSnmpLib and Microsoft.Extensions.DependencyInjection.";

    /// <summary>The capabilities of the app, one entry per functional module.</summary>
    public IReadOnlyList<AboutFeature> Features { get; } =
    [
        new AboutFeature("\uE80F", "#3B82F6", "#2563EB", "Technician Home",
            "Read-only system, network and security overview for the current PC."),
        new AboutFeature("\uE9D9", "#14B8A6", "#0F766E", "Diagnosis",
            "One-click PC network health check with a clear verdict and evidence."),
        new AboutFeature("\uE8F1", "#D97706", "#B45309", "Targeted Tests",
            "Focused connectivity checks for a chosen server, service or website."),
        new AboutFeature("\uE749", "#A855F7", "#9333EA", "Printers",
            "Discover and troubleshoot printers locally and over SNMP."),
        new AboutFeature("\uE968", "#10B981", "#047857", "Network Devices",
            "Inspect switches and routers via SNMP - ports, errors and uptime."),
        new AboutFeature("\uE701", "#0EA5E9", "#0369A1", "Wi-Fi Analyzer",
            "Live signal, channel map, roaming history and nearby networks."),
        new AboutFeature("\uE8A5", "#E11D48", "#BE123C", "Report Center",
            "Export HTML, TXT and JSON reports to attach to support tickets."),
    ];
}

/// <summary>One capability shown in the About dialog's feature list.</summary>
public sealed record AboutFeature(string Icon, string AccentColorStart, string AccentColorEnd, string Title, string Detail);

public sealed record AboutWorkflowStep(string Number, string Title, string Detail);
