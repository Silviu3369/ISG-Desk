using NetScopeDiagnosticCenter.Core.Models;

namespace NetScopeDiagnosticCenter.Infrastructure;

public sealed class ActivityFeedService
{
    private const int DefaultMaxEntries = 300;
    private readonly object _sync = new();
    private readonly List<ActivityEntry> _entries = [];
    private readonly int _maxEntries;

    public ActivityFeedService(int maxEntries = DefaultMaxEntries)
    {
        _maxEntries = Math.Max(50, maxEntries);
    }

    public event EventHandler<ActivityEntry>? EntryAdded;

    public IReadOnlyList<ActivityEntry> Entries
    {
        get
        {
            lock (_sync)
            {
                return _entries.ToList();
            }
        }
    }

    public ActivityEntry Add(
        ActivitySourceModule sourceModule,
        ActivityStatus status,
        string stepName,
        string message)
    {
        var entry = new ActivityEntry
        {
            Timestamp = DateTimeOffset.Now,
            SourceModule = sourceModule,
            Status = status,
            StepName = Sanitize(stepName),
            Message = Sanitize(message),
            Severity = MapSeverity(status)
        };

        lock (_sync)
        {
            _entries.Add(entry);
            TrimEntries();
        }

        EntryAdded?.Invoke(this, entry);
        return entry;
    }

    public void Clear(ActivitySourceModule? sourceModule = null)
    {
        lock (_sync)
        {
            if (sourceModule is null)
            {
                _entries.Clear();
                return;
            }

            _entries.RemoveAll(item => item.SourceModule == sourceModule.Value);
        }
    }

    private void TrimEntries()
    {
        if (_entries.Count <= _maxEntries)
        {
            return;
        }

        _entries.RemoveRange(0, _entries.Count - _maxEntries);
    }

    private static string Sanitize(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var sanitized = value;
        foreach (var marker in SensitiveMarkers)
        {
            sanitized = MaskAssignment(sanitized, marker);
        }

        return sanitized.Trim();
    }

    private static string MaskAssignment(string value, string marker)
    {
        var index = value.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        while (index >= 0)
        {
            var start = index + marker.Length;
            while (start < value.Length && (value[start] == ' ' || value[start] == ':' || value[start] == '='))
            {
                start++;
            }

            var end = start;
            while (end < value.Length && !char.IsWhiteSpace(value[end]) && value[end] is not ';' and not ',')
            {
                end++;
            }

            if (end > start)
            {
                value = value.Remove(start, end - start).Insert(start, "********");
            }

            index = value.IndexOf(marker, start + 8, StringComparison.OrdinalIgnoreCase);
        }

        return value;
    }

    private static string MapSeverity(ActivityStatus status) => status switch
    {
        ActivityStatus.Success => "OK",
        ActivityStatus.Warning => "Warning",
        ActivityStatus.Error => "Critical",
        ActivityStatus.Skipped => "Unknown",
        ActivityStatus.Running => "Info",
        ActivityStatus.Pending => "Info",
        _ => "Info"
    };

    private static readonly string[] SensitiveMarkers =
    [
        "password",
        "community",
        "credential",
        "secret",
        "token",
        "authpassword",
        "privacypassword",
        "snmpcommunity"
    ];
}
