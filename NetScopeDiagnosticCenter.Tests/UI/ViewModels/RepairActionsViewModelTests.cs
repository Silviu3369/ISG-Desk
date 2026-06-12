using NetScopeDiagnosticCenter.Collectors;
using NetScopeDiagnosticCenter.Core.Models;
using NetScopeDiagnosticCenter.Infrastructure;
using NetScopeDiagnosticCenter.UI;
using NetScopeDiagnosticCenter.UI.ViewModels;

namespace NetScopeDiagnosticCenter.Tests.UI.ViewModels;

public class RepairActionsViewModelTests
{
    private sealed class FakeHost : RepairActionsViewModel.IHost
    {
        public List<string> Statuses { get; } = [];
        public string? ActiveAdapterName { get; set; }

        public void NotifyStatus(string message) => Statuses.Add(message);
        public void NotifyBusy(bool busy) { }
    }

    private sealed class FakeRepairService : NetworkRepairService
    {
        public RepairActionResult NextResult { get; set; } = new()
        {
            ActionName = "Flush DNS cache",
            Success = true,
            Summary = "DNS client cache cleared."
        };

        public List<string> Invocations { get; } = [];

        public FakeRepairService()
            : base(new PowerShellRunner(new NullLogger()))
        {
        }

        public override Task<RepairActionResult> FlushDnsAsync(CancellationToken cancellationToken = default)
        {
            Invocations.Add("flush");
            return Task.FromResult(NextResult);
        }

        public override Task<RepairActionResult> RenewDhcpLeaseAsync(CancellationToken cancellationToken = default)
        {
            Invocations.Add("renew");
            return Task.FromResult(NextResult);
        }

        public override Task<RepairActionResult> ResetWinsockAsync(CancellationToken cancellationToken = default)
        {
            Invocations.Add("winsock");
            return Task.FromResult(NextResult);
        }

        public override Task<RepairActionResult> RestartAdapterAsync(string adapterName, CancellationToken cancellationToken = default)
        {
            Invocations.Add($"adapter:{adapterName}");
            return Task.FromResult(NextResult);
        }
    }

    private sealed class NullLogger : ILoggingService
    {
        public void Debug(string message) { }
        public void Info(string message) { }
        public void Warning(string message) { }
        public void Error(string message, Exception? exception = null) { }
    }

    private static RepairActionsViewModel Build(
        FakeRepairService service,
        FakeHost host,
        bool confirmAnswer = true,
        List<string>? confirmations = null) =>
        new(service, new NullLogger(), host, (title, _) =>
        {
            confirmations?.Add(title);
            return confirmAnswer;
        });

    [Fact]
    public async Task FlushDns_RunsWithoutConfirmation_AndPublishesResult()
    {
        var service = new FakeRepairService();
        var host = new FakeHost();
        var confirmations = new List<string>();
        var vm = Build(service, host, confirmations: confirmations);

        await ((AsyncRelayCommand)vm.FlushDnsCommand).ExecuteAsync(null);

        confirmations.Should().BeEmpty("flushing the DNS cache is harmless and must not prompt");
        service.Invocations.Should().ContainSingle().Which.Should().Be("flush");
        vm.HasLastResult.Should().BeTrue();
        vm.LastResult!.Severity.Should().Be("OK");
        host.Statuses.Should().Contain(s => s.Contains("DNS client cache cleared"));
        host.Statuses.Should().Contain(s => s.Contains("Re-run Quick Diagnosis"));
    }

    [Fact]
    public async Task RenewDhcp_DeclinedConfirmation_DoesNotRun()
    {
        var service = new FakeRepairService();
        var host = new FakeHost();
        var vm = Build(service, host, confirmAnswer: false);

        await ((AsyncRelayCommand)vm.RenewDhcpCommand).ExecuteAsync(null);

        service.Invocations.Should().BeEmpty();
        vm.HasLastResult.Should().BeFalse();
        host.Statuses.Should().Contain(s => s.Contains("cancelled"));
    }

    [Fact]
    public async Task WinsockReset_WithRestartRequired_PublishesRestartNote()
    {
        var service = new FakeRepairService
        {
            NextResult = new RepairActionResult
            {
                ActionName = "Reset Winsock",
                Success = true,
                Summary = "Winsock catalog reset.",
                RestartRequired = true
            }
        };
        var host = new FakeHost();
        var vm = Build(service, host);

        await ((AsyncRelayCommand)vm.ResetWinsockCommand).ExecuteAsync(null);

        vm.LastResult!.Severity.Should().Be("Warning", "successful but restart-pending repairs deserve an amber flag");
        host.Statuses.Should().Contain(s => s.Contains("restart required"));
    }

    [Fact]
    public void RestartAdapter_CannotExecute_WithoutKnownAdapter()
    {
        var host = new FakeHost { ActiveAdapterName = null };
        var vm = Build(new FakeRepairService(), host);

        vm.RestartAdapterCommand.CanExecute(null).Should().BeFalse();

        host.ActiveAdapterName = "Wi-Fi";
        vm.NotifyDiagnosisContextChanged();

        vm.RestartAdapterCommand.CanExecute(null).Should().BeTrue();
        vm.RestartAdapterButtonText.Should().Contain("Wi-Fi");
    }

    [Fact]
    public async Task FailedAction_SetsCriticalSeverity()
    {
        var service = new FakeRepairService
        {
            NextResult = new RepairActionResult
            {
                ActionName = "Renew DHCP lease",
                Success = false,
                Summary = "ipconfig /renew reported an error."
            }
        };
        var host = new FakeHost();
        var vm = Build(service, host);

        await ((AsyncRelayCommand)vm.RenewDhcpCommand).ExecuteAsync(null);

        vm.LastResult!.Severity.Should().Be("Critical");
    }

    [Theory]
    [InlineData(true, false, "OK")]
    [InlineData(true, true, "Warning")]
    [InlineData(false, false, "Critical")]
    [InlineData(false, true, "Critical")]
    public void RepairActionResult_SeverityMatrix(bool success, bool restartRequired, string expected)
    {
        new RepairActionResult { Success = success, RestartRequired = restartRequired }
            .Severity.Should().Be(expected);
    }
}
