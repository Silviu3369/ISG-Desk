using NetScopeDiagnosticCenter.Infrastructure.Wlan;

namespace NetScopeDiagnosticCenter.Core.Models.Wifi;

/// <summary>
/// What the Wi-Fi adapter supports — drives Section 7 (Adapter Capabilities) of the UI.
///
/// Populated once per page-load (capabilities don't change at runtime). Combines:
/// <list type="bullet">
///   <item>WLAN-driver-reported PHY support (queried via WlanQueryInterface)</item>
///   <item>Bands supported (derived: if PHY supports HE → 2.4 + 5 + 6 GHz; VHT → 5 GHz; etc.)</item>
///   <item>Driver name / version (queried via Get-NetAdapter PowerShell — already used by app)</item>
///   <item>Permanent MAC + country code</item>
/// </list>
/// Technologies (MIMO, MU-MIMO, OFDMA, Beamforming) are derived from the PHY set since
/// HE/EHT mandate them. We don't query the driver for individual feature flags — that
/// requires NDIS OIDs and is a bigger surface than the value justifies.
/// </summary>
public sealed record WifiAdapterCapabilities(
    Guid InterfaceGuid,
    string Description,

    /// <summary>Permanent MAC address — survives driver re-installs (no MAC randomization noise).</summary>
    string? PermanentMacAddress,

    /// <summary>Driver vendor + version, e.g. "Intel Corporation 22.260.0.0".</summary>
    string? DriverName,
    string? DriverVersion,

    /// <summary>Country / regulatory domain ("US", "RO", "DE"). Null when unknown.</summary>
    string? CountryCode,

    /// <summary>Bands the adapter can operate on (from supported PHY types).</summary>
    IReadOnlyList<WifiBand> SupportedBands,

    /// <summary>PHY types the adapter supports (Wi-Fi 4 / 5 / 6 / 6E / 7 etc.).</summary>
    IReadOnlyList<Dot11PhyType> SupportedPhyTypes,

    /// <summary>Friendly technology labels: "MIMO", "MU-MIMO", "OFDMA", "Beamforming", "Target Wake Time", etc.</summary>
    IReadOnlyList<string> SupportedTechnologies,

    /// <summary>True if the adapter can authenticate via WPA3 SAE.</summary>
    bool SupportsWpa3,

    /// <summary>True if the adapter advertises Wi-Fi 6E support (HE on 6 GHz).</summary>
    bool SupportsWifi6E,

    /// <summary>True if the adapter advertises Wi-Fi 7 (EHT) support.</summary>
    bool SupportsWifi7,

    DateTimeOffset CapturedAt)
{
    /// <summary>Sentinel returned when the adapter info couldn't be probed.</summary>
    public static WifiAdapterCapabilities Unknown(Guid interfaceGuid, string description) => new(
        interfaceGuid, description,
        PermanentMacAddress: null,
        DriverName: null, DriverVersion: null, CountryCode: null,
        SupportedBands: Array.Empty<WifiBand>(),
        SupportedPhyTypes: Array.Empty<Dot11PhyType>(),
        SupportedTechnologies: Array.Empty<string>(),
        SupportsWpa3: false, SupportsWifi6E: false, SupportsWifi7: false,
        CapturedAt: DateTimeOffset.Now);

    /// <summary>Comma-separated band labels for compact display: "2.4 / 5 / 6 GHz".</summary>
    public string BandSummary => SupportedBands.Count == 0
        ? "Unknown"
        : string.Join(" / ", SupportedBands.Select(b => b switch
        {
            WifiBand.TwoPointFourGhz => "2.4",
            WifiBand.FiveGhz => "5",
            WifiBand.SixGhz => "6",
            _ => "?",
        })) + " GHz";

    /// <summary>Compact standards summary: "Wi-Fi 4 / 5 / 6 / 6E".</summary>
    public string StandardsSummary
    {
        get
        {
            var labels = new List<string>();
            if (SupportedPhyTypes.Contains(Dot11PhyType.Ht)) labels.Add("Wi-Fi 4");
            if (SupportedPhyTypes.Contains(Dot11PhyType.Vht)) labels.Add("Wi-Fi 5");
            if (SupportedPhyTypes.Contains(Dot11PhyType.He))
            {
                labels.Add(SupportsWifi6E ? "Wi-Fi 6/6E" : "Wi-Fi 6");
            }
            if (SupportedPhyTypes.Contains(Dot11PhyType.Eht)) labels.Add("Wi-Fi 7");
            return labels.Count == 0 ? "Unknown" : string.Join(" / ", labels);
        }
    }

    /// <summary>Band labels as individual chips: ["2.4 GHz", "5 GHz", "6 GHz"].</summary>
    public IReadOnlyList<string> SupportedBandLabels =>
        SupportedBands.Count == 0
            ? new[] { "Unknown" }
            : SupportedBands.Select(b => b switch
            {
                WifiBand.TwoPointFourGhz => "2.4 GHz",
                WifiBand.FiveGhz => "5 GHz",
                WifiBand.SixGhz => "6 GHz",
                _ => "?",
            }).ToArray();

    /// <summary>Standard labels as individual chips: ["Wi-Fi 4 (n)", "Wi-Fi 5 (ac)", "Wi-Fi 6 (ax)"].</summary>
    public IReadOnlyList<string> SupportedStandardLabels
    {
        get
        {
            var labels = new List<string>();
            if (SupportedPhyTypes.Contains(Dot11PhyType.Erp)) labels.Add("Wi-Fi 3 (g)");
            if (SupportedPhyTypes.Contains(Dot11PhyType.Ht)) labels.Add("Wi-Fi 4 (n)");
            if (SupportedPhyTypes.Contains(Dot11PhyType.Vht)) labels.Add("Wi-Fi 5 (ac)");
            if (SupportedPhyTypes.Contains(Dot11PhyType.He))
                labels.Add(SupportsWifi6E ? "Wi-Fi 6E (ax)" : "Wi-Fi 6 (ax)");
            if (SupportedPhyTypes.Contains(Dot11PhyType.Eht)) labels.Add("Wi-Fi 7 (be)");
            return labels.Count == 0 ? new[] { "Unknown" } : labels.ToArray();
        }
    }
}
