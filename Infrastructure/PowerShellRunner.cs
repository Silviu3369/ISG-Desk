using System.Diagnostics;
using System.IO;
using System.Text;

namespace NetScopeDiagnosticCenter.Infrastructure;

/// <summary>
/// Process-based PowerShell script runner. Each call spawns <c>powershell.exe</c>
/// with the script base64-encoded, isolating state per call.
/// </summary>
/// <remarks>
/// We intentionally do NOT pool <c>Runspace</c> instances from <c>System.Management.Automation</c>:
/// (1) requires Microsoft.PowerShell.SDK (~150 MB) which is excessive for our needs;
/// (2) process isolation prevents state leakage between collectors;
/// (3) per-call startup overhead (~300 ms) is acceptable given collectors are I/O-bound
/// and run sequentially within a diagnosis. Revisit this trade-off only if a profiled
/// diagnostic operation is dominated by PS process startup.
/// </remarks>
public sealed class PowerShellRunner : IPowerShellRunner
{
    private readonly ILoggingService _loggingService;

    public PowerShellRunner(ILoggingService loggingService)
    {
        _loggingService = loggingService;
    }

    public async Task<PowerShellExecutionResult> RunJsonScriptAsync(
        string script,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var encodedCommand = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        using var process = new Process();
        var startInfo = new ProcessStartInfo
        {
            FileName = ResolveWindowsPowerShellPath(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-EncodedCommand");
        startInfo.ArgumentList.Add(encodedCommand);
        process.StartInfo = startInfo;

        try
        {
            process.Start();

            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            var exitTask = process.WaitForExitAsync(cancellationToken);
            var completedTask = await Task.WhenAny(exitTask, Task.Delay(timeout, cancellationToken));

            if (completedTask != exitTask)
            {
                TryKill(process);
                if (cancellationToken.IsCancellationRequested)
                {
                    throw new OperationCanceledException(cancellationToken);
                }

                return new PowerShellExecutionResult
                {
                    Success = false,
                    TimedOut = true,
                    ExitCode = -1,
                    Error = $"PowerShell collector timed out after {timeout.TotalSeconds:N0} seconds."
                };
            }

            var output = await outputTask;
            var error = await errorTask;
            return new PowerShellExecutionResult
            {
                Success = process.ExitCode == 0,
                ExitCode = process.ExitCode,
                Output = output.Trim(),
                Error = error.Trim()
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            throw;
        }
        catch (Exception ex)
        {
            _loggingService.Error("PowerShell runner failed.", ex);
            return new PowerShellExecutionResult
            {
                Success = false,
                ExitCode = -1,
                Error = ex.Message
            };
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Best effort cleanup after timeout.
        }
    }

    private static string ResolveWindowsPowerShellPath()
    {
        var systemDirectory = Environment.SystemDirectory;
        if (!string.IsNullOrWhiteSpace(systemDirectory))
        {
            var fullPath = Path.Combine(systemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
            if (File.Exists(fullPath))
            {
                return fullPath;
            }
        }

        return "powershell.exe";
    }

    /// <summary>
    /// Probes PowerShell availability with a 5-second trivial script. Logs a warning
    /// on failure but never throws.
    /// </summary>
    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var probe = await RunJsonScriptAsync(
                "Write-Output 'ok'",
                TimeSpan.FromSeconds(5),
                cancellationToken);

            if (!probe.Success)
            {
                _loggingService.Warning(
                    $"PowerShell probe failed. ExitCode={probe.ExitCode}; TimedOut={probe.TimedOut}; Error={probe.Error}");
                return false;
            }

            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            _loggingService.Error("PowerShell availability probe threw unexpectedly.", ex);
            return false;
        }
    }
}
