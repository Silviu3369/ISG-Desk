using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace NetScopeDiagnosticCenter.UI;

public sealed class RelayCommand : ICommand
{
    private readonly Action<object?> _execute;
    private readonly Predicate<object?>? _canExecute;

    public RelayCommand(Action<object?> execute, Predicate<object?>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;

    public void Execute(object? parameter) => _execute(parameter);

    /// <summary>
    /// Thread-safe: marshals onto the WPF Dispatcher if invoked off the UI thread (e.g.
    /// from an async scan continuation that resumed on the thread pool). Mirrors
    /// <see cref="AsyncRelayCommand.RaiseCanExecuteChanged"/> so command-state refreshes
    /// never throw the "calling thread cannot access this object" exception.
    /// </summary>
    public void RaiseCanExecuteChanged()
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
            return;
        }
        dispatcher.BeginInvoke(DispatcherPriority.Normal,
            new Action(() => CanExecuteChanged?.Invoke(this, EventArgs.Empty)));
    }
}
