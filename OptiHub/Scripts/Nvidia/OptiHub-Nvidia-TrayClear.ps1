# OptiHub — NVIDIA tray / overflow killer (gaming quiet)
# Why icons "come back": NVDisplay.ContainerLocalSystem re-registers on soft-refresh / logon.
# Strategy:
#  1) Kill App-stack (NvContainerLocalSystem) permanently disabled
#  2) DELETE App/GFE/ShadowPlay tray keys
#  3) HIDE display-container tray (IsPromoted=0) — deleting only makes Windows recreate it as promoted
#  4) Optional multi-pass settle after container restart
#  5) Register lightweight logon task to re-hide (HKCU only, no elevation)
param(
    [switch]$NoTask,
    [int]$SettlePasses = 1
)
$ErrorActionPreference = 'Continue'

function Write-TLog([string]$Msg) {
    $line = "[TRAY] $Msg"
    Write-Host $line
    if ($env:OPTIHUB_LOG) {
        try { Add-Content -LiteralPath $env:OPTIHUB_LOG -Value $line -Encoding UTF8 -EA 0 } catch { }
    }
}

function Get-NvidiaNotifyRoots {
    $roots = [System.Collections.Generic.List[string]]::new()
    [void]$roots.Add('HKCU:\Control Panel\NotifyIconSettings')
    try {
        Get-ChildItem 'Registry::HKEY_USERS' -EA 0 | Where-Object {
            $_.PSChildName -match '^S-1-5-21-\d+-\d+-\d+-\d+$'
        } | ForEach-Object {
            [void]$roots.Add(("Registry::HKEY_USERS\{0}\Control Panel\NotifyIconSettings" -f $_.PSChildName))
        }
    } catch { }
    return @($roots | Select-Object -Unique)
}

function Test-IsDisplayContainerExe([string]$Exe) {
    return ($Exe -match '(?i)NVDisplay\.Container|Display\.NvContainer|nv_dispi\.inf')
}

function Test-IsNvidiaTrayExe([string]$Exe) {
    return ($Exe -match '(?i)NVIDIA|nvcontainer|NVDisplay|GeForce|ShadowPlay|nvsphelper|nvapp|NvBackend|NvNode|nvtray')
}

function Invoke-OptiHubNvidiaTrayPass {
    $removed = 0
    $hidden = 0
    $patternApp = '(?i)NVIDIA App|nvapp|GeForce Experience|ShadowPlay|nvsphelper|NvBackend|NvNode|GFExperience|NVIDIA Share|NVIDIA Overlay'

    foreach ($root in (Get-NvidiaNotifyRoots)) {
        if (-not (Test-Path -LiteralPath $root)) { continue }
        Get-ChildItem -LiteralPath $root -EA 0 | ForEach-Object {
            $keyPath = $_.PSPath
            $exe = $null
            try { $exe = [string](Get-ItemProperty -LiteralPath $keyPath -EA 0).ExecutablePath } catch { }
            if ([string]::IsNullOrWhiteSpace($exe)) { return }
            if (-not (Test-IsNvidiaTrayExe $exe)) { return }

            if (Test-IsDisplayContainerExe $exe) {
                # Keep key (prevents re-create as promoted) — force overflow-hidden
                try {
                    New-ItemProperty -LiteralPath $keyPath -Name 'IsPromoted' -Value 0 -PropertyType DWord -Force -EA 0 | Out-Null
                    Set-ItemProperty -LiteralPath $keyPath -Name 'IsPromoted' -Value 0 -Type DWord -Force -EA 0
                    # Some builds honor these
                    try { Set-ItemProperty -LiteralPath $keyPath -Name 'IsPromoted' -Value 0 -Force -EA 0 } catch { }
                    $hidden++
                    Write-TLog "Hidden display tray: $exe"
                } catch {
                    Write-TLog "Hide failed: $($_.Exception.Message)"
                }
                return
            }

            # App / GFE / overlay ghosts — delete
            if ($exe -match $patternApp -or $exe -match '(?i)\\nvcontainer\.exe' -or -not (Test-IsDisplayContainerExe $exe)) {
                # Non-display NVIDIA: delete
                if (-not (Test-IsDisplayContainerExe $exe)) {
                    try {
                        Remove-Item -LiteralPath $keyPath -Recurse -Force -EA Stop
                        $removed++
                        Write-TLog "Removed tray key: $exe"
                    } catch {
                        # Fallback hide
                        try {
                            Set-ItemProperty -LiteralPath $keyPath -Name 'IsPromoted' -Value 0 -Type DWord -Force -EA 0
                            $hidden++
                        } catch { }
                    }
                }
            }
        }
    }
    return [pscustomobject]@{ Removed = $removed; Hidden = $hidden }
}

function Disable-NvidiaAppContainer {
    try {
        $svc = Get-Service -Name 'NvContainerLocalSystem' -EA 0
        if ($svc) {
            if ($svc.Status -ne 'Stopped') { Stop-Service NvContainerLocalSystem -Force -EA 0 }
            Set-Service NvContainerLocalSystem -StartupType Disabled -EA 0
            Write-TLog 'NvContainerLocalSystem Stopped/Disabled'
        }
    } catch { }

    foreach ($im in @(
        'NVIDIA App.exe', 'NVIDIA Overlay.exe', 'NVIDIA Share.exe', 'nvsphelper64.exe',
        'nvsphelper.exe', 'NVIDIA Web Helper.exe', 'GFExperience.exe', 'NVIDIA GeForce Experience.exe'
    )) {
        try { & taskkill.exe /F /IM $im /T 2>$null | Out-Null } catch { }
    }

    # User-session nvcontainer only (never kill display LS host path carelessly)
    try {
        Get-CimInstance Win32_Process -Filter "Name = 'nvcontainer.exe'" -EA 0 | ForEach-Object {
            $cmd = [string]$_.CommandLine
            if ($cmd -match '(?i)Display\.NvContainer|NVDisplay') { return }
            try { Stop-Process -Id $_.ProcessId -Force -EA 0 } catch { }
        }
    } catch { }
}

function Clear-NvidiaAppLeftovers {
    foreach ($p in @(
        (Join-Path $env:ProgramData 'NVIDIA Corporation\NVIDIA App'),
        (Join-Path $env:LOCALAPPDATA 'NVIDIA Corporation\NVIDIA App'),
        (Join-Path $env:ProgramFiles 'NVIDIA Corporation\NVIDIA App')
    )) {
        if (-not (Test-Path -LiteralPath $p)) { continue }
        try {
            Remove-Item -LiteralPath $p -Recurse -Force -EA Stop
            Write-TLog "Removed $p"
        } catch {
            try {
                & takeown.exe /F $p /R /D Y 2>$null | Out-Null
                & icacls.exe $p /grant Administrators:F /T /C /Q 2>$null | Out-Null
                Remove-Item -LiteralPath $p -Recurse -Force -EA 0
                Write-TLog "Removed $p (after takeown)"
            } catch {
                Write-TLog "Could not remove $p"
            }
        }
    }

    # Startup / Run keys
    foreach ($run in @(
        'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run',
        'HKLM:\Software\Microsoft\Windows\CurrentVersion\Run',
        'HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run'
    )) {
        if (-not (Test-Path $run)) { continue }
        try {
            $props = Get-ItemProperty -LiteralPath $run -EA 0
            if (-not $props) { continue }
            $props.PSObject.Properties | Where-Object {
                $_.Name -notmatch '^PS' -and ([string]$_.Value -match '(?i)NVIDIA|GeForce|nvapp|ShadowPlay')
            } | ForEach-Object {
                try {
                    Remove-ItemProperty -LiteralPath $run -Name $_.Name -Force -EA 0
                    Write-TLog "Removed Run: $($_.Name)"
                } catch { }
            }
        } catch { }
    }
}

function Register-OptiHubTrayHideTask {
    # Lightweight: at logon re-hide NVIDIA tray (HKCU). No elevation required for IsPromoted.
    $taskName = 'OptiHub-NvidiaTrayHide'
    $scriptPath = $PSCommandPath
    if ([string]::IsNullOrWhiteSpace($scriptPath) -or -not (Test-Path -LiteralPath $scriptPath)) {
        $scriptPath = Join-Path $PSScriptRoot 'OptiHub-Nvidia-TrayClear.ps1'
    }
    if (-not (Test-Path -LiteralPath $scriptPath)) {
        Write-TLog 'Skip task: tray script path missing'
        return
    }

    $arg = "-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File `"$scriptPath`" -NoTask -SettlePasses 2"
    try {
        Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -EA 0
    } catch { }

    try {
        $action = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument $arg
        $trigger = New-ScheduledTaskTrigger -AtLogOn -User $env:USERNAME
        $settings = New-ScheduledTaskSettingsSet `
            -AllowStartIfOnBatteries `
            -DontStopIfGoingOnBatteries `
            -StartWhenAvailable `
            -ExecutionTimeLimit (New-TimeSpan -Minutes 3) `
            -MultipleInstances IgnoreNew
        $principal = New-ScheduledTaskPrincipal -UserId $env:USERNAME -LogonType Interactive -RunLevel Limited
        Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger `
            -Settings $settings -Principal $principal -Force -EA Stop | Out-Null
        Write-TLog "Registered logon task: $taskName (re-hides tray after container restarts)"
    } catch {
        # Fallback schtasks
        try {
            $cmd = "powershell.exe $arg"
            schtasks /Create /TN $taskName /TR $cmd /SC ONLOGON /RL LIMITED /F 2>$null | Out-Null
            Write-TLog "Registered logon task via schtasks: $taskName"
        } catch {
            Write-TLog "Could not register tray task: $($_.Exception.Message)"
        }
    }
}

# ---- main ----
Write-TLog 'Clearing / hiding NVIDIA tray icons...'
Disable-NvidiaAppContainer
Clear-NvidiaAppLeftovers

$passes = [Math]::Max(1, $SettlePasses)
$totalRemoved = 0
$totalHidden = 0
for ($i = 1; $i -le $passes; $i++) {
    $r = Invoke-OptiHubNvidiaTrayPass
    $totalRemoved += [int]$r.Removed
    $totalHidden += [int]$r.Hidden
    if ($i -lt $passes) { Start-Sleep -Milliseconds 800 }
}

# One more pass after short delay (container late-registers)
Start-Sleep -Milliseconds 600
$r2 = Invoke-OptiHubNvidiaTrayPass
$totalRemoved += [int]$r2.Removed
$totalHidden += [int]$r2.Hidden

Write-TLog "Done. Removed=$totalRemoved Hidden(display)=$totalHidden"

if (-not $NoTask) {
    Register-OptiHubTrayHideTask
}

Write-Output 'OPTIHUB_PROGRESS:100|Tray cleared'
exit 0
