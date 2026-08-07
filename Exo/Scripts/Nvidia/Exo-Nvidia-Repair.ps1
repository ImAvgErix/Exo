# Exo non-interactive NVIDIA repair (clear marker)
param([switch]$NonInteractive)

$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
$Optimizer = Join-Path $Root 'Nvidia-Optimizer.ps1'
if (-not (Test-Path $Optimizer)) { throw "Missing Nvidia-Optimizer.ps1 in $Root" }

& $Optimizer -Repair -NonInteractive 2>&1 | ForEach-Object { Write-Output "$_" }
exit $LASTEXITCODE
