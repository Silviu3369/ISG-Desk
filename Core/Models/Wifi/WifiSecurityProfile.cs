using NetScopeDiagnosticCenter.Infrastructure.Wlan;

namespace NetScopeDiagnosticCenter.Core.Models.Wifi;

/// <summary>
/// Friendly security label derived from the (auth + cipher) pair reported by WLAN.
///
/// We classify into the labels that helpdesk + engineers actually use, not the raw
/// algorithm names. Examples:
///   <c>Open</c>                                              → "Open"
///   <c>WpaPsk + Tkip</c>                                     → "WPA-Personal (TKIP)"
///   <c>RsnaPsk + Ccmp</c>                                    → "WPA2-Personal"
///   <c>Wpa3Sae + Ccmp</c>                                    → "WPA3-Personal"
///   <c>Owe + Ccmp</c>                                        → "Enhanced Open (OWE)"
///   <c>Rsna + Ccmp</c>                                       → "WPA2-Enterprise"
///   <c>Wpa3 + Gcmp256</c>                                    → "WPA3-Enterprise (192-bit)"
///
/// <para>Recommendation tagging:</para>
/// <list type="bullet">
///   <item><see cref="IsLegacyInsecure"/> — Open / WEP / WPA-Personal flagged for upgrade.</item>
///   <item><see cref="IsModernSecure"/> — WPA2/WPA3 personal &amp; enterprise (the green tier).</item>
///   <item><see cref="HasForwardSecrecy"/> — only WPA3 SAE + OWE qualify; helpdesk uses this
///         to advise on roaming-safe networks.</item>
/// </list>
/// </summary>
public sealed record WifiSecurityProfile(
    string Label,
    Dot11AuthAlgorithm Auth,
    Dot11CipherAlgorithm Cipher,
    bool IsOpen,
    bool IsLegacyInsecure,
    bool IsModernSecure,
    bool HasForwardSecrecy)
{
    /// <summary>
    /// Badge colour for the Visible Networks grid: red for Open (insecure),
    /// amber for legacy (WEP/WPA-TKIP), emerald for modern/secured.
    /// </summary>
    public string BadgeColor =>
        IsOpen ? "#EF4444"
        : IsLegacyInsecure ? "#F59E0B"
        : "#10B981";

    /// <summary>
    /// Beacon-only classification. A scanned BSS exposes only the 802.11 capability
    /// "Privacy" bit — NOT the auth scheme (PSK vs Enterprise vs WPA3). Claiming
    /// "WPA2-Enterprise" for every secured AP (as the old code did) is misleading.
    /// We honestly report just "Secured 🔒" or "Open" until the user connects (at which
    /// point <see cref="From"/> gives the precise scheme from the association).
    /// </summary>
    public static WifiSecurityProfile FromBeacon(bool privacyBit) => privacyBit
        ? new WifiSecurityProfile("Secured", Dot11AuthAlgorithm.Rsna, Dot11CipherAlgorithm.Ccmp,
            IsOpen: false, IsLegacyInsecure: false, IsModernSecure: true, HasForwardSecrecy: false)
        : new WifiSecurityProfile("Open", Dot11AuthAlgorithm.Open, Dot11CipherAlgorithm.None,
            IsOpen: true, IsLegacyInsecure: true, IsModernSecure: false, HasForwardSecrecy: false);

    /// <summary>Construct from raw native enums + the security-enabled flag.</summary>
    public static WifiSecurityProfile From(Dot11AuthAlgorithm auth, Dot11CipherAlgorithm cipher, bool securityEnabled)
    {
        // securityEnabled=false typically pairs with auth=Open + cipher=None. Treat as Open.
        if (!securityEnabled || (auth == Dot11AuthAlgorithm.Open && cipher == Dot11CipherAlgorithm.None))
        {
            return new WifiSecurityProfile("Open", auth, cipher,
                IsOpen: true, IsLegacyInsecure: true, IsModernSecure: false, HasForwardSecrecy: false);
        }

        var label = ResolveLabel(auth, cipher);
        var legacy = IsLegacy(auth, cipher);
        var modern = !legacy && (auth >= Dot11AuthAlgorithm.Rsna);
        var forwardSecrecy = auth is Dot11AuthAlgorithm.Wpa3Sae or Dot11AuthAlgorithm.Owe or Dot11AuthAlgorithm.Wpa3 or Dot11AuthAlgorithm.Wpa3Ent192;

        return new WifiSecurityProfile(label, auth, cipher,
            IsOpen: false,
            IsLegacyInsecure: legacy,
            IsModernSecure: modern,
            HasForwardSecrecy: forwardSecrecy);
    }

    private static string ResolveLabel(Dot11AuthAlgorithm auth, Dot11CipherAlgorithm cipher)
    {
        // OWE = Enhanced Open (Wi-Fi Alliance term).
        if (auth == Dot11AuthAlgorithm.Owe) return "Enhanced Open (OWE)";

        // WPA3 family
        if (auth == Dot11AuthAlgorithm.Wpa3Sae) return "WPA3-Personal";
        if (auth == Dot11AuthAlgorithm.Wpa3Ent192) return "WPA3-Enterprise (192-bit)";
        if (auth == Dot11AuthAlgorithm.Wpa3) return "WPA3-Enterprise";

        // WPA2 family — RsnaPsk = personal, Rsna = enterprise
        if (auth == Dot11AuthAlgorithm.RsnaPsk)
        {
            // Rare TKIP fallback for legacy clients.
            return cipher == Dot11CipherAlgorithm.Tkip ? "WPA2-Personal (TKIP)" : "WPA2-Personal";
        }
        if (auth == Dot11AuthAlgorithm.Rsna)
        {
            return cipher == Dot11CipherAlgorithm.Tkip ? "WPA2-Enterprise (TKIP)" : "WPA2-Enterprise";
        }

        // WPA1
        if (auth == Dot11AuthAlgorithm.WpaPsk) return $"WPA-Personal ({CipherShortName(cipher)})";
        if (auth == Dot11AuthAlgorithm.Wpa) return $"WPA-Enterprise ({CipherShortName(cipher)})";

        // WEP / shared key
        if (auth == Dot11AuthAlgorithm.SharedKey) return "WEP";

        return $"Unknown ({auth} / {cipher})";
    }

    private static bool IsLegacy(Dot11AuthAlgorithm auth, Dot11CipherAlgorithm cipher)
    {
        if (auth is Dot11AuthAlgorithm.SharedKey or Dot11AuthAlgorithm.WpaPsk or Dot11AuthAlgorithm.Wpa or Dot11AuthAlgorithm.WpaNone)
            return true;
        if (cipher is Dot11CipherAlgorithm.Wep or Dot11CipherAlgorithm.Wep40 or Dot11CipherAlgorithm.Wep104 or Dot11CipherAlgorithm.Tkip)
            return true;
        return false;
    }

    private static string CipherShortName(Dot11CipherAlgorithm cipher) => cipher switch
    {
        Dot11CipherAlgorithm.Tkip => "TKIP",
        Dot11CipherAlgorithm.Ccmp => "AES",
        Dot11CipherAlgorithm.Gcmp or Dot11CipherAlgorithm.Gcmp256 => "GCMP",
        Dot11CipherAlgorithm.None => "None",
        _ => cipher.ToString(),
    };
}
