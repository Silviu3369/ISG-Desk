using NetScopeDiagnosticCenter.Core.Models;

namespace NetScopeDiagnosticCenter.Core;

public sealed class LinkQualityAnalyzer
{
    public LinkQualityResult BuildFromQuickDiagnosis(NetworkDiagnosisResult? diagnosis)
    {
        if (diagnosis is null)
        {
            return new LinkQualityResult
            {
                Source = "No data",
                Summary = "No Link Quality test has been run yet.",
                Severity = "Unknown",
                Confidence = "Low",
                AffectedLayer = "Unknown"
            };
        }

        var adapter = diagnosis.Adapter;
        var profile = diagnosis.Profile;
        var slowEthernet = adapter.ConnectionType.Equals("Ethernet", StringComparison.OrdinalIgnoreCase) &&
            adapter.LinkSpeedMbps.HasValue &&
            adapter.LinkSpeedMbps.Value < profile.ExpectedMinimumEthernetMbps;
        var increasingCounters = (adapter.ErrorsPerSecond ?? 0) > 0 || (adapter.DiscardsPerSecond ?? 0) > 0;
        var cumulativeCounters = adapter.Errors > 0 || adapter.Discards > 0;
        var severity = increasingCounters ? "Critical" : slowEthernet || cumulativeCounters ? "Warning" : "OK";

        var result = new LinkQualityResult
        {
            CreatedAt = DateTimeOffset.Now,
            Source = "Latest Quick Diagnosis",
            Summary = BuildSummary(adapter, severity, slowEthernet, increasingCounters, cumulativeCounters),
            Severity = severity,
            Confidence = increasingCounters ? "High" : slowEthernet || cumulativeCounters ? "Medium" : "Low",
            AffectedLayer = adapter.ConnectionType.Equals("Wi-Fi", StringComparison.OrdinalIgnoreCase)
                ? "Wi-Fi / Local Link"
                : "Cable / Switch Port",
            AdapterName = adapter.Name,
            InterfaceDescription = adapter.InterfaceDescription,
            ConnectionType = adapter.ConnectionType,
            MacAddress = adapter.MacAddress,
            DriverInformation = adapter.DriverInformation,
            LinkSpeed = adapter.LinkSpeed,
            LinkSpeedMbps = adapter.LinkSpeedMbps,
            ExpectedMinimumEthernetMbps = profile.ExpectedMinimumEthernetMbps,
            SpeedDuplex = adapter.SpeedDuplex,
            Errors = adapter.Errors,
            Discards = adapter.Discards,
            ErrorsPerSecond = adapter.ErrorsPerSecond,
            DiscardsPerSecond = adapter.DiscardsPerSecond,
            BytesPerSecond = adapter.BytesPerSecond,
            SamplingStatus = adapter.SamplingStatus,
            Evidence =
            [
                $"Adapter: {adapter.Name}; type: {adapter.ConnectionType}.",
                $"Link speed: {adapter.LinkSpeed}; expected Ethernet minimum: {profile.ExpectedMinimumEthernetMbps} Mbps.",
                $"Speed/duplex: {adapter.SpeedDuplex}.",
                $"Counters: {adapter.Errors} errors, {adapter.Discards} discards.",
                $"Sample: {adapter.SamplingStatus}; errors/s {FormatNullable(adapter.ErrorsPerSecond)}, discards/s {FormatNullable(adapter.DiscardsPerSecond)}."
            ],
            Limitations =
            [
                "This result is derived from the latest Quick Diagnosis adapter data.",
                "Ping and DNS evidence is populated only after a manual Link Quality ping or path diagnostics test.",
                "Switch-side configuration cannot be confirmed without SNMP or direct switch access."
            ]
        };

        result.Recommendations.AddRange(BuildRecommendations(result, slowEthernet, increasingCounters, cumulativeCounters));
        return result;
    }

    private static string BuildSummary(
        AdapterInfo adapter,
        string severity,
        bool slowEthernet,
        bool increasingCounters,
        bool cumulativeCounters)
    {
        if (increasingCounters)
        {
            return "Adapter errors or discards increased during the latest sample.";
        }

        if (slowEthernet)
        {
            return $"Ethernet link speed is below the expected minimum ({adapter.LinkSpeed}).";
        }

        if (cumulativeCounters)
        {
            return "Cumulative adapter errors or discards are present.";
        }

        return severity == "OK"
            ? "No adapter counter issue was detected in the latest Quick Diagnosis."
            : "Link quality status is unknown.";
    }

    private static IEnumerable<string> BuildRecommendations(
        LinkQualityResult result,
        bool slowEthernet,
        bool increasingCounters,
        bool cumulativeCounters)
    {
        if (increasingCounters)
        {
            yield return "Check cable, docking station, wall socket, patch panel and switch-side interface counters.";
            yield return "Repeat sampling or compare against switch counters to confirm the issue is active.";
            yield break;
        }

        if (slowEthernet)
        {
            yield return "Test with a known-good cable and another wall socket or dock.";
            yield return "Verify switch port speed/duplex and expected endpoint capability.";
        }

        if (cumulativeCounters)
        {
            yield return "Treat cumulative counters as historical evidence; repeat sampling before escalating as an active fault.";
        }

        if (!slowEthernet && !cumulativeCounters)
        {
            yield return "No immediate local adapter action from the available adapter evidence.";
            yield return "If users report slowness, run Link Quality path diagnostics and compare gateway, internet and application target results.";
        }
    }

    private static string FormatNullable(double? value) => DiagnosticHelpers.FormatNullable(value);
}
