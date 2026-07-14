# Exo Discord Optimizer runner
# Wraps Disc-Optimizer.ps1 with live progress markers for the WinUI host.

param(
    [switch]$Quick,
    [switch]$SkipCacheClean,
    [switch]$NoLaunch,
    [switch]$NonInteractive,
    [switch]$SkipDebloat,
    [switch]$SkipEquicord,
    [switch]$SkipOpenAsar,
    [switch]$SkipKernel,
    [switch]$FreshInstall
)

$ErrorActionPreference = 'Stop'
# Hosted by Exo via PowerShell 7 Preview (+ Terminal Preview on the machine).
if ($PSVersionTable.PSEdition -ne 'Core' -or [int]$PSVersionTable.PSVersion.Major -lt 7) {
    throw 'Exo-Discord-Run requires PowerShell 7 Preview. Install Microsoft.PowerShell.Preview.'
}
$env:EXO = '1'
$env:DISCOPT_NONINTERACTIVE = '1'
# Never open Discord from elevated Exo - causes black screens and false boot failures.
$env:EXO_SKIP_BOOT_FLASH = '1'
$env:DISCOPT_SKIP_MANIFEST = '1'
# Force NoLaunch for every Exo-hosted run (UI owns launch).
$NoLaunch = $true
# DiscOpt kernel (aggressive 5s trim + raw input + Above Normal priority) stays ON.
# Only skip when the user/host explicitly passes -SkipKernel.

function Write-HubProgress([int]$Percent, [string]$Status) {
    $p = [Math]::Max(0, [Math]::Min(100, $Percent))
    $line = "EXO_PROGRESS:$p|$Status"
    Write-Output $line
    if ($env:EXO_LOG -and (Test-Path (Split-Path -Parent $env:EXO_LOG) -PathType Container -ErrorAction SilentlyContinue)) {
        try { Add-Content -LiteralPath $env:EXO_LOG -Value $line -Encoding UTF8 -ErrorAction SilentlyContinue } catch { }
    }
}

function Write-HubStep([string]$Msg) {
    Write-Output "[*] $Msg"
}

function Write-HubOk([string]$Msg) {
    Write-Output "[+] $Msg"
}

function Write-HubWarn([string]$Msg) {
    Write-Output "[!] $Msg"
}

function Write-HubErr([string]$Msg) {
    Write-Output "[-] $Msg"
}

function Get-ProgressForLine([string]$Line, [int]$Current) {
    $map = [ordered]@{
        'Closing Discord'                 = @{ P = 22; S = 'Closing Discord...' }
        'Installing Discord'              = @{ P = 28; S = 'Installing Discord...' }
        'Discord up to date'              = @{ P = 32; S = 'Discord is up to date' }
        'first-run'                       = @{ P = 34; S = 'Initializing Discord...' }
        'Debloat'                         = @{ P = 40; S = 'Debloating Discord...' }
        'cache'                           = @{ P = 48; S = 'Cleaning cache...' }
        'Equicord'                        = @{ P = 58; S = 'Applying Equicord...' }
        'OpenASAR'                        = @{ P = 68; S = 'Installing OpenASAR...' }
        'AMOLED'                          = @{ P = 72; S = 'Applying AMOLED theme...' }
        'kernel'                          = @{ P = 78; S = 'Installing DiscOpt kernel...' }
        'Boot check'                      = @{ P = 86; S = 'Verifying Discord still boots...' }
        'Quick boot'                      = @{ P = 86; S = 'Quick verify (no Discord flash)...' }
        'Windows'                         = @{ P = 92; S = 'Applying Windows tweaks...' }
        'Start menu'                      = @{ P = 94; S = 'Refreshing Start menu shortcut...' }
        'DONE'                            = @{ P = 100; S = 'Completed successfully' }
        'BlockKrisp'                      = @{ P = 96; S = 'Finishing checks...' }
        'everything applied successfully' = @{ P = 100; S = 'Completed successfully' }
        'finished successfully'           = @{ P = 99; S = 'Almost done...' }
        'Disc Optimizer finished'         = @{ P = 99; S = 'Wrapping up...' }
        'Rewriting Discord resources'     = @{ P = 57; S = 'Repairing Equicord loader...' }
    }

    foreach ($key in $map.Keys) {
        if ($Line -match [regex]::Escape($key)) {
            $hit = $map[$key]
            if ([int]$hit.P -gt $Current) {
                return @{ Percent = [int]$hit.P; Status = [string]$hit.S }
            }
            return $null
        }
    }

    if ($Line -match '^\[\*' -and $Current -lt 94) {
        $next = [Math]::Min(94, $Current + 2)
        $status = ($Line -replace '^\[\*\]\s*', '').Trim()
        if ([string]::IsNullOrWhiteSpace($status)) { $status = 'Working...' }
        return @{ Percent = $next; Status = $status }
    }

    return $null
}

$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
$Optimizer = Join-Path $Root 'Disc-Optimizer.ps1'

if (-not (Test-Path $Optimizer)) {
    Write-HubErr "Disc-Optimizer.ps1 not found beside this script: $Root"
    Write-HubProgress 100 'Missing optimizer script'
    exit 1
}

Write-HubProgress 3 'Starting...'

if (-not $Quick) {
    Write-HubStep 'Full aggressive apply mode'
    Write-HubProgress 10 'Full performance pass'
}

Write-HubProgress 14 'Preparing Disc Optimizer...'

$runArgs = @()
if ($Quick) { $runArgs += '-Quick' }
if ($SkipCacheClean) { $runArgs += '-SkipCacheClean' }
# Always non-interactive under Exo so Disc-Optimizer never waits on a keypress.
$NoLaunch = $true
$runArgs += '-NoLaunch'
$runArgs += '-SkipManifestSync'
if ($SkipDebloat) { $runArgs += '-SkipDebloat' }
if ($SkipEquicord) { $runArgs += '-SkipEquicord' }
if ($SkipOpenAsar) { $runArgs += '-SkipOpenAsar' }
if ($SkipKernel) { $runArgs += '-SkipKernel' }
if ($FreshInstall) { $runArgs += '-FreshInstall' }

Write-HubProgress 18 'Running Disc-Optimizer...'
Write-HubStep "Arguments: $($runArgs -join ' ')"

# Run in-process so EXO_PROGRESS / Write-Output reach the elevated wrapper log.
$script:current = 20
try {
    & $Optimizer @runArgs 2>&1 | ForEach-Object {
        $line = "$_"
        if ([string]::IsNullOrWhiteSpace($line)) { return }
        # Disc-Optimizer already mirrors to EXO_LOG; only forward stdout once.
        if ($line -notmatch '^EXO_PROGRESS:') {
            Write-Output $line
        }
        $hit = Get-ProgressForLine $line $script:current
        if ($null -ne $hit) {
            $script:current = [int]$hit.Percent
            Write-HubProgress $script:current ([string]$hit.Status)
        }
    }

    $code = if ($null -ne $LASTEXITCODE) { [int]$LASTEXITCODE } else { 0 }
    if ($code -eq 0) {
        Write-HubOk 'Discord Optimizer finished successfully.'
        Write-HubProgress 100 'Completed successfully'
        exit 0
    }

    Write-HubErr "Disc Optimizer exited with code $code"
    Write-HubProgress 100 'Finished with errors'
    Write-HubWarn 'If Discord will not open, use Exo -> Repair Discord'
    exit $code
} catch {
    Write-HubErr $_.Exception.Message
    $kitErr = Join-Path $Root 'kit\logs\last-error.log'
    $hubErr = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Exo\logs\last-discord-error.log'
    if (Test-Path -LiteralPath $kitErr) { Write-HubErr "Error log: $kitErr" }
    if (Test-Path -LiteralPath $hubErr) { Write-HubErr "Error log: $hubErr" }
    elseif ($env:EXO_LOG) { Write-HubErr "Exo log: $env:EXO_LOG" }
    Write-HubProgress 100 'Failed'
    exit 1
}
