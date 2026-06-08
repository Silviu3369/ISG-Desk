using NetScopeDiagnosticCenter.Collectors;
using NetScopeDiagnosticCenter.Core.Models;
using NetScopeDiagnosticCenter.Infrastructure;

namespace NetScopeDiagnosticCenter.Tests.Collectors;

public sealed class SnmpPrinterCollectorTests
{
    [Fact]
    public async Task IdentifyAsync_InvalidTarget_ReturnsValidationErrorBeforeNetwork()
    {
        var collector = new SnmpPrinterCollector(new SnmpClientService());

        var result = await collector.IdentifyAsync(new SnmpSessionOptions
        {
            Target = "https://example.com/printer",
            Protocol = SnmpProtocolVersion.V2C
        });

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("host name or IP address");
    }

    [Fact]
    public async Task IdentifyAsync_PreCancelledToken_ThrowsOperationCanceled()
    {
        var collector = new SnmpPrinterCollector(new SnmpClientService());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await FluentActions.Invoking(() => collector.IdentifyAsync(
                new SnmpSessionOptions { Target = "192.168.1.50" },
                cancellation.Token))
            .Should().ThrowAsync<OperationCanceledException>();
    }
}
