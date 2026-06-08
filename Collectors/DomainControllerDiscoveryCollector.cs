using NetScopeDiagnosticCenter.Core;
using NetScopeDiagnosticCenter.Core.Models;
using NetScopeDiagnosticCenter.Infrastructure;

namespace NetScopeDiagnosticCenter.Collectors;

public class DomainControllerDiscoveryCollector : JsonCollectorBase
{
    public DomainControllerDiscoveryCollector(PowerShellRunner powerShellRunner) : base(powerShellRunner)
    {
    }

    public virtual async Task<DomainControllerDiscoveryResult> DiscoverAsync(
        IEnumerable<string> domainNames,
        CancellationToken cancellationToken = default)
    {
        var safeDomains = domainNames
            .Where(domain => DiagnosticTargetValidator.TryNormalizeHost(domain, out _, out _))
            .Select(domain =>
            {
                DiagnosticTargetValidator.TryNormalizeHost(domain, out var normalized, out _);
                return normalized;
            })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(DiagnosticConstants.MaxDomainDiscoveryDomains)
            .ToList();

        if (safeDomains.Count == 0)
        {
            return new DomainControllerDiscoveryResult
            {
                Verdict = "No valid AD domain name was available for DNS SRV discovery.",
                Severity = "Warning",
                SkippedReason = "No valid AD domain name was available for DNS SRV discovery.",
                Limitations = ["Discovery uses DNS SRV records and requires an AD DNS domain name."]
            };
        }

        var domainArray = string.Join(", ", safeDomains.Select(domain => $"'{PsSingleQuote(domain)}'"));
        var script = $$"""
$ErrorActionPreference = 'Continue'
$domains = @({{domainArray}})
$candidates = @()
$evidence = @()

foreach ($domain in $domains) {
    $query = "_ldap._tcp.dc._msdcs.$domain"
    try {
        $records = @(Resolve-DnsName -Name $query -Type SRV -ErrorAction Stop)
        $srvRecords = @($records | Where-Object { $_.NameTarget })
        $evidence += "Resolved $query to $($srvRecords.Count) SRV record(s)."
        foreach ($record in $srvRecords) {
            $hostName = ([string]$record.NameTarget).TrimEnd('.')
            if ([string]::IsNullOrWhiteSpace($hostName)) { continue }
            $candidates += [pscustomobject]@{
                Host = $hostName
                DomainName = $domain
                Source = "DNS SRV $query"
                Confidence = "High"
            }
        }
    }
    catch {
        $evidence += "DNS SRV lookup failed for $query: $($_.Exception.Message)"
    }
}

$unique = @()
foreach ($candidate in $candidates) {
    if (-not ($unique | Where-Object { $_.Host -ieq $candidate.Host })) {
        $unique += $candidate
    }
}

[pscustomobject]@{
    DomainNames = @($domains)
    Verdict = if ($unique.Count -gt 0) { "Found $($unique.Count) domain controller candidate(s) from DNS SRV." } else { "No domain controllers were found from DNS SRV." }
    Severity = if ($unique.Count -gt 0) { "OK" } else { "Warning" }
    Candidates = @($unique)
    Evidence = @($evidence)
    Limitations = @(
        "Discovery uses DNS SRV only; it does not authenticate, join, modify, or scan IP ranges.",
        "A returned domain controller is still validated later by Kerberos 88, LDAP 389 and SMB 445 checks."
    )
} | ConvertTo-Json -Depth 6
""";

        var (data, execution) = await RunCollectorWithExecutionAsync<DomainControllerDiscoveryResult>(
            script,
            TimeSpan.FromSeconds(12),
            cancellationToken);

        if (data is null)
        {
            var details = execution.TimedOut
                ? "Domain controller DNS SRV discovery timed out."
                : $"Domain controller DNS SRV discovery failed: {execution.Error}";
            return new DomainControllerDiscoveryResult
            {
                DomainNames = safeDomains,
                Verdict = details,
                Severity = "Warning",
                SkippedReason = details,
                Evidence = [details]
            };
        }

        data.Candidates = data.Candidates
            .Where(candidate => DiagnosticTargetValidator.TryNormalizeHost(candidate.Host, out _, out _))
            .Select(candidate =>
            {
                DiagnosticTargetValidator.TryNormalizeHost(candidate.Host, out var normalized, out _);
                return new DomainControllerCandidate
                {
                    Host = normalized,
                    DomainName = candidate.DomainName,
                    Source = candidate.Source,
                    Confidence = candidate.Confidence
                };
            })
            .DistinctBy(candidate => candidate.Host, StringComparer.OrdinalIgnoreCase)
            .Take(DiagnosticConstants.MaxTargetedTestTargets)
            .ToList();

        data.DomainNames = safeDomains;
        if (string.IsNullOrWhiteSpace(data.Verdict))
        {
            data.Verdict = data.Candidates.Count > 0
                ? $"Found {data.Candidates.Count} domain controller candidate(s) from DNS SRV."
                : "No domain controllers were found from DNS SRV.";
        }

        return data;
    }
}
