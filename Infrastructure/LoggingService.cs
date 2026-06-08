using System.IO;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace NetScopeDiagnosticCenter.Infrastructure;

/// <summary>
/// Serilog-backed logging service. File-based rolling logs with 7-day retention,
/// configured under AppStorageService.LogFolder.
/// </summary>
/// <remarks>
/// This class preserves the original public surface (Debug/Info/Warning/Error) for
/// backward compatibility with existing call sites. New code should depend on
/// <see cref="ILoggingService"/>.
/// </remarks>
public sealed class LoggingService : ILoggingService, IDisposable
{
    private readonly Logger _logger;

    public LoggingService(AppStorageService storage)
    {
        storage.EnsureFolders();
        var logPath = Path.Combine(storage.LogFolder, "diagnostic-center-.log");
        _logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.File(
                path: logPath,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7,
                fileSizeLimitBytes: 5 * 1024 * 1024,
                rollOnFileSizeLimit: true,
                shared: true,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u4}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();
    }

    public void Debug(string message)
    {
        try { _logger.Write(LogEventLevel.Debug, message); }
        catch { /* never break diagnosis */ }
    }

    public void Info(string message)
    {
        try { _logger.Write(LogEventLevel.Information, message); }
        catch { /* never break diagnosis */ }
    }

    public void Warning(string message)
    {
        try { _logger.Write(LogEventLevel.Warning, message); }
        catch { /* never break diagnosis */ }
    }

    public void Error(string message, Exception? exception = null)
    {
        try
        {
            if (exception is null)
                _logger.Write(LogEventLevel.Error, message);
            else
                _logger.Write(LogEventLevel.Error, exception, message);
        }
        catch { /* never break diagnosis */ }
    }

    public void Dispose()
    {
        try { _logger.Dispose(); } catch { /* swallow */ }
    }
}
