using NetScopeDiagnosticCenter.Collectors.Shared;
using NetScopeDiagnosticCenter.Core.Models;
using NetScopeDiagnosticCenter.Infrastructure;

namespace NetScopeDiagnosticCenter.Collectors;

public class PrinterQueueInstaller : JsonCollectorBase
{
    public PrinterQueueInstaller(PowerShellRunner powerShellRunner) : base(powerShellRunner)
    {
    }

    public virtual async Task<PrinterInstallResult> InstallSharedQueueAsync(
        string connectionName,
        CancellationToken cancellationToken = default)
    {
        if (!PrinterTargetValidator.TryNormalizeSharedQueueConnection(connectionName, out var normalizedConnection, out var validationReason))
        {
            return new PrinterInstallResult
            {
                Success = false,
                Severity = "Warning",
                Verdict = "A valid shared printer connection is required.",
                ConnectionName = connectionName ?? string.Empty,
                Error = validationReason,
                Category = "InvalidInput",
                Evidence = ["Printer installation did not start because the connection name is invalid."]
            };
        }

        // Centralized choke-point: strips control chars (so a multi-line value can't
        // terminate the literal and inject statements), caps length, doubles quotes.
        var safeConnection = PsSingleQuote(normalizedConnection);
        var script = $$"""
$ErrorActionPreference = 'Stop'
$connection = '{{safeConnection}}'
$evidence = New-Object System.Collections.Generic.List[string]
function Add-Evidence($text) { $script:evidence.Add([string]$text) | Out-Null }

if ([string]::IsNullOrWhiteSpace($connection) -or $connection -notmatch '^\\\\[^\\]+\\[^\\]+$') {
    [pscustomobject]@{
        Success = $false
        Severity = 'Warning'
        Verdict = 'A valid shared printer connection is required.'
        ConnectionName = $connection
        PrinterName = ''
        Error = 'Expected format: \\PrintServer\ShareName'
        Category = 'InvalidInput'
        Evidence = @('Printer installation did not start because the connection name is invalid.')
    } | ConvertTo-Json -Depth 5
    exit 0
}

try {
    Add-Evidence "Installing shared printer queue $connection."
    Add-Printer -ConnectionName $connection -ErrorAction Stop
    $installed = @(Get-Printer -ErrorAction SilentlyContinue | Where-Object {
        $_.Name -eq $connection -or $_.ConnectionName -eq $connection
    } | Select-Object -First 1)
    $printerName = if ($installed.Count -gt 0) { [string]$installed[0].Name } else { $connection }
    Add-Evidence "Add-Printer completed for $connection."
    [pscustomobject]@{
        Success = $true
        Severity = 'OK'
        Verdict = 'Printer queue installed successfully.'
        ConnectionName = $connection
        PrinterName = $printerName
        Error = ''
        Category = 'Installed'
        Evidence = @($evidence)
    } | ConvertTo-Json -Depth 5
} catch {
    $message = [string]$_.Exception.Message
    # Map Windows HRESULTs first (locale-stable), then fall back to English keywords.
    # 0x80070005 / 0x80070BCB / 0x00000709 / 0x80070040 are the well-known print errors.
    $category = 'InstallFailed'
    if     ($message -match '0x80070005|denied|access')                                   { $category = 'AccessDenied' }
    elseif ($message -match 'policy|Point and Print|administrator')                       { $category = 'PolicyBlocked' }
    elseif ($message -match '0x80070BCB|cannot find|not found|driver|package')            { $category = 'DriverUnavailable' }
    elseif ($message -match '0x00000709|0x80070040|server|RPC|unreachable|network path')  { $category = 'ServerUnreachable' }
    Add-Evidence "Add-Printer failed: $message"
    [pscustomobject]@{
        Success = $false
        Severity = 'Warning'
        Verdict = 'Printer queue install failed.'
        ConnectionName = $connection
        PrinterName = ''
        Error = $message
        Category = $category
        Evidence = @($evidence)
    } | ConvertTo-Json -Depth 5
}
""";

        var (data, execution) = await RunCollectorWithExecutionAsync<PrinterInstallResult>(
            script,
            TimeSpan.FromSeconds(60),
            cancellationToken);

        return data ?? new PrinterInstallResult
        {
            Success = false,
            Severity = "Warning",
            Verdict = "Printer queue install collector failed.",
            ConnectionName = normalizedConnection,
            Error = execution.Error,
            Category = execution.TimedOut ? "TimedOut" : "CollectorFailed",
            Evidence = [execution.Error]
        };
    }
}
