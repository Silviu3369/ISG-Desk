using NetScopeDiagnosticCenter.Core.Models;
using NetScopeDiagnosticCenter.Infrastructure;

namespace NetScopeDiagnosticCenter.Collectors;

public class LocalPrinterCollector : JsonCollectorBase
{
    public LocalPrinterCollector(PowerShellRunner powerShellRunner) : base(powerShellRunner)
    {
    }

    public virtual async Task<PrinterDiscoveryResult> CollectAsync(CancellationToken cancellationToken = default)
    {
        const string script = """
$ErrorActionPreference = 'Continue'
$evidence = New-Object System.Collections.Generic.List[string]
$printers = @()
$ports = @{}
$defaultPrinterName = ''

function Add-Evidence($text) { $script:evidence.Add([string]$text) | Out-Null }
function Read-Property($object, $name) {
    if ($null -eq $object) { return '' }
    $property = $object.PSObject.Properties[$name]
    if ($null -eq $property -or $null -eq $property.Value) { return '' }
    return [string]$property.Value
}
function Format-PortProtocol($value) {
    if ($null -eq $value -or [string]::IsNullOrWhiteSpace([string]$value)) { return '' }
    switch ([string]$value) {
        '1' { return 'RAW' }
        '2' { return 'LPR' }
        default { return [string]$value }
    }
}

try {
    $spooler = Get-Service -Name Spooler -ErrorAction Stop
    $spoolerStatus = [string]$spooler.Status
    Add-Evidence "Local Print Spooler status: $spoolerStatus."
} catch {
    $spoolerStatus = 'Unknown'
    Add-Evidence "Local Print Spooler status could not be read: $($_.Exception.Message)"
}

try {
    Get-PrinterPort -ErrorAction Stop | ForEach-Object {
        $ports[[string]$_.Name] = $_
    }
    Add-Evidence "Local printer port enumeration returned $($ports.Count) ports."
} catch {
    Add-Evidence "Local printer port enumeration failed: $($_.Exception.Message)"
}

try {
    $defaultPrinter = Get-CimInstance Win32_Printer -ErrorAction Stop |
        Where-Object { $_.Default -eq $true } |
        Select-Object -First 1
    if ($defaultPrinter) {
        $defaultPrinterName = [string]$defaultPrinter.Name
        Add-Evidence "Windows default printer: $defaultPrinterName."
    } else {
        Add-Evidence "Windows did not report a default printer."
    }
} catch {
    Add-Evidence "Windows default printer could not be read: $($_.Exception.Message)"
}

try {
    $printers = @(Get-Printer -ErrorAction Stop | ForEach-Object {
        $port = $ports[[string]$_.PortName]
        $share = Read-Property $_ 'ShareName'
        $computer = Read-Property $_ 'ComputerName'
        $connection = Read-Property $_ 'ConnectionName'
        if ([string]::IsNullOrWhiteSpace($connection) -and -not [string]::IsNullOrWhiteSpace($computer) -and -not [string]::IsNullOrWhiteSpace($share)) {
            $connection = ('\\{0}\{1}' -f $computer, $share)
        }
        $name = [string]$_.Name
        $defaultProperty = Read-Property $_ 'Default'
        $isDefault = if (-not [string]::IsNullOrWhiteSpace($defaultPrinterName)) {
            $name -eq $defaultPrinterName -or $connection -eq $defaultPrinterName
        } else {
            $defaultProperty -eq 'True'
        }

        [pscustomobject]@{
            Name = $name
            ShareName = $share
            DriverName = Read-Property $_ 'DriverName'
            PortName = Read-Property $_ 'PortName'
            PortHostAddress = Read-Property $port 'PrinterHostAddress'
            PortProtocol = Format-PortProtocol (Read-Property $port 'Protocol')
            ConnectionName = $connection
            PrinterStatus = if ($_.PrinterStatus) { [string]$_.PrinterStatus } else { 'Unknown' }
            Location = Read-Property $_ 'Location'
            Comment = Read-Property $_ 'Comment'
            Shared = [bool]$_.Shared
            IsDefault = $isDefault
            Installable = $false
            InstallStatus = ''
            InstallError = ''
        }
    })
    Add-Evidence "Local printer enumeration returned $($printers.Count) printers."
    $verdict = if ($printers.Count -gt 0) { 'Local printers were enumerated.' } else { 'No local printers were returned.' }
    $severity = if ($spoolerStatus -eq 'Running') { 'OK' } else { 'Warning' }
} catch {
    Add-Evidence "Local printer enumeration failed: $($_.Exception.Message)"
    $verdict = 'Local printer enumeration failed.'
    $severity = 'Warning'
}

[pscustomobject]@{
    Mode = 'InstalledPrinters'
    Source = $env:COMPUTERNAME
    Verdict = $verdict
    Severity = $severity
    LocalSpoolerStatus = $spoolerStatus
    MaxHosts = 254
    Evidence = @($evidence)
    Limitations = @('Local printer inventory is read-only and does not install or remove printers.')
    LocalPrinters = @($printers)
    Printers = @()
    ScanResults = @()
} | ConvertTo-Json -Depth 8
""";

        var (data, execution) = await RunCollectorWithExecutionAsync<PrinterDiscoveryResult>(
            script,
            TimeSpan.FromSeconds(25),
            cancellationToken);

        if (data is not null)
        {
            return data;
        }

        return new PrinterDiscoveryResult
        {
            Mode = "InstalledPrinters",
            Source = Environment.MachineName,
            Verdict = "Local printer collector failed.",
            Severity = "Warning",
            MaxHosts = 254,
            Evidence = [execution.Error],
            Limitations = ["Local printer inventory could not be collected."]
        };
    }

    /// <summary>
    /// Sets the given installed queue as the Windows default printer via the CIM
    /// <c>Win32_Printer.SetDefaultPrinter()</c> method (no admin required — affects the
    /// current user). The name flows through <see cref="JsonCollectorBase.PsSingleQuote"/>
    /// and is matched with <c>-eq</c> (exact, not a filter expression) so it cannot inject.
    /// </summary>
    public virtual async Task<PrinterActionResult> SetDefaultPrinterAsync(
        string printerName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(printerName))
        {
            return new PrinterActionResult { Success = false, Message = "No printer selected." };
        }

        var safeName = PsSingleQuote(printerName.Trim());
        var script = $$"""
$ErrorActionPreference = 'Stop'
$name = '{{safeName}}'

function Write-PrinterActionResult($success, $message) {
    [pscustomobject]@{ Success = $success; Message = $message } | ConvertTo-Json -Compress
}

function Get-DefaultPrinterName {
    try {
        $defaultPrinter = Get-CimInstance Win32_Printer -ErrorAction Stop |
            Where-Object { $_.Default -eq $true } |
            Select-Object -First 1
        if ($defaultPrinter) { return [string]$defaultPrinter.Name }
    } catch {
        return ''
    }

    return ''
}

function Test-DefaultPrinter($candidateNames) {
    $currentDefault = Get-DefaultPrinterName
    if ([string]::IsNullOrWhiteSpace($currentDefault)) {
        return $false
    }

    foreach ($candidateName in @($candidateNames)) {
        if (-not [string]::IsNullOrWhiteSpace([string]$candidateName) -and
            $currentDefault.Equals([string]$candidateName, [System.StringComparison]::OrdinalIgnoreCase)) {
            return $true
        }
    }

    return $false
}

try {
    $installedPrinter = $null
    try {
        if (Get-Command Get-Printer -ErrorAction SilentlyContinue) {
            $installedPrinter = Get-Printer -ErrorAction Stop |
                Where-Object {
                    [string]$_.Name -eq $name -or
                    [string]$_.ConnectionName -eq $name
                } |
                Select-Object -First 1
        }
    } catch {
        $installedPrinter = $null
    }

    $printer = Get-CimInstance Win32_Printer -ErrorAction Stop |
        Where-Object {
            [string]$_.Name -eq $name -or
            [string]$_.DeviceID -eq $name
        } |
        Select-Object -First 1

    if (-not $installedPrinter -and -not $printer) {
        Write-PrinterActionResult $false "Printer '$name' was not found among installed queues."
        return
    }

    $verificationNames = @($name)
    if ($installedPrinter) {
        $verificationNames += [string]$installedPrinter.Name
        $verificationNames += [string]$installedPrinter.ConnectionName
    }
    if ($printer) {
        $verificationNames += [string]$printer.Name
        $verificationNames += [string]$printer.DeviceID
    }
    $verificationNames = @($verificationNames |
        Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) } |
        Select-Object -Unique)

    $messages = New-Object System.Collections.Generic.List[string]
    if ($printer) {
        $r = Invoke-CimMethod -InputObject $printer -MethodName SetDefaultPrinter -ErrorAction Stop
        if ($r.ReturnValue -eq 0) {
            $messages.Add('CIM SetDefaultPrinter returned success.') | Out-Null
        } else {
            $messages.Add("CIM SetDefaultPrinter returned code $($r.ReturnValue).") | Out-Null
        }
    } else {
        $messages.Add('CIM printer object was not available; using user-profile fallback.') | Out-Null
    }

    if (Test-DefaultPrinter $verificationNames) {
        Write-PrinterActionResult $true "'$name' is now the default printer."
        return
    }

    try {
        $network = New-Object -ComObject WScript.Network
        $network.SetDefaultPrinter($name)
        $messages.Add('WScript.Network SetDefaultPrinter completed.') | Out-Null
    } catch {
        $messages.Add("WScript.Network SetDefaultPrinter failed: $($_.Exception.Message)") | Out-Null
    }

    if (Test-DefaultPrinter $verificationNames) {
        Write-PrinterActionResult $true "'$name' is now the default printer."
        return
    }

    Write-PrinterActionResult $false "Windows did not confirm '$name' as the default printer. $($messages -join ' ')"
}
catch {
    Write-PrinterActionResult $false "Set default printer failed: $($_.Exception.Message)"
}
""";

        var result = await RunCollectorAsync<PrinterActionResult>(
            script, TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
        return result ?? new PrinterActionResult
        {
            Success = false,
            Message = "Set default printer collector did not return a result.",
        };
    }
}
