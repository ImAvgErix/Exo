# Exo non-interactive NVIDIA apply
param(
    [switch]$NonInteractive,
    [switch]$Gsync,
    [switch]$RawLatency,
    [string]$Series = '',
    [switch]$SkipApp,
    [switch]$SkipProfile,
    [switch]$Experimental,
    # Accepted for WinUI/VM call sites; product path always forces SafePolicy below.
    [switch]$SafePolicy
)

$ErrorActionPreference = 'Stop'
# Shared Wave-2 libs (PS7 assert, log, no Exo background footprint).
$__exoScriptsRoot = Split-Path -Parent $PSScriptRoot
if (-not $PSScriptRoot) { $__exoScriptsRoot = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path) }
$__exoCommon = Join-Path $__exoScriptsRoot 'lib\Exo.Common.ps1'
$__exoNoBg = Join-Path $__exoScriptsRoot 'lib\Exo.NoBackground.ps1'
if (Test-Path -LiteralPath $__exoCommon) { . $__exoCommon; Assert-ExoPwsh7; [void](Initialize-ExoRunLog -Module 'NVIDIA') }
elseif ($PSVersionTable.PSEdition -ne 'Core' -or [int]$PSVersionTable.PSVersion.Major -lt 7) {
    throw 'Exo-Nvidia-Run requires PowerShell 7. Install Preview: winget install Microsoft.PowerShell.Preview'
}
if (Test-Path -LiteralPath $__exoNoBg) { . $__exoNoBg; [void](Unregister-ExoBackground -Quiet) }
$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
if (-not $PSScriptRoot) { $Root = Split-Path -Parent $MyInvocation.MyCommand.Path }
# Wave-3 thin stage vocabulary (Detect = Apply contracts).
$__nvBoot = Join-Path $Root 'lib\Nvidia.Bootstrap.ps1'
if (Test-Path -LiteralPath $__nvBoot) { . $__nvBoot }
$Optimizer = Join-Path $Root 'Nvidia-Optimizer.ps1'
if (-not (Test-Path $Optimizer)) { throw "Missing Nvidia-Optimizer.ps1 in $Root" }

# Hashtable splat = named params. Array splat (@('-NonInteractive')) is positional and
# wrongly binds "-NonInteractive" to -Series (ValidateSet fails / "Finished with errors").
# SafePolicy used to be forced on here, which switched off the whole point of the module:
# NVIDIA App / GFE removal, bloat component stripping, the overlay, the GPU power ceiling and
# display prefs were all skipped, and the App was explicitly *kept* - the Control Panel branch
# would even install it. That is why an NVIDIA app turns up on a machine that never had one.
#
# The two things that genuinely warranted a guard now carry their own: SkipDriver, because
# Phase C owns driver installs from C# and this script must not race it, and SkipAudio,
# because removing HD-audio components can cost DisplayPort/HDMI sound.
$params = @{ NonInteractive = $true; SkipDriver = $true; SkipAudio = $true }
if ($Gsync) { $params['Gsync'] = $true }
if ($RawLatency) { $params['RawLatency'] = $true }
if ($Series) { $params['Series'] = $Series }
if ($SkipApp) { $params['SkipApp'] = $true }
if ($SkipProfile) { $params['SkipProfile'] = $true }
# Experimental forces profile re-import; SafePolicy stays on (no clean-driver path).
if ($Experimental) {
    Write-Output '[*] Experimental NVIDIA apply (force DRS re-import; safe policy retained)'
    $params['Experimental'] = $true
}

& $Optimizer @params 2>&1 | ForEach-Object { Write-Output "$_" }
if ($null -ne $LASTEXITCODE) { exit [int]$LASTEXITCODE }
exit 0
