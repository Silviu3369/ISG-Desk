using NetScopeDiagnosticCenter.Core;

namespace NetScopeDiagnosticCenter.Tests.Core;

public class TaskFaultObserverTests
{
    [Fact]
    public void Observe_Generic_ReturnsSameTaskInstance()
    {
        var task = Task.FromResult(42);
        TaskFaultObserver.Observe(task).Should().BeSameAs(task);
    }

    [Fact]
    public void Observe_NonGeneric_ReturnsSameTaskInstance()
    {
        var task = Task.CompletedTask;
        TaskFaultObserver.Observe(task).Should().BeSameAs(task);
    }

    [Fact]
    public async Task Observe_SuccessfulTask_ResultPassesThrough()
    {
        var result = await TaskFaultObserver.Observe(Task.FromResult("hello"));
        result.Should().Be("hello");
    }

    [Fact]
    public async Task Observe_FaultedTask_OriginalExceptionStillPropagatesToAwaiters()
    {
        var task = Task.FromException<int>(new InvalidOperationException("boom"));

        var act = async () => await TaskFaultObserver.Observe(task);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("boom");
    }

    [Fact]
    public async Task Observe_TaskFaultingLater_DoesNotDisturbTheRaceWinner()
    {
        // The Task.WhenAny pattern this helper exists for: the abandoned task faults
        // after losing the race; Observe must not change what the winner returns.
        var slowFault = Task.Run(async () =>
        {
            await Task.Delay(50);
            throw new TimeoutException("late fault");
        });
        _ = TaskFaultObserver.Observe(slowFault);

        var winner = await Task.WhenAny(slowFault, Task.Delay(1));
        winner.Should().NotBeSameAs(slowFault);

        // Let the abandoned task actually fault so the continuation runs in-test.
        await Task.Delay(120);
        slowFault.IsFaulted.Should().BeTrue();
    }
}
