using NetScopeDiagnosticCenter.Core.Models;
using NetScopeDiagnosticCenter.Infrastructure;
using NetScopeDiagnosticCenter.UI.ViewModels;

namespace NetScopeDiagnosticCenter.Tests.UI.ViewModels;

public class ActivityViewModelTests
{
    private sealed class FakeHost : ActivityViewModel.IHost
    {
        public List<string> StatusMessages { get; } = [];
        public void NotifyStatus(string message) => StatusMessages.Add(message);
    }

    private static (ActivityViewModel vm, ActivityFeedService feed, FakeHost host) Build()
    {
        var feed = new ActivityFeedService();
        var host = new FakeHost();
        var vm = new ActivityViewModel(feed, host);
        return (vm, feed, host);
    }

    [Fact]
    public void Construction_StartsWithEmptyCollections()
    {
        var (vm, _, _) = Build();
        vm.Entries.Should().BeEmpty();
    }

    [Fact]
    public void EntryAdded_AppendsToEntries()
    {
        var (vm, feed, _) = Build();

        feed.Add(ActivitySourceModule.QuickDiagnosis, ActivityStatus.Info, "step", "msg");

        vm.Entries.Should().HaveCount(1);
        vm.Entries[0].StepName.Should().Be("step");
        vm.Entries[0].Message.Should().Be("msg");
    }

    [Fact]
    public void EntryAdded_RespectsMaxLiveEntriesCap()
    {
        var (vm, feed, _) = Build();
        // Push a few more than the live VM cap (300). 320 chosen to confirm trimming.
        for (var i = 0; i < 320; i++)
        {
            feed.Add(ActivitySourceModule.QuickDiagnosis, ActivityStatus.Info, $"step-{i}", $"msg-{i}");
        }

        vm.Entries.Should().HaveCount(300);
        // The oldest entry should have been trimmed.
        vm.Entries[0].StepName.Should().NotBe("step-0");
    }

    [Fact]
    public void Clear_EmptiesCollectionsAndNotifiesHost()
    {
        var (vm, feed, host) = Build();
        feed.Add(ActivitySourceModule.QuickDiagnosis, ActivityStatus.Info, "s", "m");

        vm.ClearActivityFeed();

        vm.Entries.Should().BeEmpty();
        host.StatusMessages.Should().Contain(s => s.Contains("Activity monitor cleared"));
    }

    [Fact]
    public void ClearActivityFeedCommand_ExecutesClear()
    {
        var (vm, feed, host) = Build();
        feed.Add(ActivitySourceModule.QuickDiagnosis, ActivityStatus.Info, "s", "m");

        vm.ClearActivityFeedCommand.Execute(null);

        vm.Entries.Should().BeEmpty();
        host.StatusMessages.Should().NotBeEmpty();
    }

    [Fact]
    public void Detach_StopsReceivingNewEntries()
    {
        var (vm, feed, _) = Build();
        feed.Add(ActivitySourceModule.QuickDiagnosis, ActivityStatus.Info, "before-detach", "m");
        vm.Entries.Should().HaveCount(1);

        vm.Detach();
        feed.Add(ActivitySourceModule.QuickDiagnosis, ActivityStatus.Info, "after-detach", "m");

        // The entry was added to the feed but the VM should no longer be subscribed.
        vm.Entries.Should().HaveCount(1);
        feed.Entries.Should().HaveCountGreaterThanOrEqualTo(2);
    }

    [Fact]
    public void Constructor_NullArguments_Throw()
    {
        FluentActions.Invoking(() => new ActivityViewModel(null!, new FakeHost()))
            .Should().Throw<ArgumentNullException>();

        FluentActions.Invoking(() => new ActivityViewModel(new ActivityFeedService(), null!))
            .Should().Throw<ArgumentNullException>();
    }
}
