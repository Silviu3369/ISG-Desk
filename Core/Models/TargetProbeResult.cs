namespace NetScopeDiagnosticCenter.Core.Models;

public sealed class TargetProbeResult
{
    public DiagnosticTarget Target { get; set; } = new();
    public string ResolvedAddress { get; set; } = "Unknown";
    public string DnsStatus { get; set; } = "Unknown";
    public string PingStatus { get; set; } = "Unknown";
    public double? AverageLatencyMs { get; set; }
    public PacketLossResult PacketLoss { get; set; } = PacketLossResult.Unknown("Target");
    public List<PortProbeResult> Ports { get; set; } = [];
    public string ShareStatus { get; set; } = "Not tested";
    public string ShareDetails { get; set; } = string.Empty;
    public string Verdict { get; set; } = "Not evaluated";
    public string OwnerSuggestion { get; set; } = "Local Support";
    public string CollectorStatus { get; set; } = "OK";
    public string Details { get; set; } = string.Empty;

    // ---- Share enumeration (NetShareEnum, file-server targets only) ----

    /// <summary>"OK" / "AccessDenied" / "Unavailable" / "Not run".</summary>
    public string ShareEnumStatus { get; set; } = "Not run";

    public string ShareEnumDetails { get; set; } = string.Empty;

    /// <summary>Display labels of the shares the server publishes ("Scans", "C$ (hidden)").</summary>
    public List<string> VisibleShares { get; set; } = [];

    /// <summary>Compact cell text for the target results grid.</summary>
    public string VisibleSharesText =>
        ShareEnumStatus switch
        {
            "OK" => VisibleShares.Count == 0 ? "none published" : string.Join(", ", VisibleShares),
            "AccessDenied" => "list refused",
            "Unavailable" => "—",
            _ => "—"
        };

    /// <summary>One-line evidence sentence for steps / ticket copy.</summary>
    public string ShareEnumSummary =>
        ShareEnumStatus switch
        {
            "OK" => VisibleShares.Count == 0
                ? "Server lists no shares (none published, or hidden from this user)."
                : $"{VisibleShares.Count} share(s) published: {string.Join(", ", VisibleShares)}.",
            "AccessDenied" => "The server refused to list its shares for the current user (enumeration restricted).",
            "Unavailable" => string.IsNullOrWhiteSpace(ShareEnumDetails) ? "Share enumeration unavailable." : ShareEnumDetails,
            _ => "Share enumeration not run."
        };

    public string PortSummary =>
        Ports.Count == 0
            ? "Not tested"
            : string.Join(", ", Ports.Select(port => $"{port.Port}:{port.StatusText}"));

    public string LossText =>
        PacketLoss.LossPercent.HasValue ? $"{PacketLoss.LossPercent.Value:N1}%" : "Unknown";

    public string LatencyText =>
        AverageLatencyMs.HasValue ? $"{AverageLatencyMs.Value:N1} ms" : "Unknown";
}
