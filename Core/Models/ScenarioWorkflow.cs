namespace NetScopeDiagnosticCenter.Core.Models;

public static class ScenarioWorkflow
{
    // Targeted Tests: only the scenarios that probe a specific tech-supplied target
    // Quick Diagnosis cannot reach. Zero-input checks live in Diagnosis or Wi-Fi Analyzer.
    public const string InternalShare = "InternalShare";
    public const string DnsDomain = "DnsDomain";
    public const string ServiceAccess = "ServiceAccess";

    public static IReadOnlyList<ScenarioDefinition> All { get; } =
    [
        new() { Key = InternalShare, Name = "Internal Server or Share Access", Description = "Checks DNS, ping, SMB 445 and UNC availability when a share path is entered." },
        new() { Key = DnsDomain, Name = "DNS / Domain Connectivity", Description = "Checks domain state, logon server, DNS suffixes and DC ports." },
        new() { Key = ServiceAccess, Name = "Service Access", Description = "Checks a target and one or more service ports with DNS, ping, packet loss and TCP." }
    ];

    public static string GetName(string key)
    {
        return All.FirstOrDefault(item => item.Key == key)?.Name ?? key;
    }
}
