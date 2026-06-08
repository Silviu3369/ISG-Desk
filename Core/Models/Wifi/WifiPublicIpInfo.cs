namespace NetScopeDiagnosticCenter.Core.Models.Wifi;

/// <summary>
/// Result of the opt-in public-IP / ISP lookup (Decision 1 = A).
///
/// Returned by <c>WifiPublicIpProbe</c> after the user clicks "Detect ISP" in the
/// Connection card. The lookup uses two public APIs:
/// <list type="number">
///   <item><c>https://api.ipify.org?format=json</c> — returns just the public IP</item>
///   <item><c>https://ipapi.co/{ip}/json/</c> — enriches with ISP / city / country</item>
/// </list>
///
/// Both are rate-limited but free for low volumes. The UI caches the result in memory
/// for the session so repeat clicks don't hit the network.
///
/// <para>Failure modes are surfaced via <see cref="ErrorMessage"/>:</para>
/// <list type="bullet">
///   <item>"No internet connection" — gateway unreachable / DNS dead</item>
///   <item>"Lookup timed out" — slow link / API down</item>
///   <item>"API rate-limited" — 429 from ipapi.co</item>
/// </list>
/// </summary>
public sealed record WifiPublicIpInfo(
    string? PublicIpAddress,
    string? IspName,

    /// <summary>"Bucharest, RO" — combination of city + country code.</summary>
    string? Location,

    string? AsnNumber,
    string? AsnOrganization,
    DateTimeOffset CapturedAt,
    string? ErrorMessage)
{
    public bool IsSuccess => ErrorMessage is null && PublicIpAddress is not null;

    public static WifiPublicIpInfo Failure(string error) => new(
        PublicIpAddress: null, IspName: null, Location: null,
        AsnNumber: null, AsnOrganization: null,
        CapturedAt: DateTimeOffset.Now,
        ErrorMessage: error);
}
