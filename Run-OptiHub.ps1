#Requires -Version 7.0
<#
.SYNOPSIS
  Build (if needed) and launch OptiHub from PowerShell 7.7+.

.EXAMPLE
  pwsh -File .\Run-OptiHub.ps1
  .\Run-OptiHub.ps1 -NoBuild
#>
param(
    [switch]$NoBuild,
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug'
)

$ErrorActionPreference = 'Stop'
$Root = $PSScriptRoot
$Project = Join-Path $Root 'OptiHub\OptiHub.csproj'

function Get-OptiHubExe {
    $candidates = @(
        (Join-Path $Root "OptiHub\bin\x64\$Configuration\net8.0-windows10.0.19041.0\win-x64\OptiHub.exe"),
        (Join-Path $Root "OptiHub\bin\x64\$Configuration\net8.0-windows10.0.19041.0\OptiHub.exe"),
        (Join-Path $Root "OptiHub\bin\$Configuration\net8.0-windows10.0.19041.0\win-x64\OptiHub.exe"),
        (Join-Path $Root "OptiHub\bin\$Configuration\net8.0-windows10.0.19041.0\OptiHub.exe")
    )
    foreach ($c in $candidates) {
        if (Test-Path -LiteralPath $c) { return $c }
    }
    return $null
}

Write-Host ''
Write-Host "  OptiHub launcher  ·  PowerShell $($PSVersionTable.PSVersion)" -ForegroundColor Cyan
Write-Host ''

if (-not $NoBuild) {
    Write-Host '[*] Building OptiHub (x64 / win-x64)...' -ForegroundColor DarkGray
    & dotnet build $Project -c $Configuration
    if ($LASTEXITCODE -ne 0) { throw "Build failed (exit $LASTEXITCODE)" }
}

$exe = Get-OptiHubExe
if (-not $exe) {
    Write-Host '[-] OptiHub.exe not found. Building once more...' -ForegroundColor Yellow
    & dotnet build $Project -c $Configuration
    $exe = Get-OptiHubExe
}
if (-not $exe) {
    throw 'Could not locate OptiHub.exe after build. Check the project built successfully.'
}

Write-Host "[+] Starting $exe" -ForegroundColor Green
Start-Process -FilePath $exe -WorkingDirectory (Split-Path $exe -Parent)
