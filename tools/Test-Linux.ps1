#Requires -Version 7
<#
.SYNOPSIS
  Linux / cloud-agent verification gate for Exo.

.DESCRIPTION
  Runs every test that is meaningful without WinUI:
    - repository / script / data integrity
    - Network / Steam / Nvidia / Discord / Ui smokes

  The WinUI app itself cannot build or run on Linux (XamlCompiler.exe is a
  Windows binary). Use Windows CI or a local Windows install for GUI QA.

.EXAMPLE
  pwsh -NoProfile -File ./tools/Test-Linux.ps1
#>
param(
    [switch]$SkipUi,
    [switch]$SkipDiscord
)

$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent $PSScriptRoot
Set-Location $Root

function Invoke-Step([string]$Name, [scriptblock]$Body) {
    Write-Host ""
    Write-Host "=== $Name ===" -ForegroundColor Cyan
    & $Body
    if ($LASTEXITCODE -ne 0 -and $null -ne $LASTEXITCODE) {
        throw "Step failed: $Name (exit $LASTEXITCODE)"
    }
}

$ver = (Get-Content -LiteralPath (Join-Path $Root 'VERSION') -Raw).Trim()
Write-Host "Exo Linux test harness (VERSION=$ver)"
Write-Host "Note: WinUI GUI is Windows-only - this suite covers logic + UI contract smokes."

Invoke-Step 'Test-Repository' {
    & pwsh -NoProfile -File ./tools/Test-Repository.ps1
}

$smokes = @(
    @{ Name = 'Network.Smoke'; Project = 'tools/Network.Smoke' },
    @{ Name = 'Steam.Smoke'; Project = 'tools/Steam.Smoke' },
    @{ Name = 'Nvidia.Smoke'; Project = 'tools/Nvidia.Smoke' },
    @{ Name = 'Brave.Smoke'; Project = 'tools/Brave.Smoke' }
)
if (-not $SkipDiscord) {
    $smokes += @{ Name = 'Discord.Smoke'; Project = 'tools/Discord.Smoke' }
}
if (-not $SkipUi) {
    $smokes += @{ Name = 'Ui.Smoke'; Project = 'tools/Ui.Smoke' }
}

foreach ($s in $smokes) {
    Invoke-Step $s.Name {
        & dotnet run --project $s.Project -c Release --no-launch-profile
    }
}

Write-Host ""
Write-Host "All Linux-verifiable gates passed." -ForegroundColor Green
Write-Host "Still Windows-only: Exo.sln WinUI build, Publish-Exo.ps1, logo ink measure in Ui.Smoke, real Apply/Repair."
