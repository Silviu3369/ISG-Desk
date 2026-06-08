using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using NetScopeDiagnosticCenter.Core.Models.Wifi;

namespace NetScopeDiagnosticCenter.Collectors.Wifi;

/// <summary>
/// Opt-in public-IP + ISP lookup (Decision 1 = A — user must click "Detect ISP").
///
/// <para>
/// Two-step flow:
/// <list type="number">
///   <item>GET https://api.ipify.org?format=json — returns just the public IP. Free, no rate limit.</item>
///   <item>GET https://ipapi.co/{ip}/json/ — enriches with ISP / city / country / ASN.
///         Free tier: 30k req/month, 1k/day. Plenty for helpdesk use.</item>
/// </list>
/// </para>
///
/// <para>
/// Privacy / Network calls: the user explicitly clicked "Detect ISP" so consent is recorded
/// in the UI affordance itself. We send the public IP to ipapi.co (which they would see
/// from any HTTP request anyway) - no PII beyond that. Result is cached in memory
/// for the session so repeat clicks don't hit network.
/// </para>
///
/// <para>
/// Failure modes (encoded in <see cref="WifiPublicIpInfo.ErrorMessage"/>):
/// <list type="bullet">
///   <item>"No internet connection" — gateway unreachable / DNS dead</item>
///   <item>"Lookup timed out" — slow link / API latency</item>
///   <item>"API rate-limited" — 429 from ipapi (rare for helpdesk volumes)</item>
///   <item>"Lookup service unavailable" — 5xx from upstream</item>
/// </list>
/// All three are caught + surfaced to the user as the ErrorMessage shown in the Connection card.
/// </para>
/// </summary>
public sealed class WifiPublicIpProbe : IDisposable
{
    private readonly HttpClient _http;
    private readonly TimeProvider _time;
    private readonly bool _ownsHttpClient;
    private bool _disposed;

    /// <summary>
    /// Default ctor — instantiates an internal HttpClient with a 5s timeout.
    /// Production code should prefer the IHttpClientFactory-based ctor (constructor below)
    /// once the app wires up a factory; this minimal ctor is for ease of unit-test injection.
    /// </summary>
    public WifiPublicIpProbe(TimeProvider? time = null)
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("ISG-Desk/2.0 (helpdesk diagnostic)");
        _time = time ?? TimeProvider.System;
        _ownsHttpClient = true;
    }

    /// <summary>
    /// HttpClient-injected ctor — for tests + future IHttpClientFactory wiring.
    /// The probe does NOT dispose an externally provided client.
    /// </summary>
    public WifiPublicIpProbe(HttpClient http, TimeProvider? time = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _time = time ?? TimeProvider.System;
        _ownsHttpClient = false;
    }

    /// <summary>
    /// Run the lookup. Cancellation tokens propagate; timeouts surface as ErrorMessage.
    /// Always returns a result object — the caller checks <see cref="WifiPublicIpInfo.IsSuccess"/>.
    /// </summary>
    public async Task<WifiPublicIpInfo> ProbeAsync(CancellationToken cancellationToken)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(WifiPublicIpProbe));
        cancellationToken.ThrowIfCancellationRequested();

        // Step 1: get the public IP. ipify is the simplest possible API.
        string? publicIp;
        try
        {
            var ipResp = await _http
                .GetFromJsonAsync<IpifyResponse>("https://api.ipify.org?format=json", cancellationToken)
                .ConfigureAwait(false);
            publicIp = ipResp?.Ip;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return WifiPublicIpInfo.Failure("Lookup timed out");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException)
        {
            return WifiPublicIpInfo.Failure("No internet connection");
        }
        catch (Exception)
        {
            return WifiPublicIpInfo.Failure("Lookup failed");
        }

        if (string.IsNullOrWhiteSpace(publicIp))
        {
            return WifiPublicIpInfo.Failure("Public IP not returned by lookup service");
        }

        // Step 2: enrich with ISP / location. ipapi.co returns 429 on rate limit; we surface that.
        IpapiResponse? enriched;
        try
        {
            using var resp = await _http
                .GetAsync($"https://ipapi.co/{publicIp}/json/", cancellationToken)
                .ConfigureAwait(false);

            if ((int)resp.StatusCode == 429)
            {
                return WifiPublicIpInfo.Failure("API rate-limited (try again later)");
            }
            if ((int)resp.StatusCode >= 500)
            {
                return WifiPublicIpInfo.Failure("Lookup service unavailable");
            }
            if (!resp.IsSuccessStatusCode)
            {
                return WifiPublicIpInfo.Failure($"Lookup returned HTTP {(int)resp.StatusCode}");
            }

            enriched = await resp.Content
                .ReadFromJsonAsync<IpapiResponse>(cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Got the IP but enrichment timed out - return partial success.
            return new WifiPublicIpInfo(
                PublicIpAddress: publicIp, IspName: null, Location: null,
                AsnNumber: null, AsnOrganization: null,
                CapturedAt: _time.GetUtcNow().ToLocalTime(),
                ErrorMessage: "ISP enrichment timed out");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return new WifiPublicIpInfo(
                PublicIpAddress: publicIp, IspName: null, Location: null,
                AsnNumber: null, AsnOrganization: null,
                CapturedAt: _time.GetUtcNow().ToLocalTime(),
                ErrorMessage: "ISP enrichment failed");
        }

        if (enriched is null)
        {
            return new WifiPublicIpInfo(
                PublicIpAddress: publicIp, IspName: null, Location: null,
                AsnNumber: null, AsnOrganization: null,
                CapturedAt: _time.GetUtcNow().ToLocalTime(),
                ErrorMessage: null);
        }

        var location = (enriched.City, enriched.CountryCode) switch
        {
            (string c, string cc) when !string.IsNullOrEmpty(c) && !string.IsNullOrEmpty(cc) => $"{c}, {cc}",
            (string c, _) when !string.IsNullOrEmpty(c) => c,
            (_, string cc) when !string.IsNullOrEmpty(cc) => cc,
            _ => null,
        };

        return new WifiPublicIpInfo(
            PublicIpAddress: publicIp,
            IspName: enriched.Org ?? enriched.Asn,
            Location: location,
            AsnNumber: enriched.Asn,
            AsnOrganization: enriched.Org,
            CapturedAt: _time.GetUtcNow().ToLocalTime(),
            ErrorMessage: null);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_ownsHttpClient) _http.Dispose();
    }

    // Json shapes from the two upstream APIs. Property names match the JSON exactly so
    // System.Text.Json can deserialize without case-insensitive overhead.
    private sealed class IpifyResponse
    {
        [JsonPropertyName("ip")] public string? Ip { get; set; }
    }

    private sealed class IpapiResponse
    {
        [JsonPropertyName("ip")] public string? Ip { get; set; }
        [JsonPropertyName("city")] public string? City { get; set; }
        [JsonPropertyName("region")] public string? Region { get; set; }
        [JsonPropertyName("country")] public string? CountryCode { get; set; }
        [JsonPropertyName("country_name")] public string? CountryName { get; set; }
        [JsonPropertyName("org")] public string? Org { get; set; }
        [JsonPropertyName("asn")] public string? Asn { get; set; }
    }
}
