namespace NetScopeDiagnosticCenter.Core.Models.Wifi;

/// <summary>
/// Unified device row merged from local discovery, advertised names, and router/AP data.
/// This is the operator-facing inventory; the raw local/router tables remain evidence.
/// </summary>
public sealed record WifiDeviceInventoryItem(
    string Key,
    string IpAddress,
    string? MacAddress,
    string DisplayName,
    string? LearnedName,
    string? ManualName,
    string? Vendor,
    string DeviceType,
    string AutoDeviceType,
    string? ManualDeviceType,
    string DeviceTypeSource,
    string Confidence,
    string Sources,
    string Role,
    string Evidence,
    bool IsLocalDetected,
    bool IsRouterReported,
    DateTimeOffset ObservedAt = default,
    DateTimeOffset FirstSeenAt = default,
    DateTimeOffset LastSeenAt = default,
    int SeenCount = 0,
    string WirelessStatus = "Not wireless-proven",
    string WirelessEvidence = "No AP/controller association evidence.",
    bool IsWirelessCandidate = false)
{
    public string MacDisplay => string.IsNullOrWhiteSpace(MacAddress) ? "-" : MacAddress!;
    public string LearnedNameDisplay => string.IsNullOrWhiteSpace(LearnedName) ? "-" : LearnedName!;
    public string VendorDisplay => string.IsNullOrWhiteSpace(Vendor) ? "-" : Vendor!;
    public string ManualNameDisplay => string.IsNullOrWhiteSpace(ManualName) ? "-" : ManualName!;
    public string AutoDeviceTypeDisplay => string.IsNullOrWhiteSpace(AutoDeviceType) ? "-" : AutoDeviceType;
    public string ManualDeviceTypeDisplay => string.IsNullOrWhiteSpace(ManualDeviceType) ? "-" : ManualDeviceType!;
    public string DeviceTypeSourceDisplay => string.IsNullOrWhiteSpace(DeviceTypeSource) ? "Automatic" : DeviceTypeSource;
    public string SourceDisplay => string.IsNullOrWhiteSpace(Sources) ? "Unknown" : Sources;
    public string RoleDisplay => string.IsNullOrWhiteSpace(Role) ? "-" : Role;
    public string EvidenceDisplay => string.IsNullOrWhiteSpace(Evidence) ? "No extra evidence." : Evidence;
    public string LocalDetectedDisplay => IsLocalDetected ? "Yes" : "No";
    public string RouterReportedDisplay => IsRouterReported ? "Yes" : "No";
    public string WirelessStatusDisplay => string.IsNullOrWhiteSpace(WirelessStatus) ? "Not wireless-proven" : WirelessStatus;
    public string WirelessEvidenceDisplay => string.IsNullOrWhiteSpace(WirelessEvidence) ? "No wireless evidence." : WirelessEvidence;
    public string ObservedAtDisplay => ObservedAt == default ? "-" : $"{ObservedAt:yyyy-MM-dd HH:mm:ss zzz}";
    public string FirstSeenDisplay => FirstSeenAt == default ? "-" : $"{FirstSeenAt:yyyy-MM-dd HH:mm:ss zzz}";
    public string LastSeenDisplay => LastSeenAt == default ? ObservedAtDisplay : $"{LastSeenAt:yyyy-MM-dd HH:mm:ss zzz}";
    public string SeenCountDisplay => SeenCount <= 0 ? "-" : SeenCount.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
