namespace NetScopeDiagnosticCenter.Core;

/// <summary>
/// Marks a task's eventual fault as observed. For tasks that may be abandoned —
/// a reverse-DNS lookup losing a <see cref="Task.WhenAny(Task[])"/> race, a UDP
/// receive cut off by its client being disposed — the fault would otherwise
/// surface as <c>TaskScheduler.UnobservedTaskException</c> on the finalizer
/// thread and flood the startup-errors log.
/// </summary>
public static class TaskFaultObserver
{
    /// <summary>Attaches a fault observer and returns the same task for fluent use.</summary>
    public static Task<T> Observe<T>(Task<T> task)
    {
        AttachObserver(task);
        return task;
    }

    /// <summary>Attaches a fault observer and returns the same task for fluent use.</summary>
    public static Task Observe(Task task)
    {
        AttachObserver(task);
        return task;
    }

    private static void AttachObserver(Task task)
    {
        // Reading Task.Exception is what counts as "observing" the fault. The
        // continuation is cancelled (not faulted) when the task succeeds, which
        // never re-triggers the unobserved-exception event.
        _ = task.ContinueWith(
            static t => _ = t.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}
