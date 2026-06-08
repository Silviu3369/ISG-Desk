namespace NetScopeDiagnosticCenter.Core.Models;

public sealed class PrinterInstallResult
{
    public bool Success { get; set; }
    public string Severity { get; set; } = "Unknown";
    public string Verdict { get; set; } = string.Empty;
    public string ConnectionName { get; set; } = string.Empty;
    public string PrinterName { get; set; } = string.Empty;
    public string Error { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public List<string> Evidence { get; set; } = [];

    /// <summary>Human-readable next step derived from <see cref="Category"/> — what the tech should do now.</summary>
    public string Remediation => Category switch
    {
        "Installed" => "Print queue installed. Send a test page from Devices & Printers to confirm.",
        "AccessDenied" => "Re-run the app as a local administrator (the Point-and-Print elevation is required to install drivers).",
        "PolicyBlocked" => "Group Policy / Point-and-Print restrictions block this install. Ask the AD admin to whitelist the print server or pre-stage the driver.",
        "DriverUnavailable" => "The server didn't deliver a usable driver package. Ask the print-server admin to publish a v4 driver for this client OS / architecture.",
        "ServerUnreachable" => "The print server didn't respond. Check the UNC path, DNS, firewall on TCP 135/445 and the Spooler service on the server.",
        "TimedOut" => "Install took longer than 60 s (often a slow driver download). Try again on a faster connection or with the driver pre-installed.",
        "InstallFailed" => "Windows returned a generic install failure. Inspect the evidence and the Windows Event log (PrintService/Admin).",
        "CollectorFailed" => "The install collector itself failed before talking to Windows. See the evidence for the underlying error.",
        _ => "Inspect the evidence; if the cause is unclear, paste the Error line into the ticket."
    };
}
