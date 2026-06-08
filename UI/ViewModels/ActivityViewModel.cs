using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using NetScopeDiagnosticCenter.Core.Models;
using NetScopeDiagnosticCenter.Infrastructure;

namespace NetScopeDiagnosticCenter.UI.ViewModels;

/// <summary>
/// Activity feed ViewModel: bridges <see cref="ActivityFeedService"/> events to the live
/// observable collection used by the sidebar activity preview.
/// </summary>
/// <remarks>
/// Phase 2c migration: receives feed events on background threads and marshals to the
/// WPF Dispatcher to keep <see cref="ObservableCollection{T}"/> mutations thread-safe.
/// In Phase 2d this VM is hosted directly via DataTemplate.
/// </remarks>
public sealed class ActivityViewModel : ObservableObject
{
    /// <summary>Cross-cutting callbacks the host (currently MainViewModel) must satisfy.</summary>
    public interface IHost
    {
        void NotifyStatus(string message);
    }

    private const int MaxLiveEntries = 300;

    private readonly ActivityFeedService _feed;
    private readonly IHost _host;
    private readonly ObservableCollection<ActivityEntry> _entries = [];

    public ActivityViewModel(ActivityFeedService feed, IHost host)
    {
        _feed = feed ?? throw new ArgumentNullException(nameof(feed));
        _host = host ?? throw new ArgumentNullException(nameof(host));

        Entries = new ReadOnlyObservableCollection<ActivityEntry>(_entries);

        _feed.EntryAdded += OnEntryAdded;

        ClearActivityFeedCommand = new RelayCommand(_ => ClearActivityFeed());
    }

    public ReadOnlyObservableCollection<ActivityEntry> Entries { get; }

    /// <summary>Most recent entry — bound by the sidebar status panel for "last activity" display.</summary>
    public ActivityEntry? LastEntry => _entries.Count > 0 ? _entries[^1] : null;

    public ICommand ClearActivityFeedCommand { get; }

    public void ClearActivityFeed()
    {
        _feed.Clear();
        _entries.Clear();
        OnPropertyChanged(nameof(LastEntry));
        _host.NotifyStatus("Activity monitor cleared.");
    }

    /// <summary>
    /// Detaches event handlers when the owner explicitly disposes the activity surface.
    /// </summary>
    public void Detach()
    {
        _feed.EntryAdded -= OnEntryAdded;
    }

    private void OnEntryAdded(object? sender, ActivityEntry entry) => DispatchActivityUpdate(() =>
    {
        _entries.Add(entry);
        while (_entries.Count > MaxLiveEntries)
        {
            _entries.RemoveAt(0);
        }
        OnPropertyChanged(nameof(LastEntry));
    });

    private static void DispatchActivityUpdate(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
            return;
        }

        dispatcher.BeginInvoke(action);
    }
}
