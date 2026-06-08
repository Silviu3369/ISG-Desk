namespace NetScopeDiagnosticCenter.Infrastructure;

/// <summary>
/// Logging abstraction. Implementations must never throw; logging failures
/// must not break diagnosis flow.
/// </summary>
public interface ILoggingService
{
    void Debug(string message);
    void Info(string message);
    void Warning(string message);
    void Error(string message, Exception? exception = null);
}
