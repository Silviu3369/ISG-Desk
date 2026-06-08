using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Text;

namespace NetScopeDiagnosticCenter.Collectors.Wifi;

/// <summary>
/// Append-only on-disk history of the live Wi-Fi samples so a tech can answer
/// "what was the signal doing 2 hours ago / before the user rebooted?" - not just the
/// 60-second in-memory window.
///
/// <para>
/// Storage: one CSV per day under
/// <c>%LOCALAPPDATA%\ISG Desk\WifiHistory\wifi-YYYY-MM-DD.csv</c>. Portable by design -
/// LocalApplicationData resolves per-user on any machine, no hardcoded path, no admin.
/// Files older than <see cref="RetentionDays"/> are pruned on startup so it can't grow
/// without bound. Rows are buffered and flushed by a background timer (every
/// <see cref="FlushSeconds"/> s) so the 1 Hz UI thread never does disk I/O.
/// </para>
/// </summary>
public sealed class WifiHistoryStore : IDisposable
{
    private const int RetentionDays = 14;
    private const int FlushSeconds = 5;
    private const string Header = "Timestamp,Ssid,Bssid,RssiDbm,RxThroughputMbps,TxThroughputMbps,RxPhyMbps,TxPhyMbps";

    public readonly record struct Row(
        DateTimeOffset Timestamp,
        string? Ssid,
        string? Bssid,
        int? RssiDbm,
        double RxThroughputMbps,
        double TxThroughputMbps,
        double RxPhyMbps,
        double TxPhyMbps);

    private readonly ConcurrentQueue<Row> _pending = new();
    private readonly object _ioLock = new();
    private readonly System.Threading.Timer _flushTimer;
    private bool _disposed;

    /// <summary>Folder the CSV files live in. Created on first construction.</summary>
    public string HistoryDirectory { get; }

    public static string DefaultHistoryDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ISG Desk",
        "WifiHistory");

    public WifiHistoryStore()
        : this(DefaultHistoryDirectory)
    {
    }

    public WifiHistoryStore(string historyDirectory)
    {
        if (string.IsNullOrWhiteSpace(historyDirectory))
            throw new ArgumentException("History directory must be a non-empty path.", nameof(historyDirectory));

        HistoryDirectory = historyDirectory;
        try
        {
            Directory.CreateDirectory(HistoryDirectory);
            PruneOldFiles();
        }
        catch { /* read-only profile / locked-down box - history is best-effort, never fatal */ }

        _flushTimer = new System.Threading.Timer(
            _ => Flush(), null,
            TimeSpan.FromSeconds(FlushSeconds), TimeSpan.FromSeconds(FlushSeconds));
    }

    /// <summary>Queue one sample. Cheap + lock-free; the timer does the actual write.</summary>
    public void Record(Row row)
    {
        if (_disposed) return;
        _pending.Enqueue(row);
    }

    /// <summary>Drain the queue into today's CSV. Safe to call concurrently (serialised).</summary>
    public void Flush()
    {
        if (_pending.IsEmpty) return;

        lock (_ioLock)
        {
            try
            {
                var path = Path.Combine(
                    HistoryDirectory,
                    $"wifi-{DateTimeOffset.Now:yyyy-MM-dd}.csv");
                var newFile = !File.Exists(path);

                var sb = new StringBuilder();
                if (newFile) sb.AppendLine(Header);
                while (_pending.TryDequeue(out var r))
                {
                    sb.Append(r.Timestamp.ToString("O", CultureInfo.InvariantCulture)).Append(',')
                      .Append(Csv(r.Ssid)).Append(',')
                      .Append(Csv(r.Bssid)).Append(',')
                      .Append(r.RssiDbm?.ToString(CultureInfo.InvariantCulture) ?? string.Empty).Append(',')
                      .Append(r.RxThroughputMbps.ToString("0.###", CultureInfo.InvariantCulture)).Append(',')
                      .Append(r.TxThroughputMbps.ToString("0.###", CultureInfo.InvariantCulture)).Append(',')
                      .Append(r.RxPhyMbps.ToString("0.#", CultureInfo.InvariantCulture)).Append(',')
                      .Append(r.TxPhyMbps.ToString("0.#", CultureInfo.InvariantCulture))
                      .Append('\n');
                }
                File.AppendAllText(path, sb.ToString(), Encoding.UTF8);
            }
            catch
            {
                // Disk full / file locked / no profile dir - drop this batch rather than
                // crash the app over a non-essential history log.
            }
        }
    }

    private void PruneOldFiles()
    {
        var cutoff = DateTime.Now.AddDays(-RetentionDays);
        foreach (var f in Directory.EnumerateFiles(HistoryDirectory, "wifi-*.csv"))
        {
            try
            {
                if (File.GetLastWriteTime(f) < cutoff) File.Delete(f);
            }
            catch { /* locked file - leave it, try again next launch */ }
        }
    }

    // Minimal RFC-4180-ish quoting plus spreadsheet formula neutralization.
    // SSIDs are attacker-controlled AP names; prefix formula-like values so opening
    // the CSV in Excel cannot evaluate them as formulas.
    private static string Csv(string? v)
    {
        if (string.IsNullOrEmpty(v)) return string.Empty;
        if (IsSpreadsheetFormula(v)) v = "'" + v;
        if (v.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0) return v;
        return "\"" + v.Replace("\"", "\"\"") + "\"";
    }

    private static bool IsSpreadsheetFormula(string value)
    {
        var trimmed = value.TrimStart();
        return trimmed.Length > 0 && trimmed[0] is '=' or '+' or '-' or '@';
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _flushTimer.Dispose();
        Flush();   // final drain so the last few seconds aren't lost on exit
    }
}
