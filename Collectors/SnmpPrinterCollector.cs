using NetScopeDiagnosticCenter.Core;
using NetScopeDiagnosticCenter.Core.Models;
using NetScopeDiagnosticCenter.Infrastructure;

namespace NetScopeDiagnosticCenter.Collectors;

public class SnmpPrinterCollector
{
    private readonly SnmpClientService _snmpClient;

    private const string SysDescr = "1.3.6.1.2.1.1.1.0";
    private const string SysUpTime = "1.3.6.1.2.1.1.3.0";
    private const string SysContact = "1.3.6.1.2.1.1.4.0";
    private const string SysName = "1.3.6.1.2.1.1.5.0";
    private const string SysLocation = "1.3.6.1.2.1.1.6.0";
    private const string PrinterName = "1.3.6.1.2.1.43.5.1.1.16.1";
    private const string PrinterSerialNumber = "1.3.6.1.2.1.43.5.1.1.17.1";
    private const string HrPrinterStatus = "1.3.6.1.2.1.25.3.5.1.1";
    private const string SupplyDescription = "1.3.6.1.2.1.43.11.1.1.6";
    private const string SupplyMaxCapacity = "1.3.6.1.2.1.43.11.1.1.8";
    private const string SupplyLevel = "1.3.6.1.2.1.43.11.1.1.9";
    private const string MarkerLifeCount = "1.3.6.1.2.1.43.10.2.1.4";
    private const string AlertDescription = "1.3.6.1.2.1.43.18.1.1.8";

    private static readonly string[] IdentityOids =
    [
        SysDescr,
        SysUpTime,
        SysContact,
        SysName,
        SysLocation,
        PrinterName,
        PrinterSerialNumber
    ];

    public SnmpPrinterCollector(SnmpClientService snmpClient)
    {
        _snmpClient = snmpClient;
    }

    public virtual async Task<SnmpDeviceInfo> IdentifyAsync(
        SnmpSessionOptions options,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(options.Target))
        {
            return new SnmpDeviceInfo { Error = "SNMP target is required." };
        }

        if (!DiagnosticTargetValidator.TryNormalizeHost(options.Target, out var normalizedTarget, out var validationReason))
        {
            return new SnmpDeviceInfo
            {
                Address = options.Target.Trim(),
                Protocol = options.Protocol,
                Error = validationReason
            };
        }

        if (options.Protocol == SnmpProtocolVersion.V3AuthPriv &&
            (string.IsNullOrWhiteSpace(options.UserName) ||
             string.IsNullOrWhiteSpace(options.AuthPassword) ||
             string.IsNullOrWhiteSpace(options.PrivacyPassword)))
        {
            return new SnmpDeviceInfo
            {
                Address = normalizedTarget,
                Protocol = options.Protocol,
                Error = "SNMPv3 authPriv requires username, authentication password and privacy password."
            };
        }

        try
        {
            options.Target = normalizedTarget;
            options.Community = string.IsNullOrWhiteSpace(options.Community) ? "public" : options.Community.Trim();
            var address = await _snmpClient.ResolveIpv4Async(options.Target, cancellationToken);
            var values = await _snmpClient.GetAsync(options, IdentityOids, cancellationToken);
            var printerStatus = await ReadPrinterStatusAsync(options, cancellationToken);
            var supplies = await ReadSuppliesAsync(options, cancellationToken);
            var pageCount = await ReadPageCountAsync(options, cancellationToken);
            var alerts = await ReadActiveAlertsAsync(options, cancellationToken);

            var info = new SnmpDeviceInfo
            {
                Address = address.ToString(),
                Success = true,
                Protocol = options.Protocol,
                SysDescr = GetValue(values, SysDescr),
                SysUpTime = GetValue(values, SysUpTime),
                SysContact = GetValue(values, SysContact),
                SysName = GetValue(values, SysName),
                SysLocation = GetValue(values, SysLocation),
                PrinterName = GetValue(values, PrinterName),
                PrinterSerialNumber = GetValue(values, PrinterSerialNumber),
                PrinterStatus = printerStatus,
                Supplies = supplies,
                PageCount = pageCount,
                ActiveAlerts = alerts,
                SuppliesSummary = BuildSuppliesSummary(supplies)
            };

            info.PrinterConfirmed = HasPrinterSpecificEvidence(info);
            if (string.IsNullOrWhiteSpace(info.SysDescr) &&
                string.IsNullOrWhiteSpace(info.SysName) &&
                string.IsNullOrWhiteSpace(info.PrinterName))
            {
                info.Success = false;
                info.Error = "SNMP responded but did not return identity fields.";
            }

            return info;
        }
        catch (TimeoutException)
        {
            return new SnmpDeviceInfo
            {
                Address = options.Target.Trim(),
                Protocol = options.Protocol,
                Error = "SNMP timed out on UDP 161."
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new SnmpDeviceInfo
            {
                Address = options.Target.Trim(),
                Protocol = options.Protocol,
                Error = ex.Message
            };
        }
    }

    private async Task<string> ReadPrinterStatusAsync(
        SnmpSessionOptions options,
        CancellationToken cancellationToken)
    {
        try
        {
            var statuses = await _snmpClient.WalkRawAsync(options, HrPrinterStatus, cancellationToken);
            var raw = statuses.Values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
            return FormatPrinterStatus(raw);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return string.Empty;
        }
    }

    private async Task<List<SnmpSupplyInfo>> ReadSuppliesAsync(
        SnmpSessionOptions options,
        CancellationToken cancellationToken)
    {
        try
        {
            var descriptions = await _snmpClient.WalkRawAsync(options, SupplyDescription, cancellationToken);
            if (descriptions.Count == 0)
            {
                return [];
            }

            var levels = await _snmpClient.WalkRawAsync(options, SupplyLevel, cancellationToken);
            var maxValues = await _snmpClient.WalkRawAsync(options, SupplyMaxCapacity, cancellationToken);

            return descriptions
                .Take(12)
                .Where(item => !string.IsNullOrWhiteSpace(item.Value))
                .Select(item =>
                {
                    var suffix = GetSuffix(SupplyDescription, item.Key);
                    return new SnmpSupplyInfo
                    {
                        Name = item.Value.Trim(),
                        Level = ParseIntOr(GetBySuffix(levels, SupplyLevel, suffix), -2),
                        MaxCapacity = ParseIntOr(GetBySuffix(maxValues, SupplyMaxCapacity, suffix), -2),
                    };
                })
                .ToList();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return [];
        }
    }

    private async Task<long?> ReadPageCountAsync(
        SnmpSessionOptions options,
        CancellationToken cancellationToken)
    {
        try
        {
            var rows = await _snmpClient.WalkRawAsync(options, MarkerLifeCount, cancellationToken);
            foreach (var raw in rows.Values)
            {
                if (long.TryParse(raw?.Trim(), out var pages) && pages > 0)
                {
                    return pages;
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Page counter not exposed; leave null.
        }

        return null;
    }

    private async Task<List<string>> ReadActiveAlertsAsync(
        SnmpSessionOptions options,
        CancellationToken cancellationToken)
    {
        try
        {
            var rows = await _snmpClient.WalkRawAsync(options, AlertDescription, cancellationToken);
            return rows.Values
                .Where(v => !string.IsNullOrWhiteSpace(v) && !IsNoSuchValue(v))
                .Select(v => v.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(20)
                .ToList();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return [];
        }
    }

    private static string BuildSuppliesSummary(IReadOnlyList<SnmpSupplyInfo> supplies) =>
        supplies.Count == 0
            ? string.Empty
            : string.Join("; ", supplies.Select(s => $"{s.Name}: {s.LevelText}"));

    private static int ParseIntOr(string? value, int fallback) =>
        int.TryParse(value?.Trim(), out var parsed) ? parsed : fallback;

    private static string GetBySuffix(IReadOnlyDictionary<string, string> values, string baseOid, string suffix)
    {
        return values.TryGetValue($"{baseOid}.{suffix}", out var value) ? value : string.Empty;
    }

    private static string GetSuffix(string baseOid, string oid)
    {
        return oid.StartsWith(baseOid + ".", StringComparison.Ordinal)
            ? oid[(baseOid.Length + 1)..]
            : string.Empty;
    }

    private static string GetValue(IReadOnlyDictionary<string, string> values, string oid)
    {
        return values.TryGetValue(oid, out var value) && !IsNoSuchValue(value) ? value : string.Empty;
    }

    private static bool IsNoSuchValue(string value)
    {
        return value.Equals("No such object", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("No such instance", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("End of MIB view", StringComparison.OrdinalIgnoreCase);
    }

    private static string FormatPrinterStatus(string? raw)
    {
        return raw switch
        {
            "1" => "Other",
            "2" => "Unknown",
            "3" => "Idle",
            "4" => "Printing",
            "5" => "Warmup",
            _ => raw ?? string.Empty
        };
    }

    private static bool HasPrinterSpecificEvidence(SnmpDeviceInfo info)
    {
        if (!string.IsNullOrWhiteSpace(info.PrinterName) ||
            !string.IsNullOrWhiteSpace(info.PrinterSerialNumber) ||
            !string.IsNullOrWhiteSpace(info.PrinterStatus) ||
            !string.IsNullOrWhiteSpace(info.SuppliesSummary))
        {
            return true;
        }

        var identity = $"{info.SysName} {info.SysDescr}".ToLowerInvariant();
        return identity.Contains("printer", StringComparison.Ordinal) ||
               identity.Contains("laserjet", StringComparison.Ordinal) ||
               identity.Contains("xerox", StringComparison.Ordinal) ||
               identity.Contains("canon", StringComparison.Ordinal) ||
               identity.Contains("brother", StringComparison.Ordinal) ||
               identity.Contains("epson", StringComparison.Ordinal) ||
               identity.Contains("ricoh", StringComparison.Ordinal) ||
               identity.Contains("kyocera", StringComparison.Ordinal) ||
               identity.Contains("lexmark", StringComparison.Ordinal);
    }
}
