<#
.SYNOPSIS
    Builds a portable release ZIP of ISG Desk for distribution.

.DESCRIPTION
    Produces a framework-dependent single-file portable build under
    .\publish\ISG-Desk-<version>-portable\ and zips it into ISG-Desk-<version>-portable.zip.

    The output is small (~5-15 MB) but requires .NET 8 Desktop Runtime on the target
    machine. To produce a fully self-contained build (~80 MB, no runtime needed),
    use -SelfContained.

.PARAMETER Version
    Version stamp. Defaults to "1.1.0" (keep in sync with <Version> in the csproj).

.PARAMETER SelfContained
    If specified, embeds the .NET runtime in the output (~80 MB ZIP, no install needed).

.EXAMPLE
    .\build-release.ps1
    Produces ISG-Desk-1.1.0-portable.zip (framework-dependent).

.EXAMPLE
    .\build-release.ps1 -SelfContained
    Produces ISG-Desk-1.1.0-selfcontained.zip (with runtime embedded).
#>

[CmdletBinding()]
param(
    [string]$Version = "1.1.0",
    [switch]$SelfContained
)

$ErrorActionPreference = 'Stop'
$ProjectDir = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $ProjectDir

$suffix = if ($SelfContained) { 'selfcontained' } else { 'portable' }
$outputDir = Join-Path $ProjectDir "publish\ISG-Desk-$Version-$suffix"
$zipFile   = Join-Path $ProjectDir "ISG-Desk-$Version-$suffix.zip"

Write-Host ""
Write-Host "Building ISG Desk $Version ($suffix)" -ForegroundColor Cyan
Write-Host ("=" * 60)

# Clean previous output to avoid stale binaries.
if (Test-Path $outputDir) {
    Remove-Item -Recurse -Force $outputDir
}
if (Test-Path $zipFile) {
    Remove-Item -Force $zipFile
}

$publishArgs = @(
    'publish'
    '.\NetScopeDiagnosticCenter.csproj'
    '-c', 'Release'
    '-r', 'win-x64'
    '-o', $outputDir
    '/p:PublishSingleFile=true'
    '/p:PublishReadyToRun=true'
    '/p:IncludeNativeLibrariesForSelfExtract=true'
    '--nologo'
    '--verbosity', 'quiet'
)

if ($SelfContained) {
    $publishArgs += '/p:SelfContained=true'
    # Halves the on-disk exe (~160 MB -> ~85 MB) at a small first-launch cost.
    $publishArgs += '/p:EnableCompressionInSingleFile=true'
} else {
    $publishArgs += '/p:SelfContained=false'
}

dotnet $publishArgs
if ($LASTEXITCODE -ne 0) {
    Write-Error "Publish failed (exit $LASTEXITCODE)."
    exit 1
}

# Copy README + docs alongside the binary so users have offline reference.
Copy-Item -Path (Join-Path $ProjectDir 'QUICKSTART.md') -Destination $outputDir -Force
Copy-Item -Path (Join-Path $ProjectDir 'USER_GUIDE.md') -Destination $outputDir -Force

$niceExe = Join-Path $outputDir 'ISG Desk.exe'
$legacyExe = Join-Path $outputDir 'NetScopeDiagnosticCenter.exe'

# The project AssemblyName already produces "ISG Desk.exe". Keep this only as a
# compatibility fallback for older build metadata or stale publish outputs.
if (-not (Test-Path $niceExe) -and (Test-Path $legacyExe)) {
    Move-Item -Force -Path $legacyExe -Destination $niceExe
}

# Produce the zip.
Compress-Archive -Path "$outputDir\*" -DestinationPath $zipFile -CompressionLevel Optimal

$sizeMB = [math]::Round((Get-Item $zipFile).Length / 1MB, 1)
Write-Host ""
Write-Host "Done." -ForegroundColor Green
Write-Host "  Output:  $zipFile"
Write-Host "  Size:    ${sizeMB} MB"
Write-Host "  Folder:  $outputDir"
Write-Host ""
if (-not $SelfContained) {
    Write-Host "Note: target machine needs .NET 8 Desktop Runtime." -ForegroundColor Yellow
    Write-Host "  Download: https://dotnet.microsoft.com/download/dotnet/8.0"
}
