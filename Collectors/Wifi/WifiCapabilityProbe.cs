using System.Text.Json.Serialization;
using NetScopeDiagnosticCenter.Core.Models.Wifi;
using NetScopeDiagnosticCenter.Infrastructure;
using NetScopeDiagnosticCenter.Infrastructure.Wlan;

namespace NetScopeDiagnosticCenter.Collectors.Wifi;

// JsonCollectorBase lives in the parent namespace (NetScopeDiagnosticCenter.Collectors).

/// <summary>
/// One-shot probe that queries the Wi-Fi adapter for its capabilities (Section 7 of UI).
///
/// <para>
/// Sources combined into one <see cref="WifiAdapterCapabilities"/>:
/// <list type="bullet">
///   <item>PowerShell <c>Get-NetAdapter</c> — adapter description, MAC, driver name + version,
///         interface description (used to match the WLAN adapter GUID).</item>
///   <item>WLAN API — currently associated PHY type (gives a reliable lower bound on what
///         the adapter SUPPORTS — if it negotiated 802.11ax with the AP, it supports ax).</item>
///   <item>Inferred bands — from the connected band + driver name. We can't fully enumerate
///         supported bands without NDIS OIDs which require admin; this is a best-effort guess.</item>
/// </list>
/// </para>
///
/// <para>
/// Why not WlanQueryInterface(supported_country_or_region_string_list) etc.: that returns
/// regulatory data, not feature data. Real PHY-set probing requires NDIS_OID_DOT11_*
/// queries which are blocked outside SYSTEM context. We accept the limitation: helpdesk
/// almost always cares about "what did this connection negotiate" not "what could this
/// adapter theoretically do".
/// </para>
/// </summary>
public sealed class WifiCapabilityProbe : JsonCollectorBase
{
    private readonly IWifiScanner _scanner;
    private readonly TimeProvider _time;

    public WifiCapabilityProbe(PowerShellRunner powerShell, IWifiScanner scanner, TimeProvider? time = null)
        : base(powerShell)
    {
        _scanner = scanner ?? throw new ArgumentNullException(nameof(scanner));
        _time = time ?? TimeProvider.System;
    }

    public async Task<WifiAdapterCapabilities> ProbeAsync(Guid adapterId, string adapterDescription, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Pull adapter metadata via PowerShell. The script intentionally returns ALL Wi-Fi
        // adapters because matching by GUID is unreliable (Get-NetAdapter exposes
        // InterfaceGuid only on newer Windows). We match by description string instead.
        const string script = """
$ErrorActionPreference = 'Continue'
$adapters = @(Get-NetAdapter -Physical | Where-Object {
    $_.MediaType -eq 'Native 802.11' -or $_.PhysicalMediaType -eq 'Native 802.11' -or $_.InterfaceDescription -match 'Wi-?Fi|Wireless|802\.11'
})
$results = foreach ($a in $adapters) {
    [pscustomobject]@{
        Name = [string]$a.Name
        Description = [string]$a.InterfaceDescription
        MacAddress = [string]$a.MacAddress
        DriverVersion = [string]$a.DriverVersion
        DriverProvider = [string]$a.DriverProvider
        Status = [string]$a.Status
    }
}
# Always emit an array — defensive against the single-object JSON serialization quirk.
,@($results) | ConvertTo-Json -Depth 3
""";

        AdapterRow[] rows;
        try
        {
            var data = await RunCollectorAsync<AdapterRow[]>(script, TimeSpan.FromSeconds(10), cancellationToken)
                .ConfigureAwait(false);
            rows = data ?? Array.Empty<AdapterRow>();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            rows = Array.Empty<AdapterRow>();
        }

        // BCL gives us a 100%-reliable MAC + real adapter description with zero PowerShell
        // dependency. The caller passes the literal "Wi-Fi" string which never matched a
        // real driver row ("Intel(R) Wi-Fi 6 AX201 160MHz") — that's why MAC/Driver showed
        // "—". We now match the PS driver row against the BCL description instead.
        var bcl = Core.Wifi.WifiAdapterInfo.GetActiveWifi(adapterId);
        var realDescription = !string.IsNullOrEmpty(bcl.Description) ? bcl.Description! : adapterDescription;

        var match = rows.FirstOrDefault(r =>
            !string.IsNullOrEmpty(r.Description) &&
            !string.IsNullOrEmpty(realDescription) &&
            (r.Description.Contains(realDescription, StringComparison.OrdinalIgnoreCase) ||
             realDescription.Contains(r.Description, StringComparison.OrdinalIgnoreCase)));
        match ??= rows.FirstOrDefault();

        // Probe the connected PHY for a reliable "supports at least this" floor.
        var connection = _scanner.GetCurrentConnection(adapterId);
        var connectedPhy = connection?.PhyType ?? Dot11PhyType.Unknown;
        var connectedBand = connection?.Band ?? WifiBand.Unknown;

        var supportedPhys = DerivePhyTypes(connectedPhy, realDescription);
        var supportedBands = DeriveBands(supportedPhys, connectedBand);
        var technologies = DeriveTechnologies(supportedPhys);

        return new WifiAdapterCapabilities(
            InterfaceGuid: adapterId,
            Description: realDescription,
            // BCL MAC is authoritative; PS row is a fallback only.
            PermanentMacAddress: bcl.MacAddress ?? NormalizeMac(match?.MacAddress),
            DriverName: match?.DriverProvider,
            DriverVersion: match?.DriverVersion,
            CountryCode: null,                          // requires WlanQueryInterface country opcode (separate enum)
            SupportedBands: supportedBands,
            SupportedPhyTypes: supportedPhys,
            SupportedTechnologies: technologies,
            SupportsWpa3: supportedPhys.Contains(Dot11PhyType.He) || supportedPhys.Contains(Dot11PhyType.Eht),
            SupportsWifi6E: supportedBands.Contains(WifiBand.SixGhz),
            SupportsWifi7: supportedPhys.Contains(Dot11PhyType.Eht),
            CapturedAt: _time.GetUtcNow().ToLocalTime());
    }

    /// <summary>
    /// Derive the list of supported PHY generations. Floor = the PHY currently negotiated
    /// (an adapter that connects at ax must support ax). Augment from driver name when
    /// it embeds a marketing tier ("AX201", "BE200", "AC9560").
    /// </summary>
    private static IReadOnlyList<Dot11PhyType> DerivePhyTypes(Dot11PhyType connected, string? description)
    {
        var set = new HashSet<Dot11PhyType>();

        // Backward compatibility: any adapter supporting newer PHY also supports older.
        if (connected == Dot11PhyType.Eht)
        {
            set.Add(Dot11PhyType.Eht); set.Add(Dot11PhyType.He);
            set.Add(Dot11PhyType.Vht); set.Add(Dot11PhyType.Ht);
            set.Add(Dot11PhyType.Erp); set.Add(Dot11PhyType.Ofdm);
        }
        else if (connected == Dot11PhyType.He)
        {
            set.Add(Dot11PhyType.He); set.Add(Dot11PhyType.Vht); set.Add(Dot11PhyType.Ht);
            set.Add(Dot11PhyType.Erp); set.Add(Dot11PhyType.Ofdm);
        }
        else if (connected == Dot11PhyType.Vht)
        {
            set.Add(Dot11PhyType.Vht); set.Add(Dot11PhyType.Ht);
            set.Add(Dot11PhyType.Erp); set.Add(Dot11PhyType.Ofdm);
        }
        else if (connected == Dot11PhyType.Ht)
        {
            set.Add(Dot11PhyType.Ht); set.Add(Dot11PhyType.Erp); set.Add(Dot11PhyType.Ofdm);
        }

        // Heuristic from driver description: "AX201" → ax; "BE200" → be; etc.
        if (!string.IsNullOrEmpty(description))
        {
            if (description.Contains("BE", StringComparison.OrdinalIgnoreCase) &&
                System.Text.RegularExpressions.Regex.IsMatch(description, @"\bBE\s?\d{3}", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            {
                set.Add(Dot11PhyType.Eht);
            }
            if (description.Contains("AX", StringComparison.OrdinalIgnoreCase) &&
                System.Text.RegularExpressions.Regex.IsMatch(description, @"\bAX\s?\d{3}", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            {
                set.Add(Dot11PhyType.He);
            }
            if (description.Contains("AC", StringComparison.OrdinalIgnoreCase) &&
                System.Text.RegularExpressions.Regex.IsMatch(description, @"\bAC\s?\d{4}", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            {
                set.Add(Dot11PhyType.Vht);
            }
        }

        // Order by recency for friendly display.
        var ordered = new List<Dot11PhyType>();
        foreach (var phy in new[] { Dot11PhyType.Eht, Dot11PhyType.He, Dot11PhyType.Vht, Dot11PhyType.Ht, Dot11PhyType.Erp, Dot11PhyType.Ofdm })
        {
            if (set.Contains(phy)) ordered.Add(phy);
        }
        return ordered;
    }

    private static IReadOnlyList<WifiBand> DeriveBands(IReadOnlyList<Dot11PhyType> phys, WifiBand connectedBand)
    {
        var set = new HashSet<WifiBand>();

        // ERP / Ht imply 2.4 GHz; Vht implies 5 GHz; He implies 5 + 2.4; Eht implies 6 + 5 + 2.4.
        if (phys.Contains(Dot11PhyType.Erp) || phys.Contains(Dot11PhyType.Ht)) set.Add(WifiBand.TwoPointFourGhz);
        if (phys.Contains(Dot11PhyType.Vht) || phys.Contains(Dot11PhyType.Ofdm)) set.Add(WifiBand.FiveGhz);
        if (phys.Contains(Dot11PhyType.He))
        {
            set.Add(WifiBand.TwoPointFourGhz);
            set.Add(WifiBand.FiveGhz);
            // 6 GHz support is conditional on driver — only add when we have evidence the
            // adapter actually used 6 GHz (current connection is on 6 GHz).
            if (connectedBand == WifiBand.SixGhz) set.Add(WifiBand.SixGhz);
        }
        if (phys.Contains(Dot11PhyType.Eht))
        {
            set.Add(WifiBand.TwoPointFourGhz);
            set.Add(WifiBand.FiveGhz);
            set.Add(WifiBand.SixGhz);  // EHT mandates 6 GHz support
        }

        var ordered = new List<WifiBand>();
        foreach (var b in new[] { WifiBand.TwoPointFourGhz, WifiBand.FiveGhz, WifiBand.SixGhz })
        {
            if (set.Contains(b)) ordered.Add(b);
        }
        return ordered;
    }

    /// <summary>
    /// Map PHY set to advertised technologies for the UI. These are de-facto required by each
    /// PHY (per IEEE 802.11 spec):
    ///   ax (Wi-Fi 6) → MIMO, MU-MIMO, OFDMA, BSS Coloring, Target Wake Time
    ///   be (Wi-Fi 7) → all of the above + Multi-Link Operation, 4K-QAM, 320 MHz Channels
    ///   ac (Wi-Fi 5) → MIMO, MU-MIMO (downlink), Beamforming
    ///   n  (Wi-Fi 4) → MIMO, Beamforming (optional)
    /// </summary>
    private static IReadOnlyList<string> DeriveTechnologies(IReadOnlyList<Dot11PhyType> phys)
    {
        var set = new HashSet<string>();
        if (phys.Contains(Dot11PhyType.Ht))
        {
            set.Add("MIMO");
            set.Add("Beamforming");
        }
        if (phys.Contains(Dot11PhyType.Vht))
        {
            set.Add("MU-MIMO (downlink)");
        }
        if (phys.Contains(Dot11PhyType.He))
        {
            set.Add("MU-MIMO (uplink + downlink)");
            set.Add("OFDMA");
            set.Add("BSS Coloring");
            set.Add("Target Wake Time");
            set.Add("1024-QAM");
        }
        if (phys.Contains(Dot11PhyType.Eht))
        {
            set.Add("Multi-Link Operation (MLO)");
            set.Add("4K-QAM");
            set.Add("320 MHz Channels");
        }
        return set.OrderBy(s => s).ToArray();
    }

    private static string? NormalizeMac(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        // Get-NetAdapter emits "AA-BB-CC-DD-EE-FF"; we normalize to colon-separated upper.
        return raw.Replace('-', ':').ToUpperInvariant();
    }

    private sealed class AdapterRow
    {
        [JsonPropertyName("Name")] public string? Name { get; set; }
        [JsonPropertyName("Description")] public string? Description { get; set; }
        [JsonPropertyName("MacAddress")] public string? MacAddress { get; set; }
        [JsonPropertyName("DriverVersion")] public string? DriverVersion { get; set; }
        [JsonPropertyName("DriverProvider")] public string? DriverProvider { get; set; }
        [JsonPropertyName("Status")] public string? Status { get; set; }
    }
}
