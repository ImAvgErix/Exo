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

# Hashtable splat = named params. Array splat (@('-NonInteractive')) is positional and
# wrongly binds "-NonInteractive" to -Series (ValidateSet fails / "Finished with errors").
$params = @{ NonInteractive = $true }
if ($Gsync) { $params['Gsync'] = $true }
if ($Series) { $params['Series'] = $Series }
if ($SkipApp) { $params['SkipApp'] = $true }
if ($SkipProfile) { $params['SkipProfile'] = $true }

& $Optimizer @params 2>&1 | ForEach-Object { Write-Output "$_" }
if ($null -ne $LASTEXITCODE) { exit [int]$LASTEXITCODE }
exit 0
