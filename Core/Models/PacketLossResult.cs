namespace NetScopeDiagnosticCenter.Core.Models;

public sealed class PacketLossResult
{
    public string Target { get; set; } = string.Empty;
    public string Status { get; set; } = "Unknown";
    public int Sent { get; set; }
    public int Received { get; set; }
    public double? LossPercent { get; set; }
    public string Details { get; set; } = "Not tested or unavailable.";

    public bool IsOk => string.Equals(Status, "OK", StringComparison.OrdinalIgnoreCase);
    public bool IsWarning => string.Equals(Status, "Warning", StringComparison.OrdinalIgnoreCase);
    public bool IsCritical => string.Equals(Status, "Critical", StringComparison.OrdinalIgnoreCase);

    public static PacketLossResult Unknown(string target) => new()
    {
        Target = target,
        Status = "Unknown",
        Details = "Not tested or unavailable."
    };
}
