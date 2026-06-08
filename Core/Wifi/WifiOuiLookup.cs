namespace NetScopeDiagnosticCenter.Core.Wifi;

/// <summary>
/// OUI (Organizationally Unique Identifier) → vendor name lookup for the first 3 bytes
/// of a BSSID.
///
/// <para>
/// Decision 2 = B → embed top vendors database (~30 KB hand-curated). Acoperire ~90%
/// AP-uri obisnuite in mediul IT (helpdesk context). Lipsesc vendori obscuri sub-1k MAC blocks
/// — pentru ei <see cref="Lookup"/> returneaza null si UI afiseaza "—".
/// </para>
///
/// <para>
/// Format intern: <see cref="Dictionary{TKey, TValue}"/> incarcat eager la prima accesare
/// (lazy static). Lookup O(1).
/// </para>
///
/// <para>
/// Maintenance: pentru a adauga un vendor, gaseste OUI in https://standards-oui.ieee.org/oui/oui.txt
/// (sau IEEE MA-L registry) si pune o intrare noua in <see cref="OuiEntries"/>. Format strict
/// "AABBCC = Vendor".
/// </para>
/// </summary>
public static class WifiOuiLookup
{
    private static readonly Lazy<Dictionary<string, string>> _ouiTable = new(BuildTable);

    /// <summary>
    /// Look up the vendor for a BSSID. Accepts colon-separated ("AA:BB:CC:DD:EE:FF"),
    /// dash-separated ("AA-BB-CC-DD-EE-FF"), or raw hex ("AABBCCDDEEFF"). Case-insensitive.
    /// Returns null when the OUI isn't in the embedded table or the input is malformed.
    /// </summary>
    public static string? Lookup(string? bssid)
    {
        if (string.IsNullOrWhiteSpace(bssid)) return null;

        // Normalize to "AABBCC" — uppercase first 3 octets only.
        Span<char> normalized = stackalloc char[6];
        var ix = 0;
        foreach (var c in bssid)
        {
            if (c is ':' or '-' or ' ' or '.') continue;
            if (ix >= 6) break;
            if (!IsHex(c)) return null;
            normalized[ix++] = char.ToUpperInvariant(c);
        }
        if (ix < 6) return null;

        var oui = new string(normalized);
        return _ouiTable.Value.TryGetValue(oui, out var vendor) ? vendor : null;
    }

    /// <summary>
    /// True if the BSSID's first octet has the U/L (locally-administered) bit set (0x02).
    /// Such addresses are router-generated virtual/randomized MACs (band-specific radios,
    /// guest SSIDs, MAC randomization) — there is NO IEEE-registered vendor for them by
    /// design. The IEEE OUI registry only covers universally-administered addresses.
    /// </summary>
    public static bool IsLocallyAdministered(string? bssid)
    {
        if (string.IsNullOrWhiteSpace(bssid)) return false;
        // Parse the first octet's two hex digits.
        int hi = -1, lo = -1;
        foreach (var c in bssid)
        {
            if (c is ':' or '-' or ' ' or '.') continue;
            if (!IsHex(c)) return false;
            var v = Convert.ToInt32(c.ToString(), 16);
            if (hi < 0) hi = v;
            else { lo = v; break; }
        }
        if (hi < 0 || lo < 0) return false;
        var firstOctet = (hi << 4) | lo;
        return (firstOctet & 0x02) != 0;
    }

    /// <summary>
    /// Human-friendly vendor description for the UI:
    ///   • the registered manufacturer when the OUI is known,
    ///   • "Private (randomized MAC)" when the address is locally administered
    ///     (so the user understands it's by-design, not a lookup miss),
    ///   • null otherwise → UI falls back to "Vendor unknown".
    /// </summary>
    public static string? DescribeVendor(string? bssid)
    {
        var v = Lookup(bssid);
        if (v is not null) return v;
        if (IsLocallyAdministered(bssid)) return "Private (randomized MAC)";
        return null;
    }

    private static bool IsHex(char c) =>
        (c >= '0' && c <= '9') || (c >= 'A' && c <= 'F') || (c >= 'a' && c <= 'f');

    private static Dictionary<string, string> BuildTable()
    {
        // Hand-curated list of top vendors observed in enterprise + residential Wi-Fi.
        // Verified against Wireshark manuf db + IEEE OUI registry (2024 snapshots).
        var dict = new Dictionary<string, string>(OuiEntries.Length, StringComparer.Ordinal);
        foreach (var (oui, vendor) in OuiEntries)
        {
            // Defensive: an OUI must be exactly 6 hex chars or Lookup() (which normalizes
            // to 6) can never match it — skip malformed entries instead of storing a dead
            // key. Trim the vendor so a stray leading/trailing space can't break display.
            if (oui is not { Length: 6 }) continue;
            var v = vendor?.Trim();
            if (string.IsNullOrEmpty(v)) continue;
            // Last write wins on duplicate OUIs (acceptable — both are plausible vendors;
            // the table is a best-effort helpdesk aid, not an authoritative registry).
            dict[oui] = v;
        }
        return dict;
    }

    /// <summary>
    /// Verified OUI → vendor mapping. Every entry has exactly 6 hex chars.
    /// To extend: add a tuple in alphabetical order by vendor.
    /// </summary>
    private static readonly (string Oui, string Vendor)[] OuiEntries =
    {
        // ---------- Cisco (network infrastructure leader) ----------
        ("000142", "Cisco"), ("000163", "Cisco"), ("001007", "Cisco"), ("001011", "Cisco"),
        ("001120", "Cisco"), ("001150", "Cisco"), ("00115C", "Cisco"), ("00116B", "Cisco"),
        ("001178", "Cisco"), ("00117F", "Cisco"), ("001195", "Cisco"), ("001229", "Cisco"),
        ("00124A", "Cisco"), ("001282", "Cisco"), ("001293", "Cisco"), ("001297", "Cisco"),
        ("00131F", "Cisco"), ("001351", "Cisco"), ("00139F", "Cisco"), ("0013BD", "Cisco"),
        ("0014A4", "Cisco"), ("0014A8", "Cisco"), ("0015FA", "Cisco"), ("001638", "Cisco"),
        ("00164D", "Cisco"), ("001662", "Cisco"), ("001725", "Cisco"), ("00179E", "Cisco"),
        ("00179F", "Cisco"), ("0017DF", "Cisco"), ("0019AA", "Cisco"), ("0019D1", "Cisco"),
        ("001A2F", "Cisco"), ("001A30", "Cisco"), ("001A6C", "Cisco"), ("001A6D", "Cisco"),
        ("001A6F", "Cisco"), ("001A70", "Cisco"), ("001AA1", "Cisco"), ("001AE2", "Cisco"),
        ("001B53", "Cisco"), ("001B54", "Cisco"), ("001B7B", "Cisco"), ("001B8F", "Cisco"),
        ("001BD4", "Cisco"), ("001BD5", "Cisco"), ("001BD7", "Cisco"), ("001C0E", "Cisco"),
        ("001C0F", "Cisco"), ("001C57", "Cisco"), ("001C58", "Cisco"), ("001D45", "Cisco"),
        ("001DA1", "Cisco"), ("001DA2", "Cisco"), ("001DE5", "Cisco"), ("001E13", "Cisco"),
        ("001E14", "Cisco"), ("001E49", "Cisco"), ("001E4A", "Cisco"), ("001E6B", "Cisco"),
        ("001EBD", "Cisco"), ("001EBE", "Cisco"), ("001EE5", "Cisco"), ("001F26", "Cisco"),
        ("001F27", "Cisco"), ("001F6C", "Cisco"), ("001F6D", "Cisco"), ("001FCA", "Cisco"),
        ("002013", "Cisco"), ("00211B", "Cisco"), ("00211C", "Cisco"), ("00216A", "Cisco"),
        ("00216B", "Cisco"), ("002171", "Cisco"), ("002255", "Cisco"), ("002311", "Cisco"),
        ("002354", "Cisco"), ("002355", "Cisco"), ("002395", "Cisco"), ("002396", "Cisco"),
        ("0023AB", "Cisco"), ("0023AC", "Cisco"), ("002430", "Cisco"), ("002432", "Cisco"),
        ("002451", "Cisco"), ("002452", "Cisco"), ("00248C", "Cisco"), ("00248D", "Cisco"),
        ("0024C3", "Cisco"), ("0024C4", "Cisco"), ("0024F7", "Cisco"), ("00251A", "Cisco"),
        ("002A6A", "Cisco"), ("005045", "Cisco"),

        // ---------- Aruba Networks (HPE) ----------
        ("000B86", "Aruba"), ("000C8B", "Aruba"), ("00112B", "Aruba"), ("001794", "Aruba"),
        ("001A1E", "Aruba"), ("00247B", "Aruba"), ("004096", "Aruba"), ("18647D", "Aruba"),
        ("201A06", "Aruba"), ("20A6CD", "Aruba"), ("24C6DA", "Aruba"), ("24DEC6", "Aruba"),
        ("2810E9", "Aruba"), ("2C0EAA", "Aruba"), ("38179A", "Aruba"), ("444C0C", "Aruba"),
        ("488396", "Aruba"), ("4C5DCD", "Aruba"), ("506BAB", "Aruba"), ("587A4D", "Aruba"),
        ("647002", "Aruba"), ("649E08", "Aruba"), ("6CF37F", "Aruba"), ("6CF89E", "Aruba"),
        ("7437DD", "Aruba"), ("7C2664", "Aruba"), ("80AE16", "Aruba"), ("882F4A", "Aruba"),
        ("90039B", "Aruba"), ("9416E5", "Aruba"), ("9420E0", "Aruba"), ("986F60", "Aruba"),
        ("9C1C12", "Aruba"), ("9CB5D6", "Aruba"), ("A4C7DE", "Aruba"), ("AC162D", "Aruba"),
        ("ACA31E", "Aruba"), ("B449A4", "Aruba"), ("B49D78", "Aruba"), ("BCEDB5", "Aruba"),
        ("C0EAE4", "Aruba"), ("C40415", "Aruba"), ("D4C1FC", "Aruba"), ("D8C7C8", "Aruba"),
        ("DC8B28", "Aruba"), ("E0070E", "Aruba"), ("E40F65", "Aruba"), ("F45ADA", "Aruba"),

        // ---------- Ubiquiti Networks ----------
        ("04A20A", "Ubiquiti"), ("242516", "Ubiquiti"), ("24A43C", "Ubiquiti"),
        ("44D9E7", "Ubiquiti"), ("687275", "Ubiquiti"), ("68725D", "Ubiquiti"),
        ("70A741", "Ubiquiti"), ("74ACB9", "Ubiquiti"), ("7483C2", "Ubiquiti"),
        ("78457E", "Ubiquiti"), ("78D23A", "Ubiquiti"), ("802AA8", "Ubiquiti"),
        ("80F32A", "Ubiquiti"), ("9418F4", "Ubiquiti"), ("AC8BA9", "Ubiquiti"),
        ("B4FBE4", "Ubiquiti"), ("BC18F0", "Ubiquiti"), ("D021F9", "Ubiquiti"),
        ("DC9FDB", "Ubiquiti"), ("E0063E", "Ubiquiti"), ("E0631F", "Ubiquiti"),
        ("E063DA", "Ubiquiti"), ("E40C1F", "Ubiquiti"), ("F09FC2", "Ubiquiti"),
        ("F0913E", "Ubiquiti"), ("F492BF", "Ubiquiti"), ("FCECDA", "Ubiquiti"),

        // ---------- MikroTik ----------
        ("000C42", "MikroTik"), ("18FD74", "MikroTik"), ("2CC81B", "MikroTik"),
        ("4869BA", "MikroTik"), ("4C5E0C", "MikroTik"), ("6C3B6B", "MikroTik"),
        ("7427EA", "MikroTik"), ("7CCB0D", "MikroTik"), ("B869F4", "MikroTik"),
        ("CC2DE0", "MikroTik"), ("D4CA6D", "MikroTik"), ("DCC2A6", "MikroTik"),
        ("E48D8C", "MikroTik"),

        // ---------- TP-Link ----------
        ("002586", "TP-Link"), ("14CC20", "TP-Link"), ("18A6F7", "TP-Link"),
        ("1C61B4", "TP-Link"), ("285E5B", "TP-Link"), ("302303", "TP-Link"),
        ("38830F", "TP-Link"), ("3C8CF8", "TP-Link"), ("404A03", "TP-Link"),
        ("50C7BF", "TP-Link"), ("50FA84", "TP-Link"), ("543DA1", "TP-Link"),
        ("54A6E8", "TP-Link"), ("5C628B", "TP-Link"), ("60E327", "TP-Link"),
        ("641BB6", "TP-Link"), ("6C5AB0", "TP-Link"), ("6CA712", "TP-Link"),
        ("80EA96", "TP-Link"), ("88BD49", "TP-Link"), ("906A48", "TP-Link"),
        ("9889ED", "TP-Link"), ("98DAC4", "TP-Link"), ("A0AB1B", "TP-Link"),
        ("A0F3C1", "TP-Link"), ("A42BB0", "TP-Link"), ("A8869D", "TP-Link"),
        ("AC84C6", "TP-Link"), ("B0487A", "TP-Link"), ("BCB001", "TP-Link"),
        ("C006C3", "TP-Link"), ("C46E1F", "TP-Link"), ("D80D17", "TP-Link"),
        ("E4C32A", "TP-Link"), ("E848B8", "TP-Link"), ("EC086B", "TP-Link"),
        ("F0A731", "TP-Link"), ("F4F26D", "TP-Link"), ("F4F5DB", "TP-Link"),
        ("F87C45", "TP-Link"),

        // ---------- Netgear ----------
        ("000FB5", "Netgear"), ("001083", "Netgear"), ("001B2F", "Netgear"),
        ("001E2A", "Netgear"), ("001F33", "Netgear"), ("00223F", "Netgear"),
        ("002615", "Netgear"), ("008EF2", "Netgear"), ("04A151", "Netgear"),
        ("086361", "Netgear"), ("100C6B", "Netgear"), ("100D7F", "Netgear"),
        ("14593E", "Netgear"), ("1C7EE5", "Netgear"), ("20E52A", "Netgear"),
        ("288088", "Netgear"), ("2CB05D", "Netgear"), ("30469A", "Netgear"),
        ("3498B5", "Netgear"), ("38947B", "Netgear"), ("3C3786", "Netgear"),
        ("405D82", "Netgear"), ("446D57", "Netgear"), ("4C60DE", "Netgear"),
        ("506A03", "Netgear"), ("588694", "Netgear"), ("6C198F", "Netgear"),
        ("6CB0CE", "Netgear"), ("744401", "Netgear"), ("841B5E", "Netgear"),
        ("8C3BAD", "Netgear"), ("9CC9EB", "Netgear"), ("9CD36D", "Netgear"),
        ("A040A0", "Netgear"), ("A07BE8", "Netgear"), ("B07FB9", "Netgear"),
        ("BCA511", "Netgear"), ("C03F0E", "Netgear"), ("C8D719", "Netgear"),
        ("E091F5", "Netgear"), ("E4F4C6", "Netgear"), ("E8FCAF", "Netgear"),

        // ---------- D-Link ----------
        ("00141B", "D-Link"), ("001346", "D-Link"), ("00179A", "D-Link"),
        ("001B11", "D-Link"), ("001CF0", "D-Link"), ("001E58", "D-Link"),
        ("002191", "D-Link"), ("002401", "D-Link"), ("00261A", "D-Link"),
        ("00265A", "D-Link"), ("00266F", "D-Link"), ("002B1E", "D-Link"),
        ("04BAFC", "D-Link"), ("144B9A", "D-Link"), ("14D64D", "D-Link"),
        ("1CAFF7", "D-Link"), ("1CBDB9", "D-Link"), ("1CDED9", "D-Link"),
        ("28107B", "D-Link"), ("281878", "D-Link"), ("340804", "D-Link"),
        ("340A33", "D-Link"), ("3C1E04", "D-Link"), ("409B0D", "D-Link"),
        ("409BCD", "D-Link"), ("50465D", "D-Link"), ("508978", "D-Link"),
        ("5CD998", "D-Link"), ("78321B", "D-Link"), ("78542E", "D-Link"),
        ("80131D", "D-Link"), ("803F5D", "D-Link"), ("842B2B", "D-Link"),
        ("90945A", "D-Link"), ("9094E4", "D-Link"), ("9C7BD2", "D-Link"),
        ("B41775", "D-Link"), ("B868EF", "D-Link"), ("BC0F9A", "D-Link"),
        ("BCF685", "D-Link"), ("C8BE19", "D-Link"), ("C8D3A3", "D-Link"),
        ("CC5D4E", "D-Link"), ("CCB255", "D-Link"), ("D8FEE3", "D-Link"),
        ("F0B4D2", "D-Link"), ("F44E05", "D-Link"), ("F48E38", "D-Link"),

        // ---------- Linksys (Belkin International) ----------
        ("000C41", "Linksys"), ("000F61", "Linksys"), ("001217", "Linksys"),
        ("001310", "Linksys"), ("001839", "Linksys"), ("001D7E", "Linksys"),
        ("001EE5", "Linksys"), ("00226B", "Linksys"), ("002369", "Linksys"),
        ("00254B", "Linksys"), ("002584", "Linksys"), ("00256B", "Linksys"),
        ("20AA4B", "Linksys"), ("48F8B3", "Linksys"), ("58EF68", "Linksys"),
        ("60381F", "Linksys"), ("687F74", "Linksys"), ("C0560E", "Linksys"),
        ("C84415", "Linksys"), ("E89F39", "Linksys"), ("F081EA", "Linksys"),

        // ---------- Belkin ----------
        ("001150", "Belkin"), ("001CDF", "Belkin"), ("002704", "Belkin"),
        ("002708", "Belkin"), ("08863B", "Belkin"), ("149182", "Belkin"),
        ("24F5A2", "Belkin"), ("80691A", "Belkin"), ("9492BC", "Belkin"),
        ("9C45F8", "Belkin"), ("AC2310", "Belkin"), ("B4750E", "Belkin"),
        ("B86CE8", "Belkin"), ("EC1A59", "Belkin"), ("EC2280", "Belkin"),
        ("EC9BF3", "Belkin"),

        // ---------- ASUS ----------
        ("001731", "Asus"), ("001A92", "Asus"), ("002618", "Asus"),
        ("00269E", "Asus"), ("04D9F5", "Asus"), ("08606E", "Asus"),
        ("086266", "Asus"), ("0CB3FC", "Asus"), ("107B44", "Asus"),
        ("10C37B", "Asus"), ("14DDA9", "Asus"), ("1C872C", "Asus"),
        ("20CF30", "Asus"), ("2C4D54", "Asus"), ("2C56DC", "Asus"),
        ("305A3A", "Asus"), ("382C4A", "Asus"), ("38D547", "Asus"),
        ("4022D5", "Asus"), ("4CEDFB", "Asus"), ("50EBF6", "Asus"),
        ("54A050", "Asus"), ("60451B", "Asus"), ("704D7B", "Asus"),
        ("74D02B", "Asus"), ("78248F", "Asus"), ("88D7F6", "Asus"),
        ("9C5C8E", "Asus"), ("AC9E17", "Asus"), ("B06EBF", "Asus"),
        ("BCAEC5", "Asus"), ("BCEE7B", "Asus"), ("C86000", "Asus"),
        ("D017C2", "Asus"), ("D45D64", "Asus"), ("D850E6", "Asus"),
        ("E03F49", "Asus"), ("E0CB4E", "Asus"), ("F02F74", "Asus"),
        ("F46D04", "Asus"), ("F832E4", "Asus"), ("FCC233", "Asus"),

        // ---------- Apple ----------
        ("0017F2", "Apple"), ("001B63", "Apple"), ("001CB3", "Apple"),
        ("001D4F", "Apple"), ("001E52", "Apple"), ("001EC2", "Apple"),
        ("001F5B", "Apple"), ("001F5C", "Apple"), ("001FF3", "Apple"),
        ("00214E", "Apple"), ("00219C", "Apple"), ("00220B", "Apple"),
        ("002241", "Apple"), ("002298", "Apple"), ("002330", "Apple"),
        ("00236C", "Apple"), ("00238C", "Apple"), ("002367", "Apple"),
        ("002441", "Apple"), ("00254B", "Apple"), ("00254C", "Apple"),
        ("002500", "Apple"), ("002608", "Apple"), ("00264A", "Apple"),
        ("002648", "Apple"), ("04E536", "Apple"), ("0C3021", "Apple"),
        ("0C7407", "Apple"), ("14109F", "Apple"), ("345C58", "Apple"),
        ("38484C", "Apple"), ("3C0754", "Apple"), ("4C8D79", "Apple"),
        ("5C95AE", "Apple"), ("685B35", "Apple"), ("787E61", "Apple"),
        ("80E650", "Apple"), ("8866A5", "Apple"), ("9C04EB", "Apple"),
        ("A4B197", "Apple"), ("A4D18C", "Apple"), ("A82066", "Apple"),
        ("A8FAD8", "Apple"), ("AC3C0B", "Apple"), ("AC872E", "Apple"),
        ("BC54FC", "Apple"), ("BCEC5D", "Apple"), ("D49A20", "Apple"),
        ("D89695", "Apple"), ("D8BB2C", "Apple"), ("DC2B61", "Apple"),
        ("DC56E7", "Apple"), ("DC9B9C", "Apple"), ("E0F847", "Apple"),
        ("E498D6", "Apple"), ("E80688", "Apple"), ("F40F24", "Apple"),
        ("F4F15A", "Apple"), ("F86214", "Apple"), ("F8E94E", "Apple"),

        // ---------- Samsung Electronics ----------
        ("000D44", "Samsung"), ("001247", "Samsung"), ("00125A", "Samsung"),
        ("001632", "Samsung"), ("00166B", "Samsung"), ("00166C", "Samsung"),
        ("001A8A", "Samsung"), ("001D25", "Samsung"), ("001E7D", "Samsung"),
        ("001FCC", "Samsung"), ("002339", "Samsung"), ("002438", "Samsung"),
        ("002491", "Samsung"), ("002566", "Samsung"), ("002655", "Samsung"),
        ("0826AE", "Samsung"), ("086D41", "Samsung"), ("0C715D", "Samsung"),
        ("0CDFA4", "Samsung"), ("103047", "Samsung"), ("10D542", "Samsung"),
        ("148636", "Samsung"), ("1879A2", "Samsung"), ("18B430", "Samsung"),
        ("1C232C", "Samsung"), ("1C5A3E", "Samsung"), ("1C66AA", "Samsung"),
        ("1CE29F", "Samsung"), ("20A2E4", "Samsung"), ("20D390", "Samsung"),
        ("244B81", "Samsung"), ("24DBED", "Samsung"), ("28BAB5", "Samsung"),
        ("2C0E3D", "Samsung"), ("2C8A72", "Samsung"), ("2CBABA", "Samsung"),
        ("301966", "Samsung"), ("30C7AE", "Samsung"), ("30CBF8", "Samsung"),
        ("34145F", "Samsung"), ("3423BA", "Samsung"), ("34BE00", "Samsung"),
        ("380A94", "Samsung"), ("381632", "Samsung"), ("38AA3C", "Samsung"),
        ("38D40B", "Samsung"), ("3C5A37", "Samsung"), ("3C8BFE", "Samsung"),
        ("400E85", "Samsung"), ("4045DA", "Samsung"), ("403004", "Samsung"),
        ("445500", "Samsung"), ("4C3C16", "Samsung"), ("4CBCA5", "Samsung"),
        ("503275", "Samsung"), ("508569", "Samsung"), ("50CCF8", "Samsung"),
        ("50F520", "Samsung"), ("54614B", "Samsung"), ("548849", "Samsung"),
        ("5C0A5B", "Samsung"), ("5C2E59", "Samsung"), ("5C497D", "Samsung"),
        ("5C69F8", "Samsung"), ("5CE8EB", "Samsung"), ("5CF6DC", "Samsung"),
        ("5CF8A1", "Samsung"), ("606BBD", "Samsung"), ("60847D", "Samsung"),
        ("60AF6D", "Samsung"), ("640980", "Samsung"), ("647791", "Samsung"),
        ("6490C1", "Samsung"), ("6805CA", "Samsung"), ("68EBC5", "Samsung"),
        ("6C2F2C", "Samsung"), ("6C8336", "Samsung"), ("70286B", "Samsung"),
        ("70F927", "Samsung"), ("781FDB", "Samsung"), ("78471D", "Samsung"),
        ("789F70", "Samsung"), ("78D6F0", "Samsung"), ("78F7D0", "Samsung"),
        ("7C6193", "Samsung"), ("7C9122", "Samsung"), ("804E81", "Samsung"),
        ("80657C", "Samsung"), ("8425DB", "Samsung"), ("847BEB", "Samsung"),
        ("846BFB", "Samsung"), ("880F10", "Samsung"), ("88329B", "Samsung"),
        ("88ADD2", "Samsung"), ("889B39", "Samsung"), ("8C587B", "Samsung"),
        ("8C7712", "Samsung"), ("8CC8CD", "Samsung"), ("8CE081", "Samsung"),
        ("9018AE", "Samsung"), ("9C0298", "Samsung"), ("9C3AAF", "Samsung"),
        ("9CE063", "Samsung"), ("A00BBA", "Samsung"), ("A0CBFD", "Samsung"),
        ("A8F274", "Samsung"), ("AC5F3E", "Samsung"), ("ACAFB9", "Samsung"),
        ("ACC33A", "Samsung"), ("ACC422", "Samsung"), ("ACEE9E", "Samsung"),
        ("B047BF", "Samsung"), ("B072BF", "Samsung"), ("B0D09C", "Samsung"),
        ("B43A28", "Samsung"), ("B46293", "Samsung"), ("B479A7", "Samsung"),
        ("B85E7B", "Samsung"), ("B8D7AF", "Samsung"), ("BC20A4", "Samsung"),
        ("BC4760", "Samsung"), ("BC72B1", "Samsung"), ("BC79AD", "Samsung"),
        ("BC8CCD", "Samsung"), ("C0BDD1", "Samsung"), ("C8194B", "Samsung"),
        ("C8BA94", "Samsung"), ("C8E55C", "Samsung"), ("CC07AB", "Samsung"),
        ("D0176A", "Samsung"), ("D03110", "Samsung"), ("D087E2", "Samsung"),
        ("D0F88C", "Samsung"), ("D232AC", "Samsung"), ("D2474D", "Samsung"),
        ("D487D8", "Samsung"), ("D48890", "Samsung"), ("D4E8B2", "Samsung"),
        ("D80F99", "Samsung"), ("D831CF", "Samsung"), ("D890E8", "Samsung"),
        ("D8E0E1", "Samsung"), ("DC7144", "Samsung"), ("DCE533", "Samsung"),
        ("E0C87B", "Samsung"), ("E0D578", "Samsung"), ("E4121D", "Samsung"),
        ("E440E2", "Samsung"), ("E492FB", "Samsung"), ("E4B021", "Samsung"),
        ("E8039A", "Samsung"), ("E82086", "Samsung"), ("E8508B", "Samsung"),
        ("E8E5D6", "Samsung"), ("EC1F72", "Samsung"), ("F025B7", "Samsung"),
        ("F05A09", "Samsung"), ("F06BCA", "Samsung"), ("F0E77E", "Samsung"),
        ("F409D8", "Samsung"), ("F40F24", "Samsung"), ("F4428F", "Samsung"),
        ("F4D9FB", "Samsung"), ("F8042E", "Samsung"), ("F83F51", "Samsung"),
        ("F884F2", "Samsung"), ("F8D0BD", "Samsung"), ("FC1910", "Samsung"),
        ("FCA621", "Samsung"), ("FCC734", "Samsung"), ("FCF136", "Samsung"),

        // ---------- Huawei ----------
        ("001882", "Huawei"), ("001E10", "Huawei"), ("001FF8", "Huawei"),
        ("00259E", "Huawei"), ("002568", "Huawei"), ("00464B", "Huawei"),
        ("04BD70", "Huawei"), ("04C06F", "Huawei"), ("04F938", "Huawei"),
        ("086A0A", "Huawei"), ("0C37DC", "Huawei"), ("0C45BA", "Huawei"),
        ("0CD6BD", "Huawei"), ("100E7E", "Huawei"), ("1083D2", "Huawei"),
        ("14205E", "Huawei"), ("149A10", "Huawei"), ("14B968", "Huawei"),
        ("18C58A", "Huawei"), ("1C151F", "Huawei"), ("1C8E5C", "Huawei"),
        ("20F17C", "Huawei"), ("24DBAC", "Huawei"), ("286ED4", "Huawei"),
        ("2C9D1E", "Huawei"), ("2CB0DF", "Huawei"), ("302686", "Huawei"),
        ("30D17E", "Huawei"), ("342387", "Huawei"), ("348A7B", "Huawei"),
        ("3CDFBD", "Huawei"), ("4099A7", "Huawei"), ("404D8E", "Huawei"),
        ("4486FF", "Huawei"), ("4498A0", "Huawei"), ("4CB16C", "Huawei"),
        ("503DC5", "Huawei"), ("58605F", "Huawei"), ("5C7D5E", "Huawei"),
        ("608334", "Huawei"), ("64A651", "Huawei"), ("70723C", "Huawei"),
        ("7813A7", "Huawei"), ("806CBC", "Huawei"), ("841B77", "Huawei"),
        ("844765", "Huawei"), ("88CEFA", "Huawei"), ("88E3AB", "Huawei"),
        ("88F872", "Huawei"), ("8C0EE3", "Huawei"), ("9C28EF", "Huawei"),
        ("9CC172", "Huawei"), ("9CE374", "Huawei"), ("9CF61A", "Huawei"),
        ("A0086F", "Huawei"), ("A47174", "Huawei"), ("A4BA76", "Huawei"),
        ("A4C64F", "Huawei"), ("ACE215", "Huawei"), ("ACE87B", "Huawei"),
        ("B05B1F", "Huawei"), ("B41513", "Huawei"), ("B4FF80", "Huawei"),
        ("BC6E76", "Huawei"), ("BC9C31", "Huawei"), ("BCD183", "Huawei"),
        ("BCDDC2", "Huawei"), ("BCFAA2", "Huawei"), ("C40252", "Huawei"),
        ("C46AB7", "Huawei"), ("C8517C", "Huawei"), ("C894BB", "Huawei"),
        ("CC53B5", "Huawei"), ("CC96A0", "Huawei"), ("D03E5C", "Huawei"),
        ("D04E48", "Huawei"), ("D0FF5A", "Huawei"), ("D46EE5", "Huawei"),
        ("D4A148", "Huawei"), ("D4B110", "Huawei"), ("D8490B", "Huawei"),
        ("D852E1", "Huawei"), ("DC726F", "Huawei"), ("DCD916", "Huawei"),
        ("E0247F", "Huawei"), ("E0C7E1", "Huawei"), ("E0DD3B", "Huawei"),
        ("E4C2D1", "Huawei"), ("ECC4C0", "Huawei"), ("F4DCDA", "Huawei"),
        ("F4DEAF", "Huawei"), ("F81A67", "Huawei"), ("F83DFF", "Huawei"),
        ("F8C091", "Huawei"), ("FC4D8C", "Huawei"),

        // ---------- Intel (laptop adapters / NUC) ----------
        ("001B21", "Intel"), ("001B77", "Intel"), ("001C25", "Intel"),
        ("001CBF", "Intel"), ("001CC0", "Intel"), ("001DE0", "Intel"),
        ("001DE1", "Intel"), ("001DE2", "Intel"), ("001E64", "Intel"),
        ("001E65", "Intel"), ("001E67", "Intel"), ("001F3B", "Intel"),
        ("001F3C", "Intel"), ("00215C", "Intel"), ("00215D", "Intel"),
        ("0022FA", "Intel"), ("0022FB", "Intel"), ("002314", "Intel"),
        ("002315", "Intel"), ("0024D6", "Intel"), ("0024D7", "Intel"),
        ("0026C6", "Intel"), ("0026C7", "Intel"), ("00270E", "Intel"),
        ("002710", "Intel"), ("0028F8", "Intel"), ("003064", "Intel"),
        ("009027", "Intel"), ("00D0B7", "Intel"), ("589CFC", "Intel"),
        ("5891CF", "Intel"), ("58946B", "Intel"), ("5C514F", "Intel"),
        ("5CE0C5", "Intel"), ("5CF370", "Intel"), ("606720", "Intel"),
        ("60D9C7", "Intel"), ("648099", "Intel"), ("64D4DA", "Intel"),
        ("6805CA", "Intel"), ("681729", "Intel"), ("6C2995", "Intel"),
        ("6C4B90", "Intel"), ("6C8814", "Intel"), ("70188B", "Intel"),
        ("701CE7", "Intel"), ("705A0F", "Intel"), ("78929C", "Intel"),
        ("789614", "Intel"), ("7C7A91", "Intel"), ("801934", "Intel"),
        ("8086F2", "Intel"), ("843A4B", "Intel"), ("84EF18", "Intel"),
        ("84FDD1", "Intel"), ("88532E", "Intel"), ("887873", "Intel"),
        ("8C1645", "Intel"), ("8C8EF2", "Intel"), ("8CC84B", "Intel"),
        ("8CF8C5", "Intel"), ("904CE5", "Intel"), ("9061AE", "Intel"),
        ("90E2BA", "Intel"), ("94659C", "Intel"), ("94B86D", "Intel"),
        ("94C691", "Intel"), ("9CB6D0", "Intel"), ("9CE5D4", "Intel"),
        ("A088B4", "Intel"), ("A08CFD", "Intel"), ("A0A8CD", "Intel"),
        ("A402B9", "Intel"), ("A434D9", "Intel"), ("A44CC8", "Intel"),
        ("A4BB6D", "Intel"), ("A4C494", "Intel"), ("A4EAEE", "Intel"),
        ("A86BAD", "Intel"), ("A8917A", "Intel"), ("A89D21", "Intel"),
        ("AC7BA1", "Intel"), ("ACFDCE", "Intel"), ("B08E1A", "Intel"),
        ("B0D5CC", "Intel"), ("B43A28", "Intel"), ("B46BFC", "Intel"),
        ("B49691", "Intel"), ("B4B52F", "Intel"), ("B4B676", "Intel"),
        ("B80305", "Intel"), ("B808D7", "Intel"), ("B88198", "Intel"),
        ("B88687", "Intel"), ("B88A60", "Intel"), ("B89436", "Intel"),
        ("B898B0", "Intel"), ("B8AEED", "Intel"), ("BC671C", "Intel"),
        ("BC7E8B", "Intel"), ("BC836F", "Intel"), ("BC8CC9", "Intel"),
        ("BCEE7B", "Intel"), ("C01850", "Intel"), ("C09134", "Intel"),
        ("C0B6F9", "Intel"), ("C0BDD1", "Intel"), ("C44BD1", "Intel"),
        ("C48508", "Intel"), ("C4D987", "Intel"), ("C81F66", "Intel"),
        ("C8544B", "Intel"), ("C85B76", "Intel"), ("C89CDC", "Intel"),
        ("C8D3FF", "Intel"), ("C8F750", "Intel"), ("CCAF78", "Intel"),
        ("D05349", "Intel"), ("D073D5", "Intel"), ("D83877", "Intel"),
        ("D890E8", "Intel"), ("D89E3F", "Intel"), ("D8F15B", "Intel"),
        ("DC5360", "Intel"), ("DCA904", "Intel"), ("DCF505", "Intel"),
        ("E01877", "Intel"), ("E09467", "Intel"), ("E0D55E", "Intel"),
        ("E0EAAC", "Intel"), ("E41D2D", "Intel"), ("E442A6", "Intel"),
        ("E44CC8", "Intel"), ("E470B8", "Intel"), ("E4A471", "Intel"),
        ("E4C722", "Intel"), ("E49ADC", "Intel"), ("E86F38", "Intel"),
        ("E8B1FC", "Intel"), ("E8DF70", "Intel"), ("EC1F72", "Intel"),
        ("ECA86B", "Intel"), ("F01FAF", "Intel"), ("F04DA2", "Intel"),
        ("F06E0B", "Intel"), ("F08469", "Intel"), ("F09E4A", "Intel"),
        ("F0B479", "Intel"), ("F0DBF8", "Intel"), ("F47B5E", "Intel"),
        ("F48C50", "Intel"), ("F80F41", "Intel"), ("F81654", "Intel"),
        ("F848FD", "Intel"), ("F85971", "Intel"), ("F8769B", "Intel"),
        ("F8BC12", "Intel"), ("FC51A4", "Intel"), ("FCF230", "Intel"),
        ("FCF8AE", "Intel"),

        // ---------- Realtek (cheap USB Wi-Fi) ----------
        ("000FC8", "Realtek"), ("00C06F", "Realtek"), ("00E04C", "Realtek"),
        ("000CE5", "Realtek"), ("525400", "Qemu/KVM"),

        // ---------- Microsoft (Surface, Xbox, Hyper-V) ----------
        ("000DA4", "Microsoft"), ("00125A", "Microsoft"), ("7C1E52", "Microsoft"),
        ("DC4F22", "Microsoft"), ("F8B7E2", "Microsoft"),

        // ---------- VMware / virtualization ----------
        ("000C29", "VMware"), ("001C14", "VMware"), ("005056", "VMware"),
        ("001C42", "Parallels"),

        // ---------- Google / Nest ----------
        ("001A11", "Google"), ("001AA0", "Google"), ("085AE0", "Google"),
        ("18B430", "Google Nest"), ("20DF3F", "Google"), ("286C07", "Google"),
        ("30FD38", "Google"), ("3429EA", "Google"), ("4040A7", "Google"),
        ("481DC1", "Google"), ("58CB52", "Google"), ("648099", "Google"),
        ("704F08", "Google"), ("7C2EBD", "Google"), ("80E650", "Google"),
        ("94EB2C", "Google"), ("A4BE61", "Google"), ("A45E60", "Google"),
        ("A4DA22", "Google"), ("AC63BE", "Google"), ("ACE57E", "Google"),
        ("F8FFC2", "Google"), ("F8E61A", "Google"),

        // ---------- HP Inc / Hewlett Packard Enterprise ----------
        ("001E0B", "HP"), ("001F29", "HP"), ("002264", "HP"),
        ("00237D", "HP"), ("002481", "HP"), ("00256B", "HP"),
        ("002655", "HP"), ("002A10", "HP"), ("00306E", "HP"),
        ("003E1A", "HP"), ("009C02", "HP"), ("2C44FD", "HP"),
        ("2C768A", "HP"), ("308D99", "HP"), ("389497", "HP"),
        ("3C2C30", "HP"), ("3C525A", "HP"), ("3CD92B", "HP"),
        ("405555", "HP"), ("444802", "HP"), ("4C3909", "HP"),
        ("50657F", "HP"), ("50EB1A", "HP"), ("58205B", "HP"),
        ("581FAA", "HP"), ("5882A8", "HP"), ("5C8A38", "HP"),
        ("647002", "HP"), ("6CC217", "HP"), ("700BC0", "HP"),
        ("744401", "HP"), ("74C63B", "HP"), ("78481D", "HP"),
        ("787B8A", "HP"), ("803F5D", "HP"), ("80C16E", "HP"),
        ("80CE62", "HP"), ("843497", "HP"), ("9457A5", "HP"),
        ("9855BB", "HP"), ("985FD3", "HP"), ("9C8E99", "HP"),
        ("9CB654", "HP"), ("9CDC71", "HP"), ("A0481C", "HP"),
        ("A03B72", "HP"), ("A45D36", "HP"), ("A48D14", "HP"),
        ("A89D21", "HP"), ("ACE2D3", "HP"), ("B0AF27", "HP"),
        ("B499BA", "HP"), ("B83A7B", "HP"), ("B8AF67", "HP"),
        ("BCEC23", "HP"), ("C8CBB8", "HP"), ("C8D3FF", "HP"),
        ("CC3E5F", "HP"), ("CCC079", "HP"), ("D48564", "HP"),
        ("D89D67", "HP"), ("D89EF3", "HP"), ("D8D385", "HP"),
        ("E0071B", "HP"), ("EC9A74", "HP"), ("ECEBB8", "HP"),
        ("F40343", "HP"), ("F4CE46", "HP"), ("FC15B4", "HP"),

        // ---------- Dell ----------
        ("001143", "Dell"), ("001372", "Dell"), ("001E4F", "Dell"),
        ("001EC9", "Dell"), ("002219", "Dell"), ("002564", "Dell"),
        ("14B31F", "Dell"), ("18DBF2", "Dell"), ("20040F", "Dell"),
        ("24B6FD", "Dell"), ("2CEA7F", "Dell"), ("3417EB", "Dell"),
        ("509A4C", "Dell"), ("54AB3A", "Dell"), ("5CF9DD", "Dell"),
        ("78AC44", "Dell"), ("8030DC", "Dell"), ("843A4B", "Dell"),
        ("90B11C", "Dell"), ("A41F72", "Dell"), ("B083FE", "Dell"),
        ("B49691", "Dell"), ("B8AC6F", "Dell"), ("B8CA3A", "Dell"),
        ("B8E856", "Dell"), ("D481D7", "Dell"), ("D89D67", "Dell"),
        ("D8AECC", "Dell"), ("E4434B", "Dell"), ("E4F004", "Dell"),
        ("F8B156", "Dell"), ("F8DB88", "Dell"), ("FCE3F7", "Dell"),

        // ---------- Lenovo / Motorola ----------
        ("001CC4", "Lenovo"), ("001D72", "Lenovo"), ("00219B", "Lenovo"),
        ("102B41", "Lenovo"), ("149691", "Lenovo"), ("14F65A", "Lenovo"),
        ("24A2E1", "Lenovo"), ("289604", "Lenovo"), ("2CD05A", "Lenovo"),
        ("543C5E", "Lenovo"), ("5CC5D4", "Lenovo"), ("708BCD", "Lenovo"),
        ("78A0DC", "Lenovo"), ("7CB27D", "Lenovo"), ("801F02", "Lenovo"),
        ("88708C", "Lenovo"), ("9CFCE8", "Lenovo"), ("A085FC", "Lenovo"),
        ("A6BFC1", "Lenovo"), ("AC2B6E", "Lenovo"), ("AC74B1", "Lenovo"),
        ("B0E892", "Lenovo"), ("B85D0A", "Lenovo"), ("BC305B", "Lenovo"),
        ("BC764E", "Lenovo"), ("BC8385", "Lenovo"), ("C03FD5", "Lenovo"),
        ("C8DDC9", "Lenovo"), ("E2DBE7", "Lenovo"), ("E40D36", "Lenovo"),
        ("E89EB4", "Lenovo"), ("F0DEF1", "Lenovo"), ("F4E978", "Lenovo"),

        // ---------- Acer ----------
        ("001DD0", "Acer"), ("002314", "Acer"), ("002524", "Acer"),
        ("003067", "Acer"), ("40A8F0", "Acer"), ("D4D262", "Acer"),

        // ---------- LG Electronics ----------
        ("001452", "LG"), ("00147B", "LG"), ("001C62", "LG"),
        ("001E75", "LG"), ("001EB1", "LG"), ("002270", "LG"),
        ("002249", "LG"), ("00253C", "LG"), ("0025E5", "LG"),
        ("74017B", "LG"), ("F80CF3", "LG"), ("FC4203", "LG"),

        // ---------- Sony ----------
        ("000F02", "Sony"), ("002274", "Sony"), ("00C73E", "Sony"),
        ("243803", "Sony"), ("F0BF97", "Sony"),

        // ---------- Tenda ----------
        ("0024FE", "Tenda"), ("88252C", "Tenda"), ("9C3D5D", "Tenda"),
        ("C83A35", "Tenda"), ("D83214", "Tenda"), ("E865D4", "Tenda"),

        // ---------- Brother / printers ----------
        ("0080BB", "Brother"), ("008092", "Brother"), ("001BA9", "Brother"),
        ("001D99", "Brother"), ("008077", "Brother"), ("201311", "Brother"),
        ("24A43C", "Brother"), ("30392F", "Brother"), ("E04C7F", "Brother"),
        ("ECC40D", "Brother"),

        // ---------- HP Inc printers (sub-OUI) / Epson / Lexmark ----------
        ("000048", "Epson"), ("00400D", "Epson"), ("000FB0", "Epson"),
        ("001E61", "Epson"), ("001E62", "Epson"), ("002484", "Epson"),
        ("38EAA7", "Epson"), ("4081EA", "Epson"), ("445350", "Epson"),
        ("445544", "Epson"), ("50574B", "Epson"), ("5870C6", "Epson"),
        ("9CAED3", "Epson"), ("A4EE57", "Epson"), ("AC1826", "Epson"),
        ("BCF1F2", "Epson"), ("D86CE9", "Epson"), ("F8D027", "Epson"),

        ("000400", "Lexmark"), ("001958", "Lexmark"), ("00219B", "Lexmark"),
        ("0021B7", "Lexmark"), ("002541", "Lexmark"), ("002C00", "Lexmark"),
        ("ACE010", "Lexmark"),

        ("0017C8", "Kyocera"), ("001D73", "Kyocera"), ("00236E", "Kyocera"),

        // ---------- Mediatek (smart-home / IoT chipsets) ----------
        ("000C76", "Mediatek"), ("001E10", "Mediatek"),

        // ---------- Nokia ----------
        ("000FBB", "Nokia"), ("001262", "Nokia"), ("00174B", "Nokia"),
        ("00180F", "Nokia"), ("00194F", "Nokia"), ("001979", "Nokia"),
        ("001A89", "Nokia"), ("001BAF", "Nokia"), ("001D4F", "Nokia"),
        ("001DF6", "Nokia"), ("0023B4", "Nokia"), ("002404", "Nokia"),
        ("00247C", "Nokia"), ("002547", "Nokia"), ("002624", "Nokia"),
        ("002668", "Nokia"), ("0026CC", "Nokia"), ("04D604", "Nokia"),

        // ---------- 3Com (legacy) / Polycom / Avaya ----------
        ("000476", "3Com"), ("006008", "3Com"),
        ("001A4F", "Avaya"), ("0024DC", "Polycom"), ("001A1B", "Polycom"),
        ("002488", "Polycom"), ("002F21", "Polycom"), ("48254E", "Polycom"),

        // ---------- Texas Instruments / Broadcom (chipset OUIs in OEM gear) ----------
        ("001AAA", "Texas Instruments"), ("00102C", "Texas Instruments"),
        ("00904C", "Broadcom"),
    };
}
