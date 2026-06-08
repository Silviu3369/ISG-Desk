using System.IO;
using NetScopeDiagnosticCenter.Core.Models;
using NetScopeDiagnosticCenter.Core.Models.Wifi;
using NetScopeDiagnosticCenter.Infrastructure;
using NetScopeDiagnosticCenter.Infrastructure.Wlan;

namespace NetScopeDiagnosticCenter.Collectors;

public sealed class WifiCollector : JsonCollectorBase
{
    private readonly IWlanApi? _wlan;

    // IWlanApi is optional so existing `new WifiCollector(psRunner)` call sites (tests)
    // keep compiling; the DI container injects the registered singleton in production.
    public WifiCollector(PowerShellRunner powerShellRunner, IWlanApi? wlan = null) : base(powerShellRunner)
    {
        _wlan = wlan;
    }

    public Task<WifiInfo?> GetWifiInfoAsync(CancellationToken cancellationToken = default) =>
        GetWifiInfoAsync(string.Empty, string.Empty, cancellationToken);

    public async Task<WifiInfo?> GetWifiInfoAsync(
        string adapterName,
        string adapterDescription,
        CancellationToken cancellationToken = default)
    {
        // Locale-safe primary path: read the connection straight from the WLAN API
        // (P/Invoke). The netsh fallback below parses ENGLISH labels and silently returns
        // all-"Unknown" on a German/French/… Windows — so only use it when the WLAN
        // service is genuinely unavailable.
        var fromApi = TryGetWifiInfoFromWlanApi(adapterName, adapterDescription);
        if (fromApi is not null) return fromApi;

        var safeAdapterName = PsSingleQuote(adapterName);
        var safeAdapterDescription = PsSingleQuote(adapterDescription);
        var script = $$$"""
$ErrorActionPreference = 'Continue'
function Get-NetshValue($lines, $key) {
    $line = $lines | Where-Object { $_ -match "^\s*$([regex]::Escape($key))\s*:" } | Select-Object -First 1
    if ($line -match ':\s*(.+)$') { return $Matches[1].Trim() }
    return $null
}

function Split-InterfaceBlocks($lines) {
    $blocks = @()
    $current = @()
    foreach ($line in $lines) {
        if ($line -match '^\s*Name\s*:') {
            if ($current.Count -gt 0) {
                $blocks += ,@($current)
                $current = @()
            }
        }

        if ($current.Count -gt 0 -or $line -match '^\s*Name\s*:') {
            $current += $line
        }
    }

    if ($current.Count -gt 0) {
        $blocks += ,@($current)
    }

    return $blocks
}

$selectedAdapterName = '{{{safeAdapterName}}}'
$selectedAdapterDescription = '{{{safeAdapterDescription}}}'
$raw = netsh wlan show interfaces 2>$null
if ($LASTEXITCODE -ne 0 -or -not $raw) {
    [pscustomobject]@{
        InterfaceName = if ($selectedAdapterName) { $selectedAdapterName } else { 'Unknown' }
        InterfaceDescription = if ($selectedAdapterDescription) { $selectedAdapterDescription } else { 'Unknown' }
        CollectorStatus = 'Wi-Fi information unavailable'
    } | ConvertTo-Json -Depth 5
    exit 0
}

$blocks = @(Split-InterfaceBlocks $raw)
$selected = $null
if ($blocks.Count -gt 0) {
    if ($selectedAdapterName -or $selectedAdapterDescription) {
        $selected = $blocks | Where-Object {
            $name = Get-NetshValue $_ 'Name'
            $description = Get-NetshValue $_ 'Description'
            ($selectedAdapterName -and $name -and $name.Equals($selectedAdapterName, [System.StringComparison]::OrdinalIgnoreCase)) -or
                ($selectedAdapterDescription -and $description -and $description.Equals($selectedAdapterDescription, [System.StringComparison]::OrdinalIgnoreCase))
        } | Select-Object -First 1
    }

    if ($null -eq $selected -and $blocks.Count -eq 1) {
        $selected = $blocks[0]
    }
}

if ($null -eq $selected) {
    [pscustomobject]@{
        InterfaceName = if ($selectedAdapterName) { $selectedAdapterName } else { 'Unknown' }
        InterfaceDescription = if ($selectedAdapterDescription) { $selectedAdapterDescription } else { 'Unknown' }
        CollectorStatus = if ($selectedAdapterName -or $selectedAdapterDescription) { 'Wi-Fi information unavailable for selected adapter' } else { 'Wi-Fi information unavailable' }
    } | ConvertTo-Json -Depth 5
    exit 0
}

$interfaceName = Get-NetshValue $selected 'Name'
$interfaceDescription = Get-NetshValue $selected 'Description'
$state = Get-NetshValue $selected 'State'
if ($state -and -not $state.Equals('connected', [System.StringComparison]::OrdinalIgnoreCase)) {
    [pscustomobject]@{
        InterfaceName = if ($interfaceName) { [string]$interfaceName } else { if ($selectedAdapterName) { $selectedAdapterName } else { 'Unknown' } }
        InterfaceDescription = if ($interfaceDescription) { [string]$interfaceDescription } else { if ($selectedAdapterDescription) { $selectedAdapterDescription } else { 'Unknown' } }
        Ssid = 'Not connected'
        Bssid = 'Unknown'
        SignalPercent = $null
        RadioType = 'Unknown'
        Channel = $null
        ReceiveRateMbps = 'Unknown'
        TransmitRateMbps = 'Unknown'
        Authentication = 'Unknown'
        CollectorStatus = 'Wi-Fi adapter is not connected'
    } | ConvertTo-Json -Depth 5
    exit 0
}

$signalText = Get-NetshValue $selected 'Signal'
$channelText = Get-NetshValue $selected 'Channel'
$signal = $null
if ($signalText -match '(\d+)') { $signal = [int]$Matches[1] }
$channel = $null
if ($channelText -match '(\d+)') { $channel = [int]$Matches[1] }

[pscustomobject]@{
    InterfaceName = if ($interfaceName) { [string]$interfaceName } else { if ($selectedAdapterName) { $selectedAdapterName } else { 'Unknown' } }
    InterfaceDescription = if ($interfaceDescription) { [string]$interfaceDescription } else { if ($selectedAdapterDescription) { $selectedAdapterDescription } else { 'Unknown' } }
    Ssid = [string](Get-NetshValue $selected 'SSID')
    Bssid = [string](Get-NetshValue $selected 'BSSID')
    SignalPercent = $signal
    RadioType = [string](Get-NetshValue $selected 'Radio type')
    Channel = $channel
    ReceiveRateMbps = [string](Get-NetshValue $selected 'Receive rate (Mbps)')
    TransmitRateMbps = [string](Get-NetshValue $selected 'Transmit rate (Mbps)')
    Authentication = [string](Get-NetshValue $selected 'Authentication')
    CollectorStatus = 'OK'
} | ConvertTo-Json -Depth 5
""";

        var (data, execution) = await RunCollectorWithExecutionAsync<WifiInfo>(script, TimeSpan.FromSeconds(10), cancellationToken);
        return data ?? new WifiInfo
        {
            InterfaceName = KnownOrUnknown(adapterName),
            InterfaceDescription = KnownOrUnknown(adapterDescription),
            CollectorStatus = execution.TimedOut
                ? "Wi-Fi collector timed out."
                : $"Wi-Fi collector failed: {execution.Error}"
        };
    }

    /// <summary>
    /// Build <see cref="WifiInfo"/> from the WLAN API (locale-independent). Returns null
    /// when the API is unavailable or any read fails, so the caller falls back to netsh.
    /// </summary>
    private WifiInfo? TryGetWifiInfoFromWlanApi(string adapterName, string adapterDescription)
    {
        if (_wlan is null || !_wlan.IsAvailable) return null;

        try
        {
            var interfaces = _wlan.EnumerateInterfaces();
            if (interfaces.Count == 0) return null;

            var iface = SelectWlanInterface(interfaces, adapterDescription);
            if (iface is null)
            {
                return new WifiInfo
                {
                    InterfaceName = KnownOrUnknown(adapterName),
                    InterfaceDescription = KnownOrUnknown(adapterDescription),
                    CollectorStatus = "Wi-Fi information unavailable for selected adapter",
                };
            }

            var conn = _wlan.QueryCurrentConnection(iface.InterfaceGuid);
            if (conn is null || conn.InterfaceState != WlanInterfaceState.Connected)
            {
                return new WifiInfo
                {
                    InterfaceName = KnownOrUnknown(adapterName),
                    InterfaceDescription = KnownOrUnknown(iface.Description),
                    Ssid = "Not connected",
                    Bssid = "Unknown",
                    SignalPercent = null,
                    RadioType = "Unknown",
                    Channel = null,
                    ReceiveRateMbps = "Unknown",
                    TransmitRateMbps = "Unknown",
                    Authentication = "Unknown",
                    CollectorStatus = "Wi-Fi adapter is not connected",
                };
            }

            var channel = _wlan.QueryCurrentChannel(iface.InterfaceGuid);
            var security = WifiSecurityProfile.From(conn.AuthAlgorithm, conn.CipherAlgorithm, conn.SecurityEnabled);

            static string RateMbps(long bitsPerSecond) =>
                bitsPerSecond > 0
                    ? (bitsPerSecond / 1_000_000.0).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)
                    : "Unknown";

            return new WifiInfo
            {
                InterfaceName = KnownOrUnknown(adapterName),
                InterfaceDescription = KnownOrUnknown(iface.Description),
                Ssid = string.IsNullOrEmpty(conn.Ssid) ? "<hidden>" : conn.Ssid,
                Bssid = string.IsNullOrEmpty(conn.Bssid) ? "Unknown" : conn.Bssid,
                SignalPercent = conn.SignalQualityPercent,
                RadioType = WifiPhyStandard.ToIeeeName(conn.PhyType),
                Channel = channel,
                ReceiveRateMbps = RateMbps(conn.RxRateBitsPerSecond),
                TransmitRateMbps = RateMbps(conn.TxRateBitsPerSecond),
                Authentication = security.Label,
                CollectorStatus = "OK",
            };
        }
        catch
        {
            // Any P/Invoke hiccup → let the netsh fallback try.
            return null;
        }
    }

    private static WlanInterfaceSnapshot? SelectWlanInterface(
        IReadOnlyList<WlanInterfaceSnapshot> interfaces,
        string adapterDescription)
    {
        if (interfaces.Count == 0) return null;

        if (HasKnownValue(adapterDescription))
        {
            var exact = interfaces.FirstOrDefault(i =>
                i.Description.Equals(adapterDescription, StringComparison.OrdinalIgnoreCase));
            if (exact is not null) return exact;

            var partial = interfaces.FirstOrDefault(i =>
                HasKnownValue(i.Description) &&
                (i.Description.Contains(adapterDescription, StringComparison.OrdinalIgnoreCase) ||
                 adapterDescription.Contains(i.Description, StringComparison.OrdinalIgnoreCase)));
            if (partial is not null) return partial;

            if (interfaces.Count > 1) return null;
        }

        return interfaces.FirstOrDefault(i => i.State == WlanInterfaceState.Connected)
               ?? interfaces[0];
    }

    private static bool HasKnownValue(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        !value.Equals("Unknown", StringComparison.OrdinalIgnoreCase);

    private static string KnownOrUnknown(string? value) =>
        HasKnownValue(value) ? value! : "Unknown";

    public async Task<string> GenerateWlanReportAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        const string script = """
$ErrorActionPreference = 'Continue'
$output = netsh wlan show wlanreport 2>&1
$path = $null
foreach ($line in $output) {
    if ($line -match ':\s*(?<path>[A-Z]:\\.+\.html)') {
        $path = $Matches['path']
    }
}
if (-not $path) {
    $default = Join-Path $env:ProgramData 'Microsoft\Windows\WlanReport\wlan-report-latest.html'
    if (Test-Path $default) { $path = $default }
}
[pscustomobject]@{ Path = if ($path) { $path } else { '' } } | ConvertTo-Json -Depth 3
""";

        var result = await RunCollectorAsync<WlanReportResult>(script, TimeSpan.FromSeconds(20), cancellationToken);
        return IsSafeWlanReportPath(result?.Path) ? Path.GetFullPath(result!.Path) : string.Empty;
    }

    private static bool IsSafeWlanReportPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            var fullPath = Path.GetFullPath(path);
            var extension = Path.GetExtension(fullPath);
            if (!extension.Equals(".html", StringComparison.OrdinalIgnoreCase) &&
                !extension.Equals(".htm", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (!File.Exists(fullPath))
            {
                return false;
            }

            var wlanReportDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "Microsoft",
                "Windows",
                "WlanReport");

            var fullDirectory = Path.GetFullPath(wlanReportDirectory);
            if (!fullDirectory.EndsWith(Path.DirectorySeparatorChar))
            {
                fullDirectory += Path.DirectorySeparatorChar;
            }

            return fullPath.StartsWith(fullDirectory, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private sealed class WlanReportResult
    {
        public string Path { get; set; } = string.Empty;
    }
}
