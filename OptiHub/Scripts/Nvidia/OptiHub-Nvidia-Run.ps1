# OptiHub non-interactive NVIDIA apply
param(
    [switch]$NonInteractive,
    [switch]$Gsync,
    [string]$Series = '',
    [switch]$SkipApp,
    [switch]$SkipProfile
)

$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
$Optimizer = Join-Path $Root 'Nvidia-Optimizer.ps1'
if (-not (Test-Path $Optimizer)) { throw "Missing Nvidia-Optimizer.ps1 in $Root" }

$argList = @('-NonInteractive')
if ($Gsync) { $argList += '-Gsync' }
if ($Series) { $argList += @('-Series', $Series) }
if ($SkipApp) { $argList += '-SkipApp' }
if ($SkipProfile) { $argList += '-SkipProfile' }

& $Optimizer @argList 2>&1 | ForEach-Object { Write-Output "$_" }
exit $LASTEXITCODE
