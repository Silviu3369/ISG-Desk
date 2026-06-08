using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace NetScopeDiagnosticCenter.Core.Models.Wifi;

/// <summary>
/// One network's RSSI history across the monitoring session. It drives one coloured line
/// in the Signal Over Time graph and one checkbox row in the legend.
/// </summary>
public sealed class WifiSignalTrend : INotifyPropertyChanged
{
    public WifiSignalTrend(
        string bssid,
        string displaySsid,
        bool isCurrentConnection,
        IReadOnlyList<int> rssiHistory,
        string colorHex,
        bool isVisible)
    {
        Bssid = bssid;
        DisplaySsid = displaySsid;
        IsCurrentConnection = isCurrentConnection;
        RssiHistory = rssiHistory;
        ColorHex = colorHex;
        _isVisible = isVisible;
    }

    public string Bssid { get; }
    public string DisplaySsid { get; }
    public bool IsCurrentConnection { get; }
    public IReadOnlyList<int> RssiHistory { get; }

    public string ColorHex { get; }

    private bool _isVisible;
    public bool IsVisible
    {
        get => _isVisible;
        set
        {
            if (_isVisible == value) return;
            _isVisible = value;
            OnPropertyChanged();
        }
    }

    public int? LatestRssi => RssiHistory.Count > 0 ? RssiHistory[^1] : null;

    public string LatestRssiText => LatestRssi is { } r ? $"{r} dBm" : "No sample";

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
