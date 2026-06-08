namespace NetScopeDiagnosticCenter.Infrastructure;

/// <summary>
/// Abstraction over PowerShell script execution. Implementations must always honor
/// the supplied <see cref="CancellationToken"/> and <paramref name="timeout"/>, and must
/// never throw — diagnostic flow continues regardless of PowerShell availability.
/// </summary>
public interface IPowerShellRunner
{
    /// <summary>
    /// Executes a PowerShell script and returns the standard output (typically JSON) along with diagnostics.
    /// </summary>
    /// <param name="script">PowerShell script to execute. Must not rely on user profile state.</param>
    /// <param name="timeout">Maximum execution time. The runner kills the process when exceeded.</param>
    /// <param name="cancellationToken">Cancellation token honored throughout I/O.</param>
    Task<PowerShellExecutionResult> RunJsonScriptAsync(
        string script,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Probes whether PowerShell is available on the host. Use at startup to surface
    /// a warning when <c>powershell.exe</c> is missing or when ExecutionPolicy blocks
    /// scripts even with <c>-ExecutionPolicy Bypass</c> (e.g. constrained Group Policy).
    /// </summary>
    /// <returns><c>true</c> when a trivial probe script completes successfully; <c>false</c> otherwise.</returns>
    Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default);
}
