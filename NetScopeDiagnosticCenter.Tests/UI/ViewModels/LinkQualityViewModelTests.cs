using NetScopeDiagnosticCenter.Collectors;
using NetScopeDiagnosticCenter.Core.Models;
using NetScopeDiagnosticCenter.Infrastructure;
using NetScopeDiagnosticCenter.UI;
using NetScopeDiagnosticCenter.UI.ViewModels;

namespace NetScopeDiagnosticCenter.Tests.UI.ViewModels;

public class LinkQualityViewModelTests
{
    private sealed class FakeHost : LinkQualityViewModel.IHost
    {
        public List<string> Statuses { get; } = [];
        public NetworkDiagnosisResult? LastDiagnosis { get; set; }

        public void NotifyStatus(string message) => Statuses.Add(message);
        public void NotifyBusy(bool busy) { }
        public void NotifyDiagnosisChanged() { }
    }

    private sealed class FakeTraceRouteCollector : TraceRouteCollector
    {
        public string? RequestedTarget { get; private set; }

        public FakeTraceRouteCollector()
            : base(new PowerShellRunner(new NullLogger()))
        {
        }

        public override Task<TraceRouteResult> TraceAsync(string target = "1.1.1.1", CancellationToken cancellationToken = default)
        {
            RequestedTarget = target;
            return Task.FromResult(new TraceRouteResult
            {
                Target = target,
                ReachedTarget = true,
                Status = "OK",
                Summary = $"Path to {target} is intact.",
                Hops =
                [
                    new TraceHop { Hop = 1, Address = "192.168.0.1", Scope = "local" },
                    new TraceHop { Hop = 2, Address = "82.77.10.1", Scope = "isp/internet" }
                ]
            });
        }
    }

    private sealed class NullLogger : ILoggingService
    {
        public void Debug(string message) { }
        public void Info(string message) { }
        public void Warning(string message) { }
        public void Error(string message, Exception? exception = null) { }
    }

    [Fact]
    public async Task RunTraceRoute_WithoutCollector_ReportsUnavailable()
    {
        var host = new FakeHost();
        var vm = new LinkQualityViewModel(new NullLogger(), host);

        await vm.RunTraceRouteAsync();

        vm.HasTraceRoute.Should().BeFalse();
        host.Statuses.Should().Contain(s => s.Contains("not available"));
    }

    [Fact]
    public async Task RunTraceRoute_UsesTypedTarget_AndStoresHops()
    {
        var host = new FakeHost();
        var collector = new FakeTraceRouteCollector();
        var vm = new LinkQualityViewModel(new NullLogger(), host, collector)
        {
            LinkQualityPingTarget = "192.168.0.1"
        };

        await vm.RunTraceRouteAsync();

        collector.RequestedTarget.Should().Be("192.168.0.1");
        vm.HasTraceRoute.Should().BeTrue();
        vm.LastTraceRoute!.Hops.Should().HaveCount(2);
        vm.TraceRouteHopCountText.Should().Be("2 hops");
        vm.IsLinkQualityRunning.Should().BeFalse();
        host.Statuses.Should().Contain(s => s.Contains("Traceroute completed"));
    }

    [Fact]
    public async Task RunTraceRoute_EmptyTarget_FallsBackToInternetBeacon()
    {
        var host = new FakeHost();
        var collector = new FakeTraceRouteCollector();
        var vm = new LinkQualityViewModel(new NullLogger(), host, collector);

        await vm.RunTraceRouteAsync();

        collector.RequestedTarget.Should().NotBeNullOrWhiteSpace();
        collector.RequestedTarget.Should().Be(NetScopeDiagnosticCenter.Core.DiagnosticConstants.InternetPingTargets.First());
    }

    [Fact]
    public async Task RunTraceRoute_InvalidTarget_IsRejectedBeforeRunning()
    {
        var host = new FakeHost();
        var collector = new FakeTraceRouteCollector();
        var vm = new LinkQualityViewModel(new NullLogger(), host, collector)
        {
            LinkQualityPingTarget = "bad target!!"
        };

        await vm.RunTraceRouteAsync();

        collector.RequestedTarget.Should().BeNull("invalid hosts must never reach the collector");
        vm.HasTraceRoute.Should().BeFalse();
    }
}
