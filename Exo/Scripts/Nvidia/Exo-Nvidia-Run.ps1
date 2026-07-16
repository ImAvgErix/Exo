# Exo non-interactive NVIDIA apply
param(
    [switch]$NonInteractive,
    [switch]$Gsync,
    [string]$Series = '',
    [switch]$SkipApp,
    [switch]$SkipProfile
)

$ErrorActionPreference = 'Stop'
# Shared Wave-2 libs (PS7 assert, log, no Exo background footprint).
$__exoScriptsRoot = Split-Path -Parent $PSScriptRoot
if (-not $PSScriptRoot) { $__exoScriptsRoot = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path) }
$__exoCommon = Join-Path $__exoScriptsRoot 'lib\Exo.Common.ps1'
$__exoNoBg = Join-Path $__exoScriptsRoot 'lib\Exo.NoBackground.ps1'
if (Test-Path -LiteralPath $__exoCommon) { . $__exoCommon; Assert-ExoPwsh7; [void](Initialize-ExoRunLog -Module 'NVIDIA') }
elseif ($PSVersionTable.PSEdition -ne 'Core' -or [int]$PSVersionTable.PSVersion.Major -lt 7) {
    throw 'Exo-Nvidia-Run requires PowerShell 7. Install it with: winget install Microsoft.PowerShell'
}
if (Test-Path -LiteralPath $__exoNoBg) { . $__exoNoBg; [void](Unregister-ExoBackground -Quiet) }
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
