<#
.SYNOPSIS
    Builds CommPanel into a self-contained folder you can copy anywhere.

.DESCRIPTION
    Two flavours:

      .\build.ps1                    Small build (~320 KB). Needs the .NET 8 Desktop
                                     Runtime on the machine that runs it.

      .\build.ps1 -SelfContained     Standalone build (~150 MB folder). Runs on any
                                     Windows 10/11 x64 machine with nothing installed.

    Either way the result is a plain folder: copy it wherever you like and run
    CommPanel.exe. There is no installer and nothing is written outside that folder
    except the optional "start with Windows" registry value.

.PARAMETER SelfContained
    Bundle the .NET runtime so the target machine needs nothing installed.

.PARAMETER OutputPath
    Where to place the built folder. Defaults to .\dist\CommPanel.
#>
[CmdletBinding()]
param(
    [switch]$SelfContained,
    [string]$OutputPath
)

$ErrorActionPreference = 'Stop'

# Resolved here rather than as a parameter default: $PSScriptRoot is not reliably populated
# while parameter defaults are being bound.
$root = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Definition }
if (-not $OutputPath) { $OutputPath = Join-Path $root 'dist\CommPanel' }

$project = Join-Path $root 'CommPanel.csproj'

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw "The .NET SDK is required to build CommPanel. Install it from https://dotnet.microsoft.com/download"
}

Write-Host "Building CommPanel..." -ForegroundColor Cyan

# Refuse to build over a running copy. Without this the clean step deletes some files,
# fails on the locked executable, and leaves a half-populated folder behind.
$targetExe = Join-Path $OutputPath 'CommPanel.exe'
$running = Get-Process CommPanel -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -and $_.Path -eq $targetExe }
if ($running) {
    throw "CommPanel is running from $OutputPath. Exit it first (tray icon > Exit), then build again."
}

# Settings live beside the executable to keep the app portable, which puts them inside the
# folder being rebuilt. Carry them across so a rebuild never costs the user their setup.
$settingsName = 'CommPanel.settings.json'
$settingsPath = Join-Path $OutputPath $settingsName
$savedSettings = $null
if (Test-Path $settingsPath) {
    $savedSettings = Get-Content $settingsPath -Raw
    Write-Host "  preserving existing $settingsName"
}

if (Test-Path $OutputPath) {
    Remove-Item $OutputPath -Recurse -Force
}

$arguments = @(
    'publish', $project,
    '-c', 'Release',
    '-r', 'win-x64',
    '-o', $OutputPath,
    '--nologo'
)

if ($SelfContained) {
    $arguments += '--self-contained', 'true'
    $arguments += '-p:PublishSingleFile=false'
    Write-Host "  mode: self-contained (no .NET runtime needed on the target machine)"
}
else {
    $arguments += '--self-contained', 'false'
    Write-Host "  mode: framework-dependent (needs the .NET 8 Desktop Runtime)"
}

& dotnet @arguments
if ($LASTEXITCODE -ne 0) {
    throw "Build failed with exit code $LASTEXITCODE."
}

if ($savedSettings) {
    Set-Content -Path $settingsPath -Value $savedSettings -NoNewline
}

# The published folder is the deliverable, so the readme travels with it.
$readme = Join-Path $root 'README.md'
if (Test-Path $readme) {
    Copy-Item $readme (Join-Path $OutputPath 'README.md') -Force
}

$size = (Get-ChildItem $OutputPath -Recurse -File | Measure-Object -Property Length -Sum).Sum
Write-Host ""
Write-Host "Done." -ForegroundColor Green
Write-Host ("  folder: {0}" -f $OutputPath)
Write-Host ("  size:   {0:N1} MB" -f ($size / 1MB))
Write-Host ("  run:    {0}" -f (Join-Path $OutputPath 'CommPanel.exe'))
