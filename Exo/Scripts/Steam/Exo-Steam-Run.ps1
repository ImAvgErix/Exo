# Exo Steam Optimizer runner - progress markers for WinUI host.
param(
    [switch]$Quick,
    [switch]$NonInteractive,
    [switch]$Repair
)

$ErrorActionPreference = 'Stop'
# Shared Wave-2 libs (PS7 assert, log, no Exo background footprint).
$__exoScriptsRoot = Split-Path -Parent $PSScriptRoot
if (-not $PSScriptRoot) { $__exoScriptsRoot = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path) }
$__exoCommon = Join-Path $__exoScriptsRoot 'lib\Exo.Common.ps1'
$__exoNoBg = Join-Path $__exoScriptsRoot 'lib\Exo.NoBackground.ps1'
if (Test-Path -LiteralPath $__exoCommon) { . $__exoCommon; Assert-ExoPwsh7; [void](Initialize-ExoRunLog -Module 'Steam') }
elseif ($PSVersionTable.PSEdition -ne 'Core' -or [int]$PSVersionTable.PSVersion.Major -lt 7) {
    throw 'Exo-Steam-Run requires PowerShell 7. Install it with: winget install Microsoft.PowerShell'
}
if (Test-Path -LiteralPath $__exoNoBg) { . $__exoNoBg; [void](Unregister-ExoBackground -Quiet) }
# Wave-3 thin stage vocabulary (Detect = Apply contracts).
$__steamBoot = Join-Path $PSScriptRoot 'lib\Steam.Bootstrap.ps1'
if (-not $PSScriptRoot) { $__steamBoot = Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) 'lib\Steam.Bootstrap.ps1' }
if (Test-Path -LiteralPath $__steamBoot) { . $__steamBoot }
$env:EXO = '1'
$env:DISCOPT_NONINTERACTIVE = '1'

function Write-HubProgress([int]$Percent, [string]$Status) {
    $p = [Math]::Max(0, [Math]::Min(100, $Percent))
    $line = "EXO_PROGRESS:$p|$Status"
    Write-Output $line
    if ($env:EXO_LOG) {
        try { Add-Content -LiteralPath $env:EXO_LOG -Value $line -Encoding UTF8 -ErrorAction SilentlyContinue } catch { }
    }
}

$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
$Optimizer = Join-Path $Root 'Steam-Optimizer.ps1'
if (-not (Test-Path $Optimizer)) {
    Write-Output "[-] Steam-Optimizer.ps1 missing: $Root"
    Write-HubProgress 100 'Missing optimizer script'
    exit 1
}

Write-HubProgress 4 'Starting Steam Optimizer...'

# Reapply is intentionally a full maximum-performance pass. Quick mode remains
# available only when explicitly requested by a script caller.
if (-not $Quick -and -not $Repair) {
    Write-Output '[*] Full aggressive apply mode'
    Write-HubProgress 10 'Full performance pass'
}

$runArgs = @()
if ($Quick) { $runArgs += '-Quick' }
if ($Repair) { $runArgs += '-Repair' }
$runArgs += '-NonInteractive'
$runArgs += '-NoLaunch'

try {
    & $Optimizer @runArgs 2>&1 | ForEach-Object {
        $line = "$_"
        if ([string]::IsNullOrWhiteSpace($line)) { return }
        # Elevated Exo runs poll EXO_LOG; non-elevated reads stdout.
        # Always emit progress on both channels so the UI bar keeps updating.
        Write-Output $line
    }
    $code = if ($null -ne $LASTEXITCODE) { [int]$LASTEXITCODE } else { 0 }
    if ($code -eq 0) {
        Write-Output '[+] Steam Optimizer finished successfully.'
        Write-HubProgress 100 'Completed successfully'
        exit 0
    }
    Write-Output "[-] Steam Optimizer exited with code $code"
    Write-HubProgress 100 'Finished with errors'
    exit $code
} catch {
    Write-Output "[-] $($_.Exception.Message)"
    Write-HubProgress 100 'Failed'
    exit 1
}
