# OptiHub Discord Optimizer runner
# Wraps Disc-Optimizer.ps1 with live progress markers for the WinUI host.

param(
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
    $line = "OPTIHUB_PROGRESS:$p|$Status"
    # Success stream (elevated wrapper tees this into the poll log)
    Write-Output $line
    # Direct file fallback so UI never stalls if streams are swallowed
    if ($env:OPTIHUB_LOG -and (Test-Path (Split-Path -Parent $env:OPTIHUB_LOG) -PathType Container -ErrorAction SilentlyContinue)) {
        try { Add-Content -LiteralPath $env:OPTIHUB_LOG -Value $line -Encoding UTF8 -ErrorAction SilentlyContinue } catch { }
    }
}

function Write-HubStep([string]$Msg) {
    Write-Output "[*] $Msg"
    if ($env:OPTIHUB_LOG) { try { Add-Content -LiteralPath $env:OPTIHUB_LOG -Value "[*] $Msg" -Encoding UTF8 -ErrorAction SilentlyContinue } catch { } }
}

function Write-HubOk([string]$Msg) {
    Write-Output "[+] $Msg"
    if ($env:OPTIHUB_LOG) { try { Add-Content -LiteralPath $env:OPTIHUB_LOG -Value "[+] $Msg" -Encoding UTF8 -ErrorAction SilentlyContinue } catch { } }
}

function Write-HubWarn([string]$Msg) {
    Write-Output "[!] $Msg"
    if ($env:OPTIHUB_LOG) { try { Add-Content -LiteralPath $env:OPTIHUB_LOG -Value "[!] $Msg" -Encoding UTF8 -ErrorAction SilentlyContinue } catch { } }
}

function Write-HubErr([string]$Msg) {
    Write-Output "[-] $Msg"
    if ($env:OPTIHUB_LOG) { try { Add-Content -LiteralPath $env:OPTIHUB_LOG -Value "[-] $Msg" -Encoding UTF8 -ErrorAction SilentlyContinue } catch { } }
}

function Start-OptiHubDiscord {
    Write-HubProgress 96 'Opening Discord...'
    $local = [Environment]::GetFolderPath('LocalApplicationData')
    $root = Join-Path $local 'Discord'
    $app = Get-ChildItem $root -Directory -Filter 'app-*' -ErrorAction SilentlyContinue |
        Sort-Object Name -Descending |
        Select-Object -First 1
    if (-not $app) {
        Write-HubWarn 'Discord install folder not found - open Discord from the Start menu.'
        return
    }
    $exe = Join-Path $app.FullName 'Discord.exe'
    if (Test-Path $exe) {
        Start-Process -FilePath $exe -WorkingDirectory $app.FullName | Out-Null
        Write-HubOk 'Discord opened.'
        return
    }
    $update = Join-Path $root 'Update.exe'
    if (Test-Path $update) {
        Start-Process -FilePath $update -ArgumentList '--processStart','Discord.exe' -WorkingDirectory $root | Out-Null
        Write-HubOk 'Discord opened.'
        return
    }
    Write-HubWarn 'Could not find Discord.exe - open it from the Start menu.'
}

function Test-IsElevated {
    try {
        $id = [Security.Principal.WindowsIdentity]::GetCurrent()
        $p = New-Object Security.Principal.WindowsPrincipal $id
        return $p.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    } catch { return $false }
}

function Test-OptiHubDiscordApplied {
    $local = [Environment]::GetFolderPath('LocalApplicationData')
    $appData = [Environment]::GetFolderPath('ApplicationData')
    $discordRoot = Join-Path $local 'Discord'
    if (-not (Test-Path $discordRoot)) { return $false }

    $appDir = Get-ChildItem $discordRoot -Directory -Filter 'app-*' -ErrorAction SilentlyContinue |
        Sort-Object Name -Descending |
        Select-Object -First 1
    if (-not $appDir) { return $false }

    $resources = Join-Path $appDir.FullName 'resources'
    $equicordAsar = Join-Path $appData 'Equicord\equicord.asar'
    $appAsar = Join-Path $resources 'app.asar'
    $equicordOk = (Test-Path $equicordAsar) -and (Test-Path $appAsar) -and ((Get-Item $appAsar).Length -lt 4096)
    $openAsarOk = Test-Path (Join-Path $resources '_app.asar.stock')

    $versionDll = Join-Path $appDir.FullName 'version.dll'
    $ffmpeg = Join-Path $appDir.FullName 'ffmpeg.dll'
    $configIni = Join-Path $appDir.FullName 'config.ini'
    $kernelOk = (Test-Path $versionDll) -and (Test-Path $ffmpeg) -and (Test-Path $configIni)
    if ($kernelOk) {
        try { $kernelOk = (Get-Item $ffmpeg).Length -lt 500000 } catch { }
    }

    return $equicordOk -and ($kernelOk -or $openAsarOk)
}

function New-OptiHubRestorePoint {
    if (-not (Test-IsElevated)) {
        Write-HubWarn 'Restore point skipped (not elevated).'
        return
    }
    try {
        Write-HubProgress 8 'Creating system restore point...'
        Write-HubStep 'Creating system restore point (OptiHub-Discord)...'
        # Checkpoint-Computer can hang for minutes; bound it with a job timeout
        $job = Start-Job -ScriptBlock {
            try {
                Checkpoint-Computer -Description 'OptiHub Discord Optimizer' -RestorePointType 'MODIFY_SETTINGS' -ErrorAction Stop
                'ok'
            } catch {
                $_.Exception.Message
            }
        }
        $done = Wait-Job $job -Timeout 90
        if (-not $done) {
            Stop-Job $job -ErrorAction SilentlyContinue
            Remove-Job $job -Force -ErrorAction SilentlyContinue
            Write-HubWarn 'Restore point timed out after 90s - continuing without it.'
            return
        }
        $result = Receive-Job $job
        Remove-Job $job -Force -ErrorAction SilentlyContinue
        if ($result -eq 'ok') {
            Write-HubOk 'Restore point created.'
        } else {
            Write-HubWarn "Restore point not created: $result"
        }
    } catch {
        Write-HubWarn "Restore point not created: $($_.Exception.Message)"
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

function Get-ProgressForLine([string]$Line, [int]$Current) {
    $map = [ordered]@{
        'Closing Discord'            = @{ P = 22; S = 'Closing Discord...' }
        'Installing Discord'         = @{ P = 28; S = 'Installing Discord...' }
        'Discord up to date'         = @{ P = 32; S = 'Discord is up to date' }
        'first-run'                  = @{ P = 34; S = 'Initializing Discord...' }
        'Debloat'                    = @{ P = 40; S = 'Debloating Discord...' }
        'cache'                      = @{ P = 48; S = 'Cleaning cache...' }
        'Equicord'                   = @{ P = 58; S = 'Applying Equicord...' }
        'OpenASAR'                   = @{ P = 68; S = 'Installing OpenASAR...' }
        'AMOLED'                     = @{ P = 72; S = 'Applying AMOLED theme...' }
        'kernel'                     = @{ P = 78; S = 'Installing DiscOpt kernel...' }
        'Boot check'                 = @{ P = 86; S = 'Verifying Discord still boots...' }
        'Quick boot'                 = @{ P = 86; S = 'Quick verify (no Discord flash)...' }
        'Windows'                    = @{ P = 92; S = 'Applying Windows tweaks...' }
        'Start menu'                 = @{ P = 94; S = 'Refreshing Start menu shortcut...' }
        'DONE'                       = @{ P = 97; S = 'Finishing...' }
        'finished successfully'      = @{ P = 99; S = 'Almost done...' }
        'Disc Optimizer finished'    = @{ P = 99; S = 'Wrapping up...' }
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

if (-not $Quick -and (Test-OptiHubDiscordApplied)) {
    $Quick = $true
    Write-HubStep 'Already optimized - using Quick reapply'
    Write-HubProgress 10 'Quick reapply mode'
}

if ($CreateRestorePoint) {
    New-OptiHubRestorePoint
}

Write-HubProgress 14 'Preparing Disc Optimizer...'

$runArgs = @()
if ($Quick) { $runArgs += '-Quick' }
if ($SkipCacheClean) { $runArgs += '-SkipCacheClean' }
$runArgs += '-NoLaunch'
if ($SkipDebloat) { $runArgs += '-SkipDebloat' }
if ($SkipEquicord) { $runArgs += '-SkipEquicord' }
if ($SkipOpenAsar) { $runArgs += '-SkipOpenAsar' }
if ($SkipKernel) { $runArgs += '-SkipKernel' }
if ($FreshInstall) { $runArgs += '-FreshInstall' }

if ($NonInteractive) {
    $env:DISCOPT_NONINTERACTIVE = '1'
    $env:OPTIHUB_SKIP_BOOT_FLASH = '1'
}

Write-HubProgress 18 'Running Disc-Optimizer...'
Write-HubStep "Arguments: $($runArgs -join ' ')"

# Run in-process so OPTIHUB_PROGRESS / Write-Output reach the elevated wrapper log.
# Nested Start-Process was swallowing progress and freezing the UI after UAC.
$script:current = 20
try {
    & $Optimizer @runArgs 2>&1 | ForEach-Object {
        $line = "$_"
        if ([string]::IsNullOrWhiteSpace($line)) { return }
        # Avoid double-writing progress lines that Disc-Optimizer already logged
        if ($line -notmatch '^OPTIHUB_PROGRESS:') {
            Write-Output $line
            if ($env:OPTIHUB_LOG) {
                try { Add-Content -LiteralPath $env:OPTIHUB_LOG -Value $line -Encoding UTF8 -ErrorAction SilentlyContinue } catch { }
            }
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
        if (-not $NoLaunch) {
            Start-OptiHubDiscord
        }
        Write-HubProgress 100 'Completed successfully'
        exit 0
    }

    Write-HubErr "Disc Optimizer exited with code $code"
    Write-HubProgress 100 'Finished with errors'
    Write-HubWarn 'If Discord will not open, use OptiHub -> Repair Discord'
    exit $code
} catch {
    Write-HubErr $_.Exception.Message
    Write-HubProgress 100 'Failed'
    exit 1
}
