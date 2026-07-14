#Requires -Version 5.1
<#
.SYNOPSIS
  Build (if needed) and launch Exo from Windows PowerShell 5.1 or PowerShell 7.

.EXAMPLE
  pwsh -File .\Run-Exo.ps1
  .\Run-Exo.ps1 -NoBuild
#>
param(
    [switch]$NoBuild,
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug'
)

$ErrorActionPreference = 'Stop'
$Root = $PSScriptRoot
$Project = Join-Path $Root 'Exo\Exo.csproj'

function Get-ExoExe {
    $candidates = @(
        (Join-Path $Root "Exo\bin\x64\$Configuration\net8.0-windows10.0.19041.0\win-x64\Exo.exe"),
        (Join-Path $Root "Exo\bin\x64\$Configuration\net8.0-windows10.0.19041.0\Exo.exe"),
        (Join-Path $Root "Exo\bin\$Configuration\net8.0-windows10.0.19041.0\win-x64\Exo.exe"),
        (Join-Path $Root "Exo\bin\$Configuration\net8.0-windows10.0.19041.0\Exo.exe")
    )
    foreach ($c in $candidates) {
        if (Test-Path -LiteralPath $c) { return $c }
    }
    return $null
}

Write-Host ''
Write-Host "  Exo launcher  -  PowerShell $($PSVersionTable.PSVersion)" -ForegroundColor Cyan
Write-Host ''

if (-not $NoBuild) {
    Write-Host '[*] Building Exo (x64 / win-x64)...' -ForegroundColor DarkGray
    & dotnet build $Project -c $Configuration
    if ($LASTEXITCODE -ne 0) { throw "Build failed (exit $LASTEXITCODE)" }
}

$exe = Get-ExoExe
if (-not $exe) {
    Write-Host '[-] Exo.exe not found. Building once more...' -ForegroundColor Yellow
    & dotnet build $Project -c $Configuration
    $exe = Get-ExoExe
}
if (-not $exe) {
    throw 'Could not locate Exo.exe after build. Check the project built successfully.'
}

Write-Host "[+] Starting $exe" -ForegroundColor Green
Start-Process -FilePath $exe -WorkingDirectory (Split-Path $exe -Parent)
