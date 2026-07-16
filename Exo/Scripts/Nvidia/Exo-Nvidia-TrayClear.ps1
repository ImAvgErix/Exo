# Exo - NVIDIA tray / overflow killer (gaming quiet)
#
# Why icons "come back":
#   NVDisplay.ContainerLocalSystem re-registers a tray key on soft-refresh / logon.
#   Deleting that key makes Windows recreate it as promoted (worse).
#
# Strategy:
#   1) Kill App-stack (NvContainerLocalSystem) permanently disabled
#   2) DELETE App/GFE/ShadowPlay tray keys
#   3) HIDE display-container tray (IsPromoted=0) - leave the key
#   4) Multi-pass settle after container restart
#   5) NEVER create Exo scheduled tasks / Run keys / background services
#      (product rule: Apply-only; purge any leftover Exo-* tasks)
#
param(
    [switch]$NoTask,  # kept for callers; tasks are never registered either way
    [int]$SettlePasses = 2
)
$ErrorActionPreference = 'Continue'

function Write-TLog([string]$Msg) {
    $line = "[TRAY] $Msg"
    Write-Host $line
    if ($env:EXO_LOG) {
        try { Add-Content -LiteralPath $env:EXO_LOG -Value $line -Encoding UTF8 -EA 0 } catch { }
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

function Invoke-ExoNvidiaTrayPass {
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
                # Keep key (prevents re-create as promoted) - only touch if still promoted
                try {
                    $cur = $null
                    try { $cur = (Get-ItemProperty -LiteralPath $keyPath -EA 0).IsPromoted } catch { }
                    if ([int]$cur -eq 0) { return } # already hidden - do not spam Apply logs
                    New-ItemProperty -LiteralPath $keyPath -Name 'IsPromoted' -Value 0 -PropertyType DWord -Force -EA 0 | Out-Null
                    Set-ItemProperty -LiteralPath $keyPath -Name 'IsPromoted' -Value 0 -Type DWord -Force -EA 0
                    $hidden++
                    Write-TLog "Hidden display tray (was promoted): $exe"
                } catch {
                    Write-TLog "Hide failed: $($_.Exception.Message)"
                }
                return
            }

            # App / GFE / overlay ghosts only - skip if path already gone from disk and key absent
            if ($exe -match $patternApp -or $exe -match '(?i)\\nvcontainer\.exe') {
                try {
                    Remove-Item -LiteralPath $keyPath -Recurse -Force -EA Stop
                    $removed++
                    Write-TLog "Removed App/GFE tray key: $exe"
                } catch {
                    try {
                        $cur = $null
                        try { $cur = (Get-ItemProperty -LiteralPath $keyPath -EA 0).IsPromoted } catch { }
                        if ([int]$cur -ne 0) {
                            Set-ItemProperty -LiteralPath $keyPath -Name 'IsPromoted' -Value 0 -Type DWord -Force -EA 0
                            $hidden++
                        }
                    } catch { }
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
            Write-TLog 'NvContainerLocalSystem Stopped/Disabled (App stack only)'
        }
    } catch { }

    foreach ($im in @(
        'NVIDIA App.exe', 'NVIDIA Overlay.exe', 'NVIDIA Share.exe', 'nvsphelper64.exe',
        'nvsphelper.exe', 'NVIDIA Web Helper.exe', 'GFExperience.exe', 'NVIDIA GeForce Experience.exe'
    )) {
        try { & taskkill.exe /F /IM $im /T 2>$null | Out-Null } catch { }
    }

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
                $_.Name -notmatch '^PS' -and ([string]$_.Value -match '(?i)NVIDIA|GeForce|nvapp|ShadowPlay|\\Exo\\')
            } | ForEach-Object {
                try {
                    Remove-ItemProperty -LiteralPath $run -Name $_.Name -Force -EA 0
                    Write-TLog "Removed Run: $($_.Name)"
                } catch { }
            }
        } catch { }
    }
}

function Unregister-ExoTrayTasks {
    # Product rule: Exo never installs logon/background tasks. Purge leftovers.
    $names = @(
        'Exo-NvidiaTrayHide',
        'Exo-NvidiaDisplayPersist',
        'Exo-NvidiaBackgroundPersist',
        'Exo-NvidiaTray',
        'Exo-Nvidia'
    )
    foreach ($taskName in $names) {
        try { Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -EA 0 } catch { }
        try { schtasks /Delete /TN $taskName /F 2>$null | Out-Null } catch { }
    }
    try {
        Get-ScheduledTask -EA 0 | Where-Object { $_.TaskName -match '(?i)^Exo-' } | ForEach-Object {
            try {
                Unregister-ScheduledTask -TaskName $_.TaskName -TaskPath $_.TaskPath -Confirm:$false -EA 0
                Write-TLog "Removed leftover task: $($_.TaskPath)$($_.TaskName)"
            } catch { }
        }
    } catch { }
    Write-TLog 'Exo scheduled tasks purged (none registered)'
}

# ---- main ----
Write-TLog 'Clearing / hiding NVIDIA tray icons...'
Disable-NvidiaAppContainer
Clear-NvidiaAppLeftovers
Unregister-ExoTrayTasks

$passes = [Math]::Max(1, $SettlePasses)
$totalRemoved = 0
$totalHidden = 0
for ($i = 1; $i -le $passes; $i++) {
    $r = Invoke-ExoNvidiaTrayPass
    $totalRemoved += [int]$r.Removed
    $totalHidden += [int]$r.Hidden
    if ($i -lt $passes) { Start-Sleep -Milliseconds 800 }
}

# One more pass after short delay (container late-registers)
Start-Sleep -Milliseconds 600
$r2 = Invoke-ExoNvidiaTrayPass
$totalRemoved += [int]$r2.Removed
$totalHidden += [int]$r2.Hidden

Write-TLog "Done. Removed=$totalRemoved Hidden(display)=$totalHidden"
Write-TLog 'No Exo background services, startup entries, or scheduled tasks created'

Write-Output 'EXO_PROGRESS:100|Tray cleared'
exit 0
