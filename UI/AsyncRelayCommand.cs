using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace NetScopeDiagnosticCenter.UI;

public sealed class AsyncRelayCommand : ICommand
{
    private readonly Func<object?, Task> _execute;
    private readonly Predicate<object?>? _canExecute;
    private bool _isRunning;

    public AsyncRelayCommand(Func<object?, Task> execute, Predicate<object?>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => !_isRunning && (_canExecute?.Invoke(parameter) ?? true);

    public async void Execute(object? parameter)
    {
        await ExecuteAsync(parameter);
    }

    /// <summary>
    /// Awaitable execution path. Prefer <see cref="Execute"/> from XAML; tests use this
    /// to await completion deterministically.
    /// </summary>
    public async Task ExecuteAsync(object? parameter)
    {
        if (!CanExecute(parameter))
        {
            return;
        }

        _isRunning = true;
        RaiseCanExecuteChanged();
        try
        {
            await _execute(parameter);
        }
        finally
        {
            _isRunning = false;
            RaiseCanExecuteChanged();
        }
    }

    /// <summary>
    /// Raises <see cref="CanExecuteChanged"/>. Thread-safe: if called from a non-UI thread
    /// (e.g. from a property setter inside a Task continuation that resumed on the thread
    /// pool), the invocation is marshaled onto the WPF Dispatcher.
    ///
    /// <para>
    /// Why this matters: WPF subscribes Button.Command and friends to
    /// <see cref="CanExecuteChanged"/>. When the event fires, the button calls
    /// <see cref="ICommand.CanExecute"/> and reads its own Command DependencyProperty —
    /// which throws <c>InvalidOperationException</c> "calling thread cannot access this
    /// object" if the firing thread isn't the UI thread. Crash, dialog, Shutdown(1).
    /// </para>
    ///
    /// <para>
    /// We tried to fix this at the call sites by removing <c>ConfigureAwait(false)</c>,
    /// but that's fragile (any future async edit can reintroduce the bug). Marshaling here
    /// makes the command thread-safe by construction.
    /// </para>
    /// </summary>
    public void RaiseCanExecuteChanged()
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            // Already on UI thread (typical XAML invocation path) — fire synchronously.
            // Also covers the unit-test scenario where Application.Current is null.
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
            return;
        }
        // BeginInvoke is non-blocking; we don't want to wait on the UI thread from here.
        dispatcher.BeginInvoke(DispatcherPriority.Normal,
            new Action(() => CanExecuteChanged?.Invoke(this, EventArgs.Empty)));
    }
}
