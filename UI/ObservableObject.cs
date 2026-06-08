using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Threading;

namespace NetScopeDiagnosticCenter.UI;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    /// <summary>
    /// Fires <see cref="PropertyChanged"/>. Thread-safe: if called from a non-UI thread
    /// (e.g. an async continuation that resumed on the thread pool because a downstream
    /// library used <c>ConfigureAwait(false)</c>), the invocation is marshaled onto the
    /// WPF Dispatcher.
    ///
    /// <para>
    /// Defense-in-depth complement to <c>AsyncRelayCommand.RaiseCanExecuteChanged</c>. Most
    /// WPF data bindings auto-marshal PropertyChanged events, but some don't (notably any
    /// path that touches a DependencyObject in a converter or MultiBinding). This makes
    /// every VM in the app safe to mutate from any thread.
    /// </para>
    /// </summary>
    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            // Already on UI thread, or unit-test scenario where Application is null.
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
            return;
        }
        // Off-thread caller — marshal onto the dispatcher non-blocking.
        var name = propertyName;
        dispatcher.BeginInvoke(DispatcherPriority.DataBind,
            new Action(() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name))));
    }
}
