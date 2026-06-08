namespace NetScopeDiagnosticCenter.Core.Models;

public sealed class NetworkDeviceInterfaceInfo
{
    public int Index { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string AdminStatus { get; set; } = "Unknown";
    public string OperStatus { get; set; } = "Unknown";
    public long? SpeedMbps { get; set; }
    public string SpeedText { get; set; } = "Unknown";
    public ulong InOctets { get; set; }
    public ulong OutOctets { get; set; }
    public ulong Errors { get; set; }
    public ulong Discards { get; set; }
    public string TrafficText { get; set; } = "Unknown";
    public double? BytesPerSecond { get; set; }
    public double? UtilizationPercent { get; set; }
    public double ErrorsPerSecond { get; set; }
    public double DiscardsPerSecond { get; set; }
    public string TrafficRateText { get; set; } = "Unknown";
    public bool NeedsAttention { get; set; }
    public string AttentionReason { get; set; } = string.Empty;
    public string RecommendedNextCheck { get; set; } = string.Empty;
    public string Verdict { get; set; } = "Unknown";
    public string Severity { get; set; } = "Unknown";

    public string NameDisplay => ValueOrUnknown(Name);

    public string DescriptionDisplay => ValueOrUnknown(Description);

    public string AdminStatusDisplay => ValueOrUnknown(AdminStatus);

    public string OperStatusDisplay => ValueOrUnknown(OperStatus);

    public string SpeedTextDisplay => ValueOrUnknown(SpeedText);

    public string TrafficTextDisplay => ValueOrUnknown(TrafficText);

    public string TrafficRateTextDisplay => ValueOrUnknown(TrafficRateText);

    public string AttentionReasonDisplay => ValueOrUnknown(AttentionReason);

    public string RecommendedNextCheckDisplay => ValueOrUnknown(RecommendedNextCheck);

    public string VerdictDisplay => ValueOrUnknown(Verdict);

    public string SeverityDisplay => ValueOrUnknown(Severity);

    private static string ValueOrUnknown(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "Unknown" : value;
}
