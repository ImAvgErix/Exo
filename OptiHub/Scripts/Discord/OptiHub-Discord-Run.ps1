# OptiHub Discord Optimizer runner
# Wraps Disc-Optimizer.ps1 with progress markers, dry-run, restore points, non-interactive mode.
# Source kit: https://github.com/BarcusEric/DiscOpti

param(
    [switch]$DryRun,
    [switch]$CreateRestorePoint,
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
$env:OPTIHUB = '1'

function Write-HubProgress([int]$Percent, [string]$Status) {
    $p = [Math]::Max(0, [Math]::Min(100, $Percent))
    Write-Host "OPTIHUB_PROGRESS:$p|$Status"
}

function Write-HubStep([string]$Msg) {
    Write-Host "[*] $Msg" -ForegroundColor Cyan
}

function Write-HubOk([string]$Msg) {
    Write-Host "[+] $Msg" -ForegroundColor Green
}

function Write-HubWarn([string]$Msg) {
    Write-Host "[!] $Msg" -ForegroundColor Yellow
}

function Write-HubErr([string]$Msg) {
    Write-Host "[-] $Msg" -ForegroundColor Red
}

function Test-IsElevated {
    try {
        $id = [Security.Principal.WindowsIdentity]::GetCurrent()
        $p = New-Object Security.Principal.WindowsPrincipal $id
        return $p.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    } catch { return $false }
}

function New-OptiHubRestorePoint {
    if (-not (Test-IsElevated)) {
        Write-HubWarn 'Restore point skipped (not elevated).'
        return
    }
    try {
        Write-HubProgress 8 'Creating system restore point…'
        Write-HubStep 'Creating system restore point (OptiHub-Discord)…'
        # Checkpoint-Computer can fail if restore is disabled or frequency limited
        Checkpoint-Computer -Description 'OptiHub Discord Optimizer' -RestorePointType 'MODIFY_SETTINGS' -ErrorAction Stop
        Write-HubOk 'Restore point created.'
    } catch {
        Write-HubWarn "Restore point not created: $($_.Exception.Message)"
        # Fallback: WMI SystemRestore
        try {
            $sr = Get-CimClass -Namespace 'root/default' -ClassName 'SystemRestore' -ErrorAction SilentlyContinue
            if ($sr) {
                $result = Invoke-CimMethod -Namespace 'root/default' -ClassName 'SystemRestore' -MethodName 'CreateRestorePoint' -Arguments @{
                    Description = 'OptiHub Discord Optimizer'
                    RestorePointType = 12
                    EventType = 100
                }
                if ($result.ReturnValue -eq 0) {
                    Write-HubOk 'Restore point created (WMI).'
                } else {
                    Write-HubWarn "WMI restore point return: $($result.ReturnValue)"
                }
            }
        } catch {
            Write-HubWarn 'System Restore may be disabled on this PC.'
        }
    }
}

$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
$Optimizer = Join-Path $Root 'Disc-Optimizer.ps1'

if (-not (Test-Path $Optimizer)) {
    Write-HubErr "Disc-Optimizer.ps1 not found beside this script: $Root"
    Write-HubProgress 100 'Missing optimizer script'
    exit 1
}

Write-Host ''
Write-Host '  OptiHub · Discord Optimizer' -ForegroundColor Cyan
Write-Host '  OptiHub safety wrappers · progress · dry-run' -ForegroundColor DarkGray
Write-Host ''

Write-HubProgress 3 'Starting…'

if ($DryRun) {
    Write-HubProgress 10 'Dry-run: verify only'
    Write-HubStep 'Dry-run enabled — no system changes will be made.'
    $verifyArgs = @('-VerifyOnly', '-NoLaunch')
    if ($Quick) { $verifyArgs += '-Quick' }

    try {
        Write-HubProgress 25 'Running verification…'
        & $Optimizer @verifyArgs
        $code = if ($null -ne $LASTEXITCODE) { $LASTEXITCODE } else { 0 }
        if ($code -eq 0) {
            Write-HubOk 'Dry-run verification completed.'
            Write-HubProgress 100 'Dry-run complete'
            exit 0
        } else {
            Write-HubWarn "Verify reported exit code $code"
            Write-HubProgress 100 'Dry-run finished with warnings'
            exit $code
        }
    } catch {
        Write-HubErr $_.Exception.Message
        Write-HubProgress 100 'Dry-run failed'
        exit 1
    }
}

if ($CreateRestorePoint) {
    New-OptiHubRestorePoint
}

Write-HubProgress 12 'Preparing Disc Optimizer…'

$runArgs = @()
if ($Quick) { $runArgs += '-Quick' }
if ($SkipCacheClean) { $runArgs += '-SkipCacheClean' }
if ($NoLaunch -or $NonInteractive) { $runArgs += '-NoLaunch' }
if ($SkipDebloat) { $runArgs += '-SkipDebloat' }
if ($SkipEquicord) { $runArgs += '-SkipEquicord' }
if ($SkipOpenAsar) { $runArgs += '-SkipOpenAsar' }
if ($SkipKernel) { $runArgs += '-SkipKernel' }
if ($FreshInstall) { $runArgs += '-FreshInstall' }

# Non-interactive: suppress Read-Host prompts by pre-setting env + stdin tricks where possible
if ($NonInteractive) {
    $env:DISCOPT_NONINTERACTIVE = '1'
}

Write-HubProgress 18 'Launching Disc-Optimizer.ps1…'
Write-HubStep "Arguments: $($runArgs -join ' ')"

# Stream output and translate key steps into progress
$stepMap = [ordered]@{
    'Closing Discord'           = 22
    'Installing Discord'        = 30
    'Discord'                   = 35
    'Debloat'                   = 45
    'cache'                     = 52
    'Equicord'                  = 62
    'OpenASAR'                  = 70
    'kernel'                    = 78
    'Boot safety'               = 85
    'Windows'                   = 90
    'DONE'                      = 96
    'finished'                  = 98
}

try {
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = (Get-Process -Id $PID).Path
    if (-not $psi.FileName -or $psi.FileName -notmatch 'pwsh|powershell') {
        $psi.FileName = 'powershell.exe'
    }
    # Prefer current host executable
    try {
        $psi.FileName = [System.Diagnostics.Process]::GetCurrentProcess().MainModule.FileName
    } catch {}

    $argLine = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$Optimizer`"") + ($runArgs | ForEach-Object { $_ })
    $psi.Arguments = ($argLine -join ' ')
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.CreateNoWindow = $true
    $psi.WorkingDirectory = $Root

    $proc = New-Object System.Diagnostics.Process
    $proc.StartInfo = $psi
    [void]$proc.Start()

    $current = 20
    while (-not $proc.HasExited) {
        while (-not $proc.StandardOutput.EndOfStream) {
            $line = $proc.StandardOutput.ReadLine()
            if ($null -eq $line) { break }
            Write-Host $line
            foreach ($key in $stepMap.Keys) {
                if ($line -match [regex]::Escape($key)) {
                    $target = [int]$stepMap[$key]
                    if ($target -gt $current) {
                        $current = $target
                        Write-HubProgress $current ($line.Trim())
                    }
                    break
                }
            }
            if ($line -match '^\[\*' -and $current -lt 92) {
                $current = [Math]::Min(92, $current + 2)
                Write-HubProgress $current ($line.Trim())
            }
        }
        Start-Sleep -Milliseconds 100
    }

    $err = $proc.StandardError.ReadToEnd()
    if ($err) { Write-Host $err }

    # Drain remaining stdout
    $rest = $proc.StandardOutput.ReadToEnd()
    if ($rest) {
        Write-Host $rest
    }

    $code = $proc.ExitCode
    if ($code -eq 0) {
        Write-HubOk 'Discord Optimizer finished successfully.'
        Write-HubProgress 100 'Completed successfully'
        exit 0
    } else {
        Write-HubErr "Disc Optimizer exited with code $code"
        Write-HubProgress 100 'Finished with errors'
        Write-HubWarn 'If Discord will not open, use OptiHub → Repair Discord or:'
        Write-Host '  irm "https://raw.githubusercontent.com/BarcusEric/DiscOpti/main/Repair-Discord.ps1" | iex' -ForegroundColor Cyan
        exit $code
    }
} catch {
    # Fallback: direct invocation (same process)
    Write-HubWarn "Streamed launch failed ($($_.Exception.Message)); running in-process…"
    Write-HubProgress 25 'Running Disc Optimizer…'
    try {
        & $Optimizer @runArgs
        $code = if ($null -ne $LASTEXITCODE) { $LASTEXITCODE } else { 0 }
        if ($code -eq 0) {
            Write-HubProgress 100 'Completed successfully'
            exit 0
        }
        Write-HubProgress 100 'Finished with errors'
        exit $code
    } catch {
        Write-HubErr $_.Exception.Message
        Write-HubProgress 100 'Failed'
        exit 1
    }
}
