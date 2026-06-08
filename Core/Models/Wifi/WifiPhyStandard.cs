using NetScopeDiagnosticCenter.Infrastructure.Wlan;

namespace NetScopeDiagnosticCenter.Core.Models.Wifi;

/// <summary>
/// Friendly Wi-Fi generation labels mapped from the native <see cref="Dot11PhyType"/>.
///
/// These are what the UI displays ("Wi-Fi 5", "Wi-Fi 6E") instead of the wonky
/// IEEE letter codes ("802.11ac", "802.11ax 6 GHz"):
///  <list type="bullet">
///   <item><see cref="ToFriendlyName"/> — full label for AP rows + connection card</item>
///   <item><see cref="ToShortLabel"/> — compact tag ("ax") for dense DataGrid columns</item>
///   <item><see cref="ToIeeeName"/> — raw IEEE name ("802.11ax") for the export / engineers</item>
///  </list>
/// </summary>
public static class WifiPhyStandard
{
    /// <summary>
    /// Maps the native PHY type + band to a display name.
    /// Wi-Fi 6 on 6 GHz becomes "Wi-Fi 6E" (only HE PHY supports 6 GHz officially).
    /// </summary>
    public static string ToFriendlyName(Dot11PhyType phy, WifiBand band) => phy switch
    {
        Dot11PhyType.Ofdm => "Wi-Fi 1 (a)",
        Dot11PhyType.Dsss or Dot11PhyType.HrDsss => "Wi-Fi 2 (b)",
        Dot11PhyType.Erp => "Wi-Fi 3 (g)",
        Dot11PhyType.Ht => "Wi-Fi 4 (n)",
        Dot11PhyType.Vht => "Wi-Fi 5 (ac)",
        Dot11PhyType.He when band == WifiBand.SixGhz => "Wi-Fi 6E (ax)",
        Dot11PhyType.He => "Wi-Fi 6 (ax)",
        Dot11PhyType.Eht => "Wi-Fi 7 (be)",
        Dot11PhyType.Dmg => "WiGig (ad, 60 GHz)",
        Dot11PhyType.Fhss => "Legacy FHSS",
        _ => "Unknown",
    };

    /// <summary>Compact tag for DataGrid columns: "ax", "ac", "n", etc.</summary>
    public static string ToShortLabel(Dot11PhyType phy) => phy switch
    {
        Dot11PhyType.Ofdm => "a",
        Dot11PhyType.Dsss or Dot11PhyType.HrDsss => "b",
        Dot11PhyType.Erp => "g",
        Dot11PhyType.Ht => "n",
        Dot11PhyType.Vht => "ac",
        Dot11PhyType.He => "ax",
        Dot11PhyType.Eht => "be",
        Dot11PhyType.Dmg => "ad",
        _ => "?",
    };

    /// <summary>Full IEEE name for engineer-facing reports / JSON export.</summary>
    public static string ToIeeeName(Dot11PhyType phy) => phy switch
    {
        Dot11PhyType.Ofdm => "802.11a",
        Dot11PhyType.Dsss => "802.11 (DSSS legacy)",
        Dot11PhyType.HrDsss => "802.11b",
        Dot11PhyType.Erp => "802.11g",
        Dot11PhyType.Ht => "802.11n",
        Dot11PhyType.Vht => "802.11ac",
        Dot11PhyType.He => "802.11ax",
        Dot11PhyType.Eht => "802.11be",
        Dot11PhyType.Dmg => "802.11ad",
        Dot11PhyType.Fhss => "802.11 FHSS",
        _ => "Unknown",
    };
}
