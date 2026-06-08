using System.Net;
using System.Net.Http;
using System.Text;
using NetScopeDiagnosticCenter.Collectors;
using NetScopeDiagnosticCenter.Collectors.Wifi;
using NetScopeDiagnosticCenter.Core.Models.Wifi;
using NetScopeDiagnosticCenter.Infrastructure;

namespace NetScopeDiagnosticCenter.Tests.Collectors.Wifi;

public sealed class WifiCancellationTests
{
    [Fact]
    public async Task LanScanner_PreCancelledToken_ThrowsOperationCanceled()
    {
        var scanner = new WifiLanScanner(new PowerShellRunner(new NullLogger()));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await FluentActions.Invoking(() => scanner.ScanAsync(cancellation.Token))
            .Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task CapabilityProbe_PreCancelledToken_ThrowsOperationCanceled()
    {
        using var wifiScanner = new StubWifiScanner();
        var probe = new WifiCapabilityProbe(
            new PowerShellRunner(new NullLogger()),
            wifiScanner);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await FluentActions.Invoking(() => probe.ProbeAsync(
                Guid.NewGuid(),
                "Wi-Fi",
                cancellation.Token))
            .Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task PublicIpProbe_CancelledIpLookup_ThrowsOperationCanceled()
    {
        using var cancellation = new CancellationTokenSource();
        using var http = new HttpClient(new CancelsOnRequestHandler(cancellation));
        using var probe = new WifiPublicIpProbe(http);

        await FluentActions.Invoking(() => probe.ProbeAsync(cancellation.Token))
            .Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task PublicIpProbe_CancelledEnrichmentLookup_ThrowsOperationCanceled()
    {
        using var cancellation = new CancellationTokenSource();
        using var http = new HttpClient(new CancelsOnSecondRequestHandler(cancellation));
        using var probe = new WifiPublicIpProbe(http);

        await FluentActions.Invoking(() => probe.ProbeAsync(cancellation.Token))
            .Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task WifiCollector_GenerateWlanReport_PreCancelledToken_ThrowsOperationCanceled()
    {
        var collector = new WifiCollector(new PowerShellRunner(new NullLogger()));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await FluentActions.Invoking(() => collector.GenerateWlanReportAsync(cancellation.Token))
            .Should().ThrowAsync<OperationCanceledException>();
    }

    private sealed class CancelsOnRequestHandler : HttpMessageHandler
    {
        private readonly CancellationTokenSource _cancellation;

        public CancelsOnRequestHandler(CancellationTokenSource cancellation)
        {
            _cancellation = cancellation;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            _cancellation.Cancel();
            return Task.FromCanceled<HttpResponseMessage>(_cancellation.Token);
        }
    }

    private sealed class CancelsOnSecondRequestHandler : HttpMessageHandler
    {
        private readonly CancellationTokenSource _cancellation;
        private int _calls;

        public CancelsOnSecondRequestHandler(CancellationTokenSource cancellation)
        {
            _cancellation = cancellation;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            _calls++;
            if (_calls == 1)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"ip":"203.0.113.10"}""", Encoding.UTF8, "application/json")
                });
            }

            _cancellation.Cancel();
            return Task.FromCanceled<HttpResponseMessage>(_cancellation.Token);
        }
    }

    private sealed class StubWifiScanner : IWifiScanner
    {
        public bool IsAvailable => true;
        public Guid? GetPrimaryAdapterId() => Guid.NewGuid();
        public WifiConnectionDetails? GetCurrentConnection(Guid adapterId) => null;
        public Task<bool> ForceScanAsync(Guid adapterId, CancellationToken cancellationToken) => Task.FromResult(true);
        public IReadOnlyList<WifiAccessPoint> GetVisibleAccessPoints(Guid adapterId) => Array.Empty<WifiAccessPoint>();
        public IReadOnlyList<string> GetSavedProfiles(Guid adapterId) => Array.Empty<string>();
        public bool ForgetSavedProfile(Guid adapterId, string profileName) => false;
        public int? GetCurrentRssiDbm(Guid adapterId) => null;
        public void Dispose() { }
    }

    private sealed class NullLogger : ILoggingService
    {
        public void Debug(string message) { }
        public void Info(string message) { }
        public void Warning(string message) { }
        public void Error(string message, Exception? exception = null) { }
    }
}
