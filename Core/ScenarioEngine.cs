using System.Diagnostics;
using NetScopeDiagnosticCenter.Collectors;
using NetScopeDiagnosticCenter.Core.Models;

namespace NetScopeDiagnosticCenter.Core;

public sealed class ScenarioEngine
{
    private readonly TargetConnectivityCollector _targetCollector;
    private readonly DomainCollector _domainCollector;

    public ScenarioEngine(
        TargetConnectivityCollector targetCollector,
        DomainCollector domainCollector)
    {
        _targetCollector = targetCollector;
        _domainCollector = domainCollector;
    }

    public async Task<ScenarioDiagnosisResult> RunAsync(
        string workflowKey,
        NetworkDiagnosisResult diagnosis,
        NetworkProfile profile,
        WorkflowInput? input = null,
        CancellationToken cancellationToken = default)
    {
        return await RunCoreAsync(
            workflowKey,
            diagnosis,
            profile,
            input,
            $"Fresh local baseline from {diagnosis.CreatedAt:yyyy-MM-dd HH:mm:ss}.",
            cancellationToken);
    }

    public async Task<ScenarioDiagnosisResult> RunIndependentAsync(
        string workflowKey,
        NetworkProfile profile,
        WorkflowInput? input = null,
        CancellationToken cancellationToken = default)
    {
        var context = new NetworkDiagnosisResult
        {
            Profile = profile,
            CreatedAt = DateTimeOffset.Now
        };

        return await RunCoreAsync(
            workflowKey,
            context,
            profile,
            input,
            "Independent targeted test. Quick Diagnosis baseline was not run.",
            cancellationToken);
    }

    private async Task<ScenarioDiagnosisResult> RunCoreAsync(
        string workflowKey,
        NetworkDiagnosisResult diagnosis,
        NetworkProfile profile,
        WorkflowInput? input,
        string baselineSummary,
        CancellationToken cancellationToken)
    {
        input ??= new WorkflowInput();
        diagnosis.Profile = profile;
        var startedAt = DateTimeOffset.Now;
        var result = workflowKey switch
        {
            ScenarioWorkflow.InternalShare => await EvaluateTargetsAsync(
                workflowKey,
                BuildTargets(input.ShareTarget, profile.FileServers, "File server", [445]),
                [445],
                "Enter a server or share target before running this targeted test.",
                "Internal Server or Share Access",
                cancellationToken),
            ScenarioWorkflow.DnsDomain => await EvaluateDnsDomainAsync(diagnosis, profile, input, cancellationToken),
            ScenarioWorkflow.ServiceAccess => await EvaluateTargetsAsync(
                workflowKey,
                BuildExplicitTargets(input.ServiceTarget, "Service target", ParsePorts(input.ServicePorts, profile.ImportantPorts)),
                ParsePorts(input.ServicePorts, profile.ImportantPorts),
                "Enter a service target and one or more ports before running this targeted test.",
                "Service Access",
                cancellationToken),
            _ => MissingWorkflow(workflowKey)
        };

        ApplyWorkflowMetadata(result, input, startedAt, baselineSummary);
        return result;
    }


    private async Task<ScenarioDiagnosisResult> EvaluateTargetsAsync(
        string workflowKey,
        IReadOnlyList<DiagnosticTarget> targets,
        IReadOnlyList<int> defaultPorts,
        string missingTargetMessage,
        string workflowName,
        CancellationToken cancellationToken)
    {
        var result = Base(workflowKey);
        result.WorkflowName = workflowName;

        if (targets.Count == 0)
        {
            return Finalize(result, "Targeted test needs configured targets", "Warning", "High", "Configuration", "Local Support",
                missingTargetMessage);
        }

        if (defaultPorts.Count == 0)
        {
            return Finalize(result, "Targeted test needs valid service ports", "Warning", "High", "Configuration", "Local Support",
                missingTargetMessage);
        }

        foreach (var target in targets)
        {
            var stepWatch = Stopwatch.StartNew();
            var probe = await _targetCollector.ProbeTargetAsync(target, defaultPorts, cancellationToken);
            stepWatch.Stop();
            EvaluateProbe(probe);
            result.TargetResults.Add(probe);
            result.Evidence.Add($"Target: {target} at {target.Host}.");
            result.Evidence.Add($"DNS: {probe.DnsStatus}; resolved address: {probe.ResolvedAddress}.");
            result.Evidence.Add($"Ping: {probe.PingStatus}; latency: {FormatLatency(probe.AverageLatencyMs)}.");
            result.Evidence.Add(PacketLossEvidence(target.Host, probe.PacketLoss));
            foreach (var port in probe.Ports)
            {
                result.Evidence.Add($"TCP {port.Port}: {(port.TcpSucceeded ? "Open" : "Failed")}.");
            }
            if (HasShareProbe(probe))
            {
                result.Evidence.Add($"Share path: {probe.ShareStatus}; {probe.ShareDetails}");
            }
            result.Steps.Add(BuildTargetStep(probe, stepWatch.Elapsed.TotalMilliseconds));
        }

        if (result.TargetResults.Any(probe => !IsCollectorOk(probe.CollectorStatus)))
        {
            return Finalize(result, "One or more target probes could not complete", "Warning", "Medium", "Collector", "Local Support",
                "Review collector error details, retry, and validate PowerShell/network permissions.");
        }

        if (result.TargetResults.All(probe => probe.DnsStatus == "Critical"))
        {
            return Finalize(result, "Target name does not resolve", "Critical", "High", "DNS", "Network",
                "Check internal DNS record, suffix search list and DNS server health.");
        }

        if (result.TargetResults.All(probe => probe.PingStatus == "Critical" && probe.Ports.All(port => !port.TcpSucceeded)))
        {
            return Finalize(result, "All tested targets are unreachable from this PC", "Critical", "Medium", "Server / Firewall", "Network / Server",
                "Check server status, VLAN/routing and firewall policy.");
        }

        if (result.TargetResults.Any(probe => probe.PingStatus == "Critical" && probe.Ports.Any(port => port.TcpSucceeded)))
        {
            return Finalize(result, "Service is reachable but ICMP ping is blocked or unavailable", "OK", "Medium", "Service", "Local Support",
                "Treat TCP success as stronger evidence than ICMP; continue with application/service-specific checks.");
        }

        if (result.TargetResults.Any(probe => probe.PingStatus == "OK" && probe.Ports.Any(port => !port.TcpSucceeded)))
        {
            return Finalize(result, "Target responds but one or more service ports failed", "Warning", "High", "Firewall / Server", "Server / Firewall",
                "Check service status and firewall rules for the failed port.");
        }

        if (result.TargetResults.Any(probe => probe.Ports.Count > 0 && probe.Ports.All(port => !port.TcpSucceeded)))
        {
            return Finalize(result, "One or more targets are reachable but required service ports failed", "Warning", "High", "Firewall / Server", "Server / Firewall",
                "Check service status and firewall rules for the required port.");
        }

        if (result.TargetResults.Any(probe => probe.Ports.Any(port => !port.TcpSucceeded)))
        {
            return Finalize(result, "One or more required service ports failed", "Warning", "Medium", "Firewall / Server", "Server / Firewall",
                "Review the failed port column and confirm whether each listed service should be reachable.");
        }

        if (workflowKey == ScenarioWorkflow.InternalShare &&
            result.TargetResults.Any(probe => string.Equals(probe.ShareStatus, "Critical", StringComparison.OrdinalIgnoreCase)))
        {
            return Finalize(result, "UNC share path is not accessible", "Critical", "High", "SMB / File Share", "Server / File Services",
                "Confirm the share name, SMB service state and file-share permissions.");
        }

        if (workflowKey == ScenarioWorkflow.InternalShare &&
            result.TargetResults.Any(probe => string.Equals(probe.ShareStatus, "Warning", StringComparison.OrdinalIgnoreCase)))
        {
            return Finalize(result, "UNC share exists but access was denied", "Warning", "High", "Permissions", "Server / Access",
                "Confirm the user's group membership and share/NTFS permissions.");
        }

        if (result.TargetResults.Any(probe => probe.PacketLoss.Received > 0 && probe.PacketLoss.LossPercent is > 5))
        {
            return Finalize(result, "Target path has packet loss", "Critical", "Medium", "Network", "Network",
                "Check switch path, Wi-Fi, gateway and server-side interface counters.");
        }

        return Finalize(result, "Target connectivity looks healthy for tested targets", "OK", "High", "Server", "Local Support",
            "If the application still fails, check permissions, application logs and service-specific configuration.");
    }

    private async Task<ScenarioDiagnosisResult> EvaluateDnsDomainAsync(
        NetworkDiagnosisResult diagnosis,
        NetworkProfile profile,
        WorkflowInput input,
        CancellationToken cancellationToken)
    {
        var domain = await _domainCollector.GetDomainInfoAsync(cancellationToken);
        diagnosis.Domain = domain;
        var result = Base(ScenarioWorkflow.DnsDomain);

        result.Evidence.Add($"Domain joined: {domain.IsDomainJoined}.");
        result.Evidence.Add($"Detected domain: {domain.DomainName}.");
        result.Evidence.Add($"Logon server: {domain.LogonServer}.");
        result.Evidence.Add($"DNS suffixes: {FormatDnsSuffixes(domain)}.");
        AddStep(result, "Domain context", "PC / DNS", DomainStatus(domain),
            [
                $"Domain joined: {domain.IsDomainJoined}.",
                $"Domain: {domain.DomainName}; logon server: {domain.LogonServer}.",
                $"DNS suffixes: {FormatDnsSuffixes(domain)}."
            ],
            IsCollectorOk(domain.CollectorStatus) ? string.Empty : domain.CollectorStatus,
            "Validate domain join state, DNS suffixes and detected logon server.");

        var targets = BuildTargets(input.DomainControllerOverride, profile.DomainControllers, "Domain controller", [88, 389, 445]);
        if (targets.Count == 0 &&
            domain.IsDomainJoined &&
            !string.IsNullOrWhiteSpace(domain.LogonServer) &&
            domain.LogonServer != "Unknown" &&
            domain.LogonServer != "Not domain joined")
        {
            targets.Add(new DiagnosticTarget
            {
                Name = "Logon server",
                Host = domain.LogonServer,
                Purpose = "Domain controller",
                Ports = [88, 389, 445]
            });
        }

        if (!domain.IsDomainJoined &&
            string.IsNullOrWhiteSpace(profile.DomainName) &&
            targets.Count == 0)
        {
            return Finalize(result, "PC is not domain joined or no domain profile is configured", "Warning", "Medium", "PC / DNS", "Local Support",
                "Enter a domain controller override, configure expected domain/DC targets, or verify this PC is supposed to be domain joined.");
        }

        if (targets.Count == 0)
        {
            return Finalize(result, "Domain targeted test needs a domain controller target", "Warning", "High", "Configuration", "Local Support",
                "Add one or more domain controllers to the network profile.");
        }

        foreach (var target in targets)
        {
            var stepWatch = Stopwatch.StartNew();
            var probe = await _targetCollector.ProbeTargetAsync(target, [88, 389, 445], cancellationToken);
            stepWatch.Stop();
            EvaluateProbe(probe);
            result.TargetResults.Add(probe);
            result.Evidence.Add($"DC target: {probe.Target.Host}; DNS: {probe.DnsStatus}; Ping: {probe.PingStatus}.");
            foreach (var port in probe.Ports)
            {
                result.Evidence.Add($"DC TCP {port.Port}: {(port.TcpSucceeded ? "Open" : "Failed")}.");
            }
            result.Steps.Add(BuildTargetStep(probe, stepWatch.Elapsed.TotalMilliseconds));
        }

        if (result.TargetResults.Any(probe => !IsCollectorOk(probe.CollectorStatus)))
        {
            return Finalize(result, "One or more domain probes could not complete", "Warning", "Medium", "Collector", "Local Support",
                "Review collector error details, retry, and validate PowerShell/network permissions.");
        }

        if (result.TargetResults.All(probe => probe.DnsStatus == "Critical"))
        {
            return Finalize(result, "Domain controller name does not resolve", "Critical", "High", "DNS", "Network",
                "Check DNS suffix, domain DNS server and DC records.");
        }

        if (result.TargetResults.Any(probe => probe.Ports.Any(port => port.Port is 88 or 389 or 445 && !port.TcpSucceeded)))
        {
            return Finalize(result, "One or more domain service ports are blocked or unavailable", "Critical", "Medium", "Firewall / Domain", "Network / Server",
                "Check Kerberos 88, LDAP 389 and SMB 445 to the domain controller; DNS must be validated with real name resolution, not TCP 53 alone.");
        }

        if (!domain.IsDomainJoined)
        {
            return Finalize(result, "Domain controller is reachable but this PC is not domain joined", "Warning", "High", "PC / Domain", "Local Support",
                "If this workstation should be domain joined, verify join state, trust relationship and machine account health.");
        }

        return Finalize(result, "DNS and domain connectivity look healthy", "OK", "High", "DNS / Domain", "Local Support",
            "If login or share access still fails, check credentials, permissions and server-side logs.");
    }


    private static ScenarioDiagnosisResult MissingWorkflow(string workflowKey)
    {
        return Finalize(Base(workflowKey), "Unknown targeted test", "Unknown", "Low", "Unknown", "Local Support",
            "Select a supported targeted test.");
    }

    private static void ApplyWorkflowMetadata(
        ScenarioDiagnosisResult result,
        WorkflowInput input,
        DateTimeOffset startedAt,
        string baselineSummary)
    {
        result.StartedAt = startedAt;
        result.CompletedAt ??= DateTimeOffset.Now;
        result.BaselineSummary = baselineSummary;
        result.InputSummary = BuildInputSummary(result.WorkflowKey, input);
    }

    private static string BuildInputSummary(string workflowKey, WorkflowInput input)
    {
        return workflowKey switch
        {
            ScenarioWorkflow.InternalShare => string.IsNullOrWhiteSpace(input.ShareTarget) ? "No share/server target entered." : $"Share/server target: {input.ShareTarget.Trim()}",
            ScenarioWorkflow.DnsDomain => string.IsNullOrWhiteSpace(input.DomainControllerOverride) ? "Using detected/profile domain controller targets." : $"Domain controller override: {input.DomainControllerOverride.Trim()}",
            ScenarioWorkflow.ServiceAccess => $"Service target: {ValueOrNone(input.ServiceTarget)}; ports: {ValueOrNone(input.ServicePorts)}",
            _ => "No manual target required."
        };
    }

    private static string ValueOrNone(string value) =>
        string.IsNullOrWhiteSpace(value) ? "not entered" : value.Trim();

    private static void AddStep(
        ScenarioDiagnosisResult result,
        string name,
        string layer,
        string status,
        IEnumerable<string> evidence,
        string error,
        string recommendation,
        double durationMs = 0)
    {
        result.Steps.Add(new DiagnosisStepResult
        {
            Name = name,
            Layer = layer,
            Status = string.IsNullOrWhiteSpace(status) ? "Unknown" : status,
            DurationMs = durationMs,
            Evidence = [.. evidence],
            Error = error,
            Recommendation = recommendation
        });
    }

    private static DiagnosisStepResult BuildTargetStep(TargetProbeResult probe, double durationMs)
    {
        var status = ProbeStatus(probe);
        return new DiagnosisStepResult
        {
            Name = probe.Target.Host,
            Layer = probe.Target.Purpose,
            Status = status,
            DurationMs = durationMs,
            Evidence =
            [
                $"DNS: {probe.DnsStatus}; resolved address: {probe.ResolvedAddress}.",
                $"Ping: {probe.PingStatus}; latency: {probe.LatencyText}.",
                $"Packet loss: {probe.LossText}.",
                $"Ports: {probe.PortSummary}.",
                ShareEvidence(probe)
            ],
            Error = IsCollectorOk(probe.CollectorStatus) ? string.Empty : probe.CollectorStatus,
            Recommendation = probe.Verdict switch
            {
                "DNS failed" => "Check DNS record, suffix search list and DNS server health.",
                "Unreachable" => "Check routing, VLAN/firewall path and server availability.",
                "Ports failed" => "Check service state and firewall policy for the required ports.",
                "Service/firewall issue" => "Check service status and firewall policy for the failed ports.",
                "Service reachable; ICMP blocked" => "Continue with service-specific checks; ICMP failure alone is not a service outage.",
                "Share failed" => "Confirm the UNC share name, file service state and share/NTFS permissions.",
                "Share access warning" => "Confirm the current user is expected to have access to this share.",
                "Packet loss" => "Check local link, Wi-Fi, gateway and switch/server counters.",
                _ => "If the application still fails, check service-specific logs and permissions."
            }
        };
    }

    private static string ProbeStatus(TargetProbeResult probe)
    {
        if (!IsCollectorOk(probe.CollectorStatus))
            return "Warning";
        if (probe.DnsStatus == "Critical" || probe.PingStatus == "Critical" && probe.Ports.All(port => !port.TcpSucceeded))
            return "Critical";
        if (probe.PingStatus == "Critical" && probe.Ports.Any(port => port.TcpSucceeded))
            return "OK";
        if (string.Equals(probe.ShareStatus, "Critical", StringComparison.OrdinalIgnoreCase))
            return "Critical";
        if (string.Equals(probe.ShareStatus, "Warning", StringComparison.OrdinalIgnoreCase))
            return "Warning";
        if (probe.Ports.Any(port => !port.TcpSucceeded) || probe.PacketLoss.Received > 0 && probe.PacketLoss.LossPercent is > 0)
            return "Warning";
        return "OK";
    }

    private static string DomainStatus(DomainInfo domain)
    {
        if (!IsCollectorOk(domain.CollectorStatus))
            return "Warning";
        if (!domain.IsDomainJoined)
            return "Warning";
        if (string.IsNullOrWhiteSpace(domain.LogonServer) || domain.LogonServer == "Unknown")
            return "Warning";
        return "OK";
    }

    private static string FormatDnsSuffixes(DomainInfo domain) =>
        domain.DnsSuffixes.Count == 0 ? "None" : string.Join(", ", domain.DnsSuffixes);

    private static bool IsCollectorOk(string status) => DiagnosticHelpers.IsCollectorOk(status);

    private static ScenarioDiagnosisResult Base(string workflowKey)
    {
        return new ScenarioDiagnosisResult
        {
            WorkflowKey = workflowKey,
            WorkflowName = ScenarioWorkflow.GetName(workflowKey),
            Limitations =
            [
                "This targeted test diagnoses the local PC and configured profile targets only.",
                "Switch, firewall and server-side configuration is inferred unless direct evidence is available."
            ]
        };
    }

    private static ScenarioDiagnosisResult Finalize(
        ScenarioDiagnosisResult result,
        string title,
        string severity,
        string confidence,
        string affectedLayer,
        string ownerSuggestion,
        string nextCheck)
    {
        result.Title = title;
        result.Severity = severity;
        result.Confidence = confidence;
        result.AffectedLayer = affectedLayer;
        result.OwnerSuggestion = ownerSuggestion;
        result.NextChecks.Add(nextCheck);
        result.CompletedAt ??= DateTimeOffset.Now;
        return result;
    }

    private static string PacketLossEvidence(string name, PacketLossResult packetLoss)
    {
        return packetLoss.LossPercent.HasValue
            ? $"{name} packet loss: {packetLoss.LossPercent:N1}% ({packetLoss.Received}/{packetLoss.Sent} replies)."
            : $"{name} packet loss: {packetLoss.Status}.";
    }

    private static string FormatLatency(double? latency)
    {
        return latency.HasValue ? $"{latency.Value:N1} ms" : "Unknown";
    }

    private static List<DiagnosticTarget> BuildTargets(
        string inputTarget,
        IReadOnlyList<DiagnosticTarget> fallbackTargets,
        string purpose,
        IReadOnlyList<int> defaultPorts)
    {
        if (!string.IsNullOrWhiteSpace(inputTarget))
        {
            return ParseTargetInput(inputTarget, purpose, defaultPorts);
        }

        return [.. fallbackTargets];
    }

    private static List<DiagnosticTarget> BuildExplicitTargets(
        string inputTarget,
        string purpose,
        IReadOnlyList<int> defaultPorts)
    {
        return string.IsNullOrWhiteSpace(inputTarget)
            ? []
            : ParseTargetInput(inputTarget, purpose, defaultPorts);
    }

    private static List<int> ParsePorts(string portsText, IReadOnlyList<int> fallbackPorts)
    {
        var fallback = fallbackPorts
            .Distinct()
            .Where(port => port is > 0 and <= 65535)
            .Take(DiagnosticConstants.MaxTargetedTestPorts)
            .ToList();

        if (string.IsNullOrWhiteSpace(portsText))
        {
            return fallback;
        }

        var parsedPorts = portsText
            .Split([';', ',', ' ', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => int.TryParse(part, out var port) && port is > 0 and <= 65535 ? port : 0)
            .ToList();

        if (parsedPorts.Any(port => port == 0))
        {
            return [];
        }

        return parsedPorts
            .Distinct()
            .Take(DiagnosticConstants.MaxTargetedTestPorts)
            .ToList();
    }

    private static List<DiagnosticTarget> ParseTargetInput(string inputTarget, string purpose, IReadOnlyList<int> defaultPorts)
    {
        return inputTarget
            .Split([';', ',', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(target => BuildTarget(target, purpose, defaultPorts))
            .OfType<DiagnosticTarget>()
            .DistinctBy(target => string.IsNullOrWhiteSpace(target.SharePath) ? target.Host : target.SharePath, StringComparer.OrdinalIgnoreCase)
            .Take(DiagnosticConstants.MaxTargetedTestTargets)
            .ToList();
    }

    private static DiagnosticTarget? BuildTarget(string target, string purpose, IReadOnlyList<int> defaultPorts)
    {
        if (!DiagnosticTargetValidator.TryNormalizeHostOrShare(target, out var normalizedHost, out var normalizedSharePath, out _))
        {
            return null;
        }

        return new DiagnosticTarget
        {
            Name = string.IsNullOrWhiteSpace(normalizedSharePath) ? normalizedHost : normalizedSharePath,
            Host = normalizedHost,
            SharePath = normalizedSharePath,
            Purpose = purpose,
            Ports = [.. defaultPorts]
        };
    }

    private static void EvaluateProbe(TargetProbeResult probe)
    {
        if (!IsCollectorOk(probe.CollectorStatus))
        {
            probe.Verdict = "Collector warning";
            probe.OwnerSuggestion = "Local Support";
            return;
        }

        if (probe.DnsStatus == "Critical")
        {
            probe.Verdict = "DNS failed";
            probe.OwnerSuggestion = "Network";
            return;
        }

        if (probe.PingStatus == "Critical" && probe.Ports.All(port => !port.TcpSucceeded))
        {
            probe.Verdict = "Unreachable";
            probe.OwnerSuggestion = "Network / Server";
            return;
        }

        if (probe.PingStatus == "Critical" && probe.Ports.Any(port => port.TcpSucceeded))
        {
            probe.Verdict = "Service reachable; ICMP blocked";
            probe.OwnerSuggestion = "Local Support";
            return;
        }

        if (probe.PingStatus == "OK" && probe.Ports.Any(port => !port.TcpSucceeded))
        {
            probe.Verdict = "Service/firewall issue";
            probe.OwnerSuggestion = "Server / Firewall";
            return;
        }

        if (probe.Ports.Count > 0 && probe.Ports.All(port => !port.TcpSucceeded))
        {
            probe.Verdict = "Ports failed";
            probe.OwnerSuggestion = "Server / Firewall";
            return;
        }

        if (probe.Ports.Count > 0 && probe.Ports.Any(port => !port.TcpSucceeded))
        {
            probe.Verdict = "Some ports failed";
            probe.OwnerSuggestion = "Server / Firewall";
            return;
        }

        if (string.Equals(probe.ShareStatus, "Critical", StringComparison.OrdinalIgnoreCase))
        {
            probe.Verdict = "Share failed";
            probe.OwnerSuggestion = "Server / File Services";
            return;
        }

        if (string.Equals(probe.ShareStatus, "Warning", StringComparison.OrdinalIgnoreCase))
        {
            probe.Verdict = "Share access warning";
            probe.OwnerSuggestion = "Server / Access";
            return;
        }

        if (probe.PacketLoss.Received > 0 && probe.PacketLoss.LossPercent is > 5)
        {
            probe.Verdict = "Packet loss";
            probe.OwnerSuggestion = "Network";
            return;
        }

        probe.Verdict = "Healthy";
        probe.OwnerSuggestion = "Local Support";
    }

    private static bool HasShareProbe(TargetProbeResult probe) =>
        !string.IsNullOrWhiteSpace(probe.Target.SharePath) ||
        !string.Equals(probe.ShareStatus, "Not tested", StringComparison.OrdinalIgnoreCase);

    private static string ShareEvidence(TargetProbeResult probe) =>
        HasShareProbe(probe)
            ? $"Share: {probe.ShareStatus}; {probe.ShareDetails}"
            : "Share: Not tested.";
}
