using NetScopeDiagnosticCenter.Collectors;
using NetScopeDiagnosticCenter.Core;
using NetScopeDiagnosticCenter.Core.Models;
using NetScopeDiagnosticCenter.Infrastructure;

namespace NetScopeDiagnosticCenter.Tests.Core;

public sealed class ScenarioEngineTests
{
    [Fact]
    public async Task RunAsync_ServiceAccessWithInvalidUserPorts_DoesNotFallBackToProfilePorts()
    {
        var engine = new ScenarioEngine(
            new TargetConnectivityCollector(new PowerShellRunner(new NullLogger())),
            new DomainCollector(new PowerShellRunner(new NullLogger())));
        var profile = new NetworkProfile { ImportantPorts = [443] };
        var input = new WorkflowInput
        {
            ServiceTarget = "example.com",
            ServicePorts = "70000"
        };

        var result = await engine.RunAsync(
            ScenarioWorkflow.ServiceAccess,
            new NetworkDiagnosisResult(),
            profile,
            input);

        result.Title.Should().Be("Targeted test needs valid service ports");
        result.Severity.Should().Be("Warning");
        result.TargetResults.Should().BeEmpty();
        result.NextChecks.Should().ContainSingle(check => check.Contains("ports", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task RunAsync_ServiceAccessWithMixedInvalidUserPorts_DoesNotProbeValidSubset()
    {
        var targetCollector = new FakeTargetConnectivityCollector(target => HealthyProbe(target, [443]));
        var engine = new ScenarioEngine(
            targetCollector,
            new FakeDomainCollector(new DomainInfo()));
        var input = new WorkflowInput
        {
            ServiceTarget = "service.contoso.local",
            ServicePorts = "443, 70000"
        };

        var result = await engine.RunAsync(
            ScenarioWorkflow.ServiceAccess,
            new NetworkDiagnosisResult(),
            new NetworkProfile { ImportantPorts = [3389] },
            input);

        targetCollector.ProbedHosts.Should().BeEmpty();
        result.Title.Should().Be("Targeted test needs valid service ports");
        result.Severity.Should().Be("Warning");
        result.TargetResults.Should().BeEmpty();
    }

    [Fact]
    public async Task RunAsync_ServiceAccessWithTooManyPorts_CapsPortsAtProductionLimit()
    {
        DiagnosticTarget? probedTarget = null;
        var targetCollector = new FakeTargetConnectivityCollector(target =>
        {
            probedTarget = target;
            return HealthyProbe(target, target.Ports);
        });
        var engine = new ScenarioEngine(
            targetCollector,
            new FakeDomainCollector(new DomainInfo()));
        var input = new WorkflowInput
        {
            ServiceTarget = "service.contoso.local",
            ServicePorts = string.Join(",", Enumerable.Range(1, DiagnosticConstants.MaxTargetedTestPorts + 5))
        };

        await engine.RunAsync(
            ScenarioWorkflow.ServiceAccess,
            new NetworkDiagnosisResult(),
            new NetworkProfile(),
            input);

        probedTarget.Should().NotBeNull();
        probedTarget!.Ports.Should().HaveCount(DiagnosticConstants.MaxTargetedTestPorts);
    }

    [Fact]
    public async Task RunIndependentAsync_ServiceAccess_DoesNotClaimQuickDiagnosisBaseline()
    {
        var targetCollector = new FakeTargetConnectivityCollector(target => HealthyProbe(target, target.Ports));
        var engine = new ScenarioEngine(
            targetCollector,
            new FakeDomainCollector(new DomainInfo()));

        var result = await engine.RunIndependentAsync(
            ScenarioWorkflow.ServiceAccess,
            new NetworkProfile(),
            new WorkflowInput
            {
                ServiceTarget = "service.contoso.local",
                ServicePorts = "443"
            });

        result.BaselineSummary.Should().Be("Independent targeted test. Quick Diagnosis baseline was not run.");
        result.Title.Should().Be("Target connectivity looks healthy for tested targets");
        targetCollector.ProbedHosts.Should().ContainSingle("service.contoso.local");
    }

    [Fact]
    public async Task RunAsync_DnsDomainWithManualDcOnWorkgroupPc_StillProbesOverride()
    {
        var targetCollector = new FakeTargetConnectivityCollector(target => HealthyProbe(target, [88, 389, 445]));
        var domainCollector = new FakeDomainCollector(new DomainInfo
        {
            IsDomainJoined = false,
            DomainName = "Not domain joined",
            LogonServer = "Not domain joined"
        });
        var engine = new ScenarioEngine(targetCollector, domainCollector);
        var input = new WorkflowInput { DomainControllerOverride = "dc01.contoso.local" };

        var result = await engine.RunAsync(
            ScenarioWorkflow.DnsDomain,
            new NetworkDiagnosisResult(),
            new NetworkProfile(),
            input);

        targetCollector.ProbedHosts.Should().ContainSingle("dc01.contoso.local");
        result.TargetResults.Should().ContainSingle();
        result.Title.Should().Be("Domain controller is reachable but this PC is not domain joined");
        result.Severity.Should().Be("Warning");
    }

    [Fact]
    public async Task RunAsync_DnsDomainOnWorkgroupPcWithoutTargets_ReturnsConfigurationWarning()
    {
        var targetCollector = new FakeTargetConnectivityCollector(target => HealthyProbe(target, [88, 389, 445]));
        var domainCollector = new FakeDomainCollector(new DomainInfo
        {
            IsDomainJoined = false,
            DomainName = "Not domain joined",
            LogonServer = "Not domain joined"
        });
        var engine = new ScenarioEngine(targetCollector, domainCollector);

        var result = await engine.RunAsync(
            ScenarioWorkflow.DnsDomain,
            new NetworkDiagnosisResult(),
            new NetworkProfile(),
            new WorkflowInput());

        targetCollector.ProbedHosts.Should().BeEmpty();
        result.Title.Should().Be("PC is not domain joined or no domain profile is configured");
        result.Severity.Should().Be("Warning");
    }

    private static TargetProbeResult HealthyProbe(DiagnosticTarget target, IReadOnlyList<int> ports) => new()
    {
        Target = target,
        ResolvedAddress = target.Host,
        DnsStatus = "OK",
        PingStatus = "OK",
        PacketLoss = new PacketLossResult
        {
            Target = target.Host,
            Status = "OK",
            Sent = 4,
            Received = 4,
            LossPercent = 0,
            Details = "0% packet loss (4/4 replies)."
        },
        Ports = ports.Select(port => new PortProbeResult
        {
            Port = port,
            TcpSucceeded = true,
            LatencyMs = 1,
            Details = "TCP port open."
        }).ToList(),
        CollectorStatus = "OK"
    };

    private sealed class FakeTargetConnectivityCollector : TargetConnectivityCollector
    {
        private readonly Func<DiagnosticTarget, TargetProbeResult> _probeFactory;

        public FakeTargetConnectivityCollector(Func<DiagnosticTarget, TargetProbeResult> probeFactory)
            : base(new PowerShellRunner(new NullLogger()))
        {
            _probeFactory = probeFactory;
        }

        public List<string> ProbedHosts { get; } = [];

        public override Task<TargetProbeResult> ProbeTargetAsync(
            DiagnosticTarget target,
            IReadOnlyList<int> defaultPorts,
            CancellationToken cancellationToken = default)
        {
            ProbedHosts.Add(target.Host);
            return Task.FromResult(_probeFactory(target));
        }
    }

    private sealed class FakeDomainCollector : DomainCollector
    {
        private readonly DomainInfo _domain;

        public FakeDomainCollector(DomainInfo domain)
            : base(new PowerShellRunner(new NullLogger()))
        {
            _domain = domain;
        }

        public override Task<DomainInfo> GetDomainInfoAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(_domain);
    }

    private sealed class NullLogger : ILoggingService
    {
        public void Debug(string message) { }
        public void Info(string message) { }
        public void Warning(string message) { }
        public void Error(string message, Exception? exception = null) { }
    }
}
