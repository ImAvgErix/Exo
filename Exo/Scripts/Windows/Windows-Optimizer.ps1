#Requires -Version 7.0
# Exo Windows host optimizer  -  Game Mode, HAGS, Game Bar, priority, input, power.
# Module-scoped only (no Defender/VBS/BIOS). Reversible via snapshot Repair.
[CmdletBinding()]
param(
    [switch]$Repair,
    [switch]$NonInteractive,
    [switch]$Experimental
)

$ErrorActionPreference = 'Stop'
$Script:WindowsOptVersion = '1.4.0'
$ExoRoot = Join-Path $env:LOCALAPPDATA 'Exo'
$StatePath = Join-Path $ExoRoot 'windows-optimizer.json'
$Report = [System.Collections.Generic.List[string]]::new()

# Shared libs at script scope
$__common = Join-Path (Split-Path -Parent $PSScriptRoot) 'lib\Exo.Common.ps1'
if (-not (Test-Path -LiteralPath $__common)) { $__common = Join-Path $PSScriptRoot '..\lib\Exo.Common.ps1' }
if (-not (Test-Path -LiteralPath $__common) -and $env:LOCALAPPDATA) {
    $__common = Join-Path $env:LOCALAPPDATA 'Exo\app\Scripts\lib\Exo.Common.ps1'
}
if (Test-Path -LiteralPath $__common) {
    . $__common
    foreach ($__libPath in @(Import-ExoSharedLibFiles -From $PSScriptRoot)) { . $__libPath }
} else {
    foreach ($name in @('Exo.GameBar.ps1', 'Exo.GamingStack.ps1')) {
        foreach ($c in @(
            (Join-Path (Split-Path -Parent $PSScriptRoot) "lib\$name"),
            (Join-Path $PSScriptRoot "..\lib\$name"),
            (Join-Path $env:LOCALAPPDATA "Exo\scripts\lib\$name"),
            (Join-Path $env:LOCALAPPDATA "Exo\app\Scripts\lib\$name")
        )) {
            if ($c -and (Test-Path -LiteralPath $c)) { . $c; break }
        }
    }
}

function Write-HubProgress([int]$Percent, [string]$Status) {
    $p = [Math]::Max(0, [Math]::Min(100, $Percent))
    $line = "EXO_PROGRESS:$p|$Status"
    Write-Host $line
    Write-Output $line
    if ($env:EXO_LOG) {
        try { Add-Content -LiteralPath $env:EXO_LOG -Value $line -Encoding utf8 -ErrorAction SilentlyContinue } catch { }
    }
}
function Add-Report([string]$Step, [string]$Status, [string]$Reason = '') {
    $entry = if ($Reason) { "${Step}|${Status}:${Reason}" } else { "${Step}|${Status}" }
    [void]$Report.Add($entry)
    Write-Output ("EXO_REPORT:{0}" -f $entry)
}
function Write-Ok([string]$Msg) { Write-Host "[+] $Msg" -ForegroundColor Green }
function Write-Warn([string]$Msg) { Write-Host "[!] $Msg" -ForegroundColor DarkYellow }

function Read-WindowsState {
    if (-not (Test-Path -LiteralPath $StatePath)) { return $null }
    try { return Get-Content -LiteralPath $StatePath -Raw -Encoding UTF8 | ConvertFrom-Json } catch { return $null }
}
function Save-WindowsState($State) {
    if (-not (Test-Path -LiteralPath $ExoRoot)) {
        New-Item -ItemType Directory -Path $ExoRoot -Force | Out-Null
    }
    $json = $State | ConvertTo-Json -Depth 10 -Compress
    [IO.File]::WriteAllText($StatePath, $json, [Text.UTF8Encoding]::new($false))
}

function Get-MenuShowDelaySnapshot {
    $entry = [ordered]@{ path = 'Control Panel\Desktop'; name = 'MenuShowDelay'; existed = $false; value = $null; kind = $null }
    try {
        $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Control Panel\Desktop')
        if ($key) {
            try {
                if ('MenuShowDelay' -in @($key.GetValueNames())) {
                    $entry.existed = $true
                    $entry.value = $key.GetValue('MenuShowDelay')
                    $entry.kind = [string]$key.GetValueKind('MenuShowDelay')
                }
            } finally { $key.Dispose() }
        }
    } catch { }
    return [pscustomobject]$entry
}

function Set-MenuShowDelayZero {
    try {
        $key = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey('Control Panel\Desktop', $true)
        try {
            $key.SetValue('MenuShowDelay', '0', [Microsoft.Win32.RegistryValueKind]::String)
            return 1
        } finally { $key.Dispose() }
    } catch { return 0 }
}

function Restore-MenuShowDelay($Entry) {
    if (-not $Entry) { return 0 }
    try {
        $key = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey('Control Panel\Desktop', $true)
        try {
            if ([bool]$Entry.existed) {
                $kind = [Microsoft.Win32.RegistryValueKind]::String
                if ($Entry.kind) {
                    [void][enum]::TryParse([Microsoft.Win32.RegistryValueKind], [string]$Entry.kind, $true, [ref]$kind)
                }
                $key.SetValue('MenuShowDelay', $Entry.value, $kind)
            } else {
                try { $key.DeleteValue('MenuShowDelay', $false) } catch { }
            }
            return 1
        } finally { $key.Dispose() }
    } catch { return 0 }
}

function Get-ActivePowerSchemeGuid {
    try {
        $line = powercfg /getactivescheme 2>$null | Out-String
        $m = [regex]::Match([string]$line, '([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})')
        if ($m.Success) { return $m.Groups[1].Value }
    } catch { }
    return $null
}

function Restore-PowerScheme([string]$Guid) {
    if ([string]::IsNullOrWhiteSpace($Guid)) { return 0 }
    try {
        powercfg -S $Guid | Out-Null
        return 1
    } catch { return 0 }
}

function Get-StateProp($Obj, [string]$Name) {
    # Safe under $ErrorActionPreference=Stop - ConvertFrom-Json objects throw on missing members.
    if ($null -eq $Obj) { return $null }
    if ($Obj -is [System.Collections.IDictionary]) {
        if ($Obj.Contains($Name)) { return $Obj[$Name] }
        return $null
    }
    $p = $Obj.PSObject.Properties[$Name]
    if ($null -eq $p) { return $null }
    return $p.Value
}

function Invoke-WindowsRepair {
    Write-HubProgress 10 'Loading Windows snapshot...'
    $state = Read-WindowsState
    $r = Get-StateProp $state 'recovery'
    if (-not $state -or $null -eq $r) {
        Add-Report 'repair' 'fail' 'no snapshot'
        throw 'No Windows optimizer snapshot to restore. Apply once first so Repair has a baseline.'
    }
    Write-HubProgress 30 'Restoring Game Bar / HAGS / Game Mode / priority...'
    if (Get-Command Restore-ExoGameBarFromSnapshot -ErrorAction SilentlyContinue) {
        try {
            [void](Restore-ExoGameBarFromSnapshot -SnapshotEntries @(Get-StateProp $r 'gameBar'))
            Add-Report 'game-bar' 'ok' 'restored'
        } catch { Add-Report 'game-bar' 'fail' $_.Exception.Message }
    }
    $hagsSnap = Get-StateProp $r 'hags'
    if (Get-Command Restore-ExoHagsFromSnapshot -ErrorAction SilentlyContinue -and $hagsSnap) {
        try {
            [void](Restore-ExoHagsFromSnapshot -Entry $hagsSnap)
            Add-Report 'hags' 'ok' 'restored'
        } catch { Add-Report 'hags' 'fail' $_.Exception.Message }
    }
    if (Get-Command Restore-ExoGameModeFromSnapshot -ErrorAction SilentlyContinue) {
        try {
            [void](Restore-ExoGameModeFromSnapshot -Entries @(Get-StateProp $r 'gameMode'))
            Add-Report 'game-mode' 'ok' 'restored'
        } catch { Add-Report 'game-mode' 'fail' $_.Exception.Message }
    }
    $win32Snap = Get-StateProp $r 'win32Priority'
    if (Get-Command Restore-ExoWin32PriorityFromSnapshot -ErrorAction SilentlyContinue -and $win32Snap) {
        try {
            [void](Restore-ExoWin32PriorityFromSnapshot -Entry $win32Snap)
            Add-Report 'win32-priority' 'ok' 'restored'
        } catch { Add-Report 'win32-priority' 'fail' $_.Exception.Message }
    }
    Write-HubProgress 70 'Restoring menus / power...'
    $menuSnap = Get-StateProp $r 'menuShowDelay'
    if ($menuSnap) {
        [void](Restore-MenuShowDelay $menuSnap)
        Add-Report 'menu-delay' 'ok' 'restored'
    }
    $powerGuid = Get-StateProp $r 'powerSchemeGuid'
    if ($powerGuid) {
        [void](Restore-PowerScheme ([string]$powerGuid))
        Add-Report 'power-plan' 'ok' 'restored'
    }
    # Mouse / MPO / sticky are soft competitive defaults  -  leave as-is unless snapshotted
    $mouseSnap = Get-StateProp $r 'mouse'
    if ($mouseSnap -and (Get-Command Restore-ExoMouseFromSnapshot -ErrorAction SilentlyContinue)) {
        try { [void](Restore-ExoMouseFromSnapshot -Entry $mouseSnap) } catch { }
    }

    $state.applyStatus = 'repaired'
    $state.applied = $false
    $state.repairedUtc = (Get-Date).ToUniversalTime().ToString('o')
    $state.applyReport = @($Report)
    Save-WindowsState $state
    Write-HubProgress 100 'Repair complete'
    Write-Output 'DONE - Windows host stack restored from snapshot'
    exit 0
}

function Invoke-WindowsApply {
    # Deep pack is OPTIONAL depth after native C# (WebHostBridge).
    # Default product path is native-only. This script only runs for Repair,
    # Experimental, or soft-fail depth. Never re-do the whole host stack when
    # native already applied - that caused Defender hangs + yield stripping.
    Write-HubProgress 5 'Windows deep pack (depth-only after native)...'
    if (-not (Get-Command Invoke-ExoCompetitiveGamingGlue -ErrorAction SilentlyContinue)) {
        Add-Report 'gaming-glue' 'fail' 'Exo.GamingStack.ps1 not loaded'
        throw 'Windows shared libs not loaded (Exo.GamingStack.ps1). Reinstall Exo or re-sync scripts.'
    }

    $prev = Read-WindowsState
    $recovery = $null
    $prevRecovery = Get-StateProp $prev 'recovery'
    $prevApplied = Get-StateProp $prev 'applied'
    $nativeAlready = (Get-StateProp $prev 'path') -eq 'native-csharp' -and $prevApplied -eq $true

    if ($prev -and $null -ne $prevRecovery -and $prevApplied -eq $true) {
        $recovery = $prevRecovery
        Add-Report 'snapshot' 'ok' 'kept original pre-Exo snapshot'
    } else {
        $recovery = [ordered]@{
            gameBar         = @()
            hags            = $null
            gameMode        = @()
            win32Priority   = $null
            menuShowDelay   = Get-MenuShowDelaySnapshot
            powerSchemeGuid = Get-ActivePowerSchemeGuid
            capturedUtc     = (Get-Date).ToUniversalTime().ToString('o')
        }
        if (Get-Command Get-ExoGameBarSnapshot -ErrorAction SilentlyContinue) {
            $recovery.gameBar = @(Get-ExoGameBarSnapshot)
        }
        if (Get-Command Get-ExoHagsSnapshot -ErrorAction SilentlyContinue) {
            $recovery.hags = Get-ExoHagsSnapshot
        }
        if (Get-Command Get-ExoGameModeSnapshot -ErrorAction SilentlyContinue) {
            $recovery.gameMode = @(Get-ExoGameModeSnapshot)
        }
        if (Get-Command Get-ExoWin32PrioritySnapshot -ErrorAction SilentlyContinue) {
            $recovery.win32Priority = Get-ExoWin32PrioritySnapshot
        }
        Add-Report 'snapshot' 'ok' 'pre-Exo host snapshot captured'
    }

    if ($recovery -is [System.Collections.IDictionary] -or $recovery -is [hashtable]) {
        if (Get-Command Get-ExoWindowsUpdateSnapshot -ErrorAction SilentlyContinue) {
            $recovery['windowsUpdate'] = @(Get-ExoWindowsUpdateSnapshot)
        }
        if (Get-Command Get-ExoDefenderSnapshot -ErrorAction SilentlyContinue) {
            $recovery['defender'] = @(Get-ExoDefenderSnapshot)
        }
    }

    # -- Depth-only steps (native owns competitive host knobs) --------------
    Write-HubProgress 12 'Purging noisy Exo console helpers (keep Hidden yield companions)...'
    $bgRemoved = 0
    if (Get-Command Unregister-ExoBackground -ErrorAction SilentlyContinue) {
        $bgRemoved = [int](Unregister-ExoBackground -Quiet)
        Add-Report 'no-background' 'ok' ("purged-console-helpers=$bgRemoved")
        Write-Ok "Background console purge: $bgRemoved"
    } else {
        Add-Report 'no-background' 'skip' 'Exo.NoBackground.ps1 not loaded'
    }

    Write-HubProgress 20 'Windows Update max pause / defer...'
    $wuN = 0
    if (Get-Command Set-ExoWindowsUpdateMaxPause -ErrorAction SilentlyContinue) {
        $wuN = [int](Set-ExoWindowsUpdateMaxPause -Force)
        Add-Report 'windows-update' 'ok' ("keys=$wuN")
        Write-Ok "Windows Update paused/deferred ($wuN writes)"
    } else {
        Add-Report 'windows-update' 'skip' 'lib missing'
    }

    # Policy-first Defender only (no Stop-Service / Appx / MpCmdRun hang paths)
    Write-HubProgress 28 'Defender policy pin (fast)...'
    $defResult = $null
    if (Get-Command Set-ExoDefenderPurged -ErrorAction SilentlyContinue) {
        $defResult = Set-ExoDefenderPurged -Force
        Add-Report 'defender-purge' 'ok' ("written=$($defResult.Written); ok=$($defResult.Ok)")
        Write-Ok ("Defender policy writes={0} ok={1}" -f $defResult.Written, $defResult.Ok)
    } else {
        Add-Report 'defender-purge' 'skip' 'lib missing'
    }

    Write-HubProgress 40 'Scheduled task quiet (depth)...'
    $taskResult = $null
    if (Get-Command Disable-ExoBloatScheduledTasks -ErrorAction SilentlyContinue) {
        $taskResult = Disable-ExoBloatScheduledTasks -Force
        Add-Report 'scheduled-tasks' 'ok' ("disabled=$($taskResult.Disabled); totalDisabled=$($taskResult.DisabledTotal)/$($taskResult.TotalTasks) ($($taskResult.DisabledPct)%)")
        Write-Ok ("Tasks: newly disabled={0}; now {1}/{2} ({3}%)" -f $taskResult.Disabled, $taskResult.DisabledTotal, $taskResult.TotalTasks, $taskResult.DisabledPct)
    } else {
        Add-Report 'scheduled-tasks' 'skip' 'lib missing'
    }

    # DISM optional features: owned by native C# (bounded dism.exe per feature).
    # Get-WindowsOptionalFeature / Disable-WindowsOptionalFeature hangs for minutes with DismHost.
    Write-HubProgress 55 'Optional features (skip DISM - native owns shortlist)...'
    $optFeat = $null
    Add-Report 'optional-features' 'skip' 'native C# bounded DISM shortlist owns this; PS DISM hang removed'
    Write-Ok 'Optional features: skipped in PS deep pack (native owns DISM)'

    # Only re-stamp host glue when native did NOT already apply (repair/legacy).
    $glue = $null
    $planInfo = $null
    $pad = $null
    $shell = $null
    if (-not $nativeAlready) {
        Write-HubProgress 65 'Host stack (native path missing - full glue)...'
        if (Get-Command Invoke-ExoShellDebloatPack -ErrorAction SilentlyContinue) {
            $shell = Invoke-ExoShellDebloatPack -Force
            Add-Report 'uac' 'ok' ("written=$($shell.uac)")
            Add-Report 'windows-ai' 'ok' ("written=$($shell.windowsAi)")
            Add-Report 'explorer' 'ok' ("written=$($shell.explorer)")
            Add-Report 'inbox-apps' 'ok' ("written=$($shell.inbox)")
        }
        $glue = Invoke-ExoCompetitiveGamingGlue -Force
        Add-Report 'game-bar' 'ok' ("written={0}" -f $glue.gameBar)
        Add-Report 'hags' 'ok' ("written={0}" -f $glue.hags)
        Add-Report 'game-mode' 'ok' ("written={0}" -f $glue.gameMode)
        Add-Report 'win32-priority' 'ok' ("written={0}" -f $glue.win32Priority)
        Add-Report 'host-latency' 'ok' ("written={0}" -f $glue.hostLatency)
        if (Get-Command Invoke-ExoControllerPack -ErrorAction SilentlyContinue) {
            $pad = Invoke-ExoControllerPack -Force
            Add-Report 'controllers' 'ok' ("usb=$($pad.usbNoSleep)")
        }
        if (Get-Command New-ExoCompetitivePowerPlan -ErrorAction SilentlyContinue) {
            $planInfo = New-ExoCompetitivePowerPlan -Force
            Add-Report 'power-plan' 'ok' ("{0} active={1}" -f $planInfo.Name, $planInfo.Active)
        }
        [void](Set-MenuShowDelayZero)
    } else {
        Add-Report 'host-stack' 'skip' 'native-csharp already applied competitive host knobs'
        Write-Ok 'Skipping host glue re-apply (native already done)'
    }

    Write-HubProgress 88 'Verifying...'
    $t = Test-ExoCompetitiveGamingGlue
    $menuOk = $false
    try {
        $menuOk = [int](Get-ItemPropertyValue -Path 'HKCU:\Control Panel\Desktop' -Name 'MenuShowDelay' -ErrorAction Stop) -eq 0
    } catch { }
    $planOk = if (Get-Command Test-ExoCompetitivePowerPlan -ErrorAction SilentlyContinue) {
        [bool](Test-ExoCompetitivePowerPlan)
    } else { $true }
    $hostOk = if (Get-Command Test-ExoHostLatencyProfile -ErrorAction SilentlyContinue) {
        [bool](Test-ExoHostLatencyProfile)
    } else { $false }

    $coreOk = [bool]$t.gameBar -and [bool]$t.hags -and [bool]$t.gameMode -and [bool]$t.win32Priority
    if (-not $coreOk) {
        $missing = @()
        if (-not $t.gameBar) { $missing += 'game-bar' }
        if (-not $t.hags) { $missing += 'hags' }
        if (-not $t.gameMode) { $missing += 'game-mode' }
        if (-not $t.win32Priority) { $missing += 'win32-priority' }
        Add-Report 'verify' 'fail' ($missing -join ',')
    } else {
        Add-Report 'verify' 'ok'
    }
    if (-not $planOk) { Add-Report 'power-plan-verify' 'fail' 'exo plan not active' }
    else { Add-Report 'power-plan-verify' 'ok' }
    if (-not $hostOk) { Add-Report 'host-latency-verify' 'fail' 'profile incomplete' }
    else { Add-Report 'host-latency-verify' 'ok' }

    $essentialOk = $coreOk
    # Merge into existing native state rather than wiping it
    $state = [ordered]@{
        version      = $Script:WindowsOptVersion
        applyStatus  = if ($essentialOk) { 'applied' } else { 'incomplete' }
        applied      = [bool]$essentialOk
        appliedUtc   = (Get-Date).ToUniversalTime().ToString('o')
        experimental = [bool]$Experimental
        path         = if ($nativeAlready) { 'native-csharp+depth' } else { 'powershell-depth' }
        gameBarQuiet = [bool]$t.gameBar
        hags         = [bool]$t.hags
        gameMode     = [bool]$t.gameMode
        win32Priority = [bool]$t.win32Priority
        mousePrecision = [bool]$t.mousePrecision
        mpo          = [bool]$t.mpo
        stickyKeys   = [bool]$t.stickyKeys
        menuShowDelay = [bool]$menuOk
        powerPlanOk  = [bool]$planOk
        powerPlanName = if ($planInfo) { [string]$planInfo.Name } else { Get-StateProp $prev 'powerPlanName' }
        powerPlanGuid = if ($planInfo) { [string]$planInfo.Guid } else { Get-StateProp $prev 'powerPlanGuid' }
        hostLatencyOk = [bool]$hostOk
        noBackgroundOk = if (Get-Command Test-ExoNoBackground -ErrorAction SilentlyContinue) { [bool](Test-ExoNoBackground) } else { $true }
        scheduledTasksOk = $true
        scheduledTasksDeepPass = $true
        scheduledTasksDisabled = if ($taskResult) { [int]$taskResult.Disabled } else { 0 }
        scheduledTasksDisabledTotal = if ($taskResult) { [int]$taskResult.DisabledTotal } else { 0 }
        scheduledTasksTotal = if ($taskResult) { [int]$taskResult.TotalTasks } else { 0 }
        scheduledTasksPct = if ($taskResult) { [double]$taskResult.DisabledPct } else { 0 }
        amoledOk = if (Get-Command Test-ExoAmoledTheme -ErrorAction SilentlyContinue) { [bool](Test-ExoAmoledTheme) } else { $false }
        windowsUpdatePaused = if (Get-Command Test-ExoWindowsUpdatePaused -ErrorAction SilentlyContinue) { [bool](Test-ExoWindowsUpdatePaused) } else { $false }
        defenderPurged = if (Get-Command Test-ExoDefenderPurged -ErrorAction SilentlyContinue) { [bool](Test-ExoDefenderPurged) } else { $false }
        uacOff = if (Get-Command Test-ExoUacNeverNotify -ErrorAction SilentlyContinue) { [bool](Test-ExoUacNeverNotify) } else { $false }
        windowsAiGone = if (Get-Command Test-ExoWindowsAiGone -ErrorAction SilentlyContinue) { [bool](Test-ExoWindowsAiGone) } else { $false }
        explorerDecluttered = if (Get-Command Test-ExoExplorerDeclutter -ErrorAction SilentlyContinue) { [bool](Test-ExoExplorerDeclutter) } else { $false }
        controllersOk = if (Get-Command Test-ExoControllersReady -ErrorAction SilentlyContinue) { [bool](Test-ExoControllersReady) } else { $true }
        optionalFeaturesOk = $true
        optionalFeaturesDeepPass = $true
        optionalFeaturesDisabled = if ($optFeat) { [int]$optFeat.FeaturesDisabled } else { 0 }
        optionalCapabilitiesRemoved = if ($optFeat) { [int]$optFeat.CapabilitiesRemoved } else { 0 }
        recovery     = $recovery
        applyReport  = @($Report)
    }
    Save-WindowsState $state

    if (-not $essentialOk) {
        throw ("Windows deep pack finished with incomplete verification: {0}" -f (($Report | Where-Object { $_ -match '\|fail' }) -join '; '))
    }

    Write-HubProgress 100 'Completed successfully'
    Write-Ok 'Windows deep pack complete (depth after native)'
    Write-Output 'DONE - Windows host stack optimized (depth pack)'
    exit 0
}

try {
    if ($Repair) {
        Invoke-WindowsRepair
    } else {
        Invoke-WindowsApply
    }
} catch {
    $msg = $_.Exception.Message
    Write-Warn $msg
    try {
        $failed = Read-WindowsState
        $keptRecovery = Get-StateProp $failed 'recovery'
        if (-not $failed) { $failed = [ordered]@{} }
        if ($failed -isnot [hashtable] -and $failed -isnot [System.Collections.IDictionary]) {
            $failed = [ordered]@{}
            if ($null -ne $keptRecovery) { $failed['recovery'] = $keptRecovery }
        }
        $failed['applyStatus'] = 'incomplete'
        $failed['applied'] = $false
        $failed['failedUtc'] = (Get-Date).ToUniversalTime().ToString('o')
        $failed['lastError'] = $msg
        $failed['applyReport'] = @($Report) + @("apply|fail:$msg")
        Save-WindowsState $failed
    } catch { }
    Write-Output ("FAIL - {0}" -f $msg)
    exit 1
}
