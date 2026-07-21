# Exo.GamingStack.ps1 - competitive gaming glue shared by optimizers.
# HAGS, Game Mode, Win32PrioritySeparation, and Game Bar quiet.
# Reversible via snapshot helpers. Dot-source from Steam/Discord/Riot/Epic/NVIDIA.

Set-StrictMode -Version Latest

# Import Game Bar helpers if not already loaded
if (-not (Get-Command Set-ExoGameBarQuiet -ErrorAction SilentlyContinue)) {
    $gb = Join-Path $PSScriptRoot 'Exo.GameBar.ps1'
    if (Test-Path -LiteralPath $gb) { . $gb }
}

function Get-ExoHagsSnapshot {
    $path = 'SYSTEM\CurrentControlSet\Control\GraphicsDrivers'
    $entry = [ordered]@{ path = $path; name = 'HwSchMode'; existed = $false; value = $null; kind = $null }
    try {
        $key = [Microsoft.Win32.Registry]::LocalMachine.OpenSubKey($path)
        if ($key) {
            try {
                if ('HwSchMode' -in @($key.GetValueNames())) {
                    $entry.existed = $true
                    $entry.value = $key.GetValue('HwSchMode')
                    $entry.kind = [string]$key.GetValueKind('HwSchMode')
                }
            } finally { $key.Dispose() }
        }
    } catch { }
    return [pscustomobject]$entry
}

function Test-ExoHagsEnabled {
    try {
        $key = [Microsoft.Win32.Registry]::LocalMachine.OpenSubKey('SYSTEM\CurrentControlSet\Control\GraphicsDrivers')
        if (-not $key) { return $false }
        try {
            $v = $key.GetValue('HwSchMode', $null)
            return ($null -ne $v -and [int]$v -eq 2)
        } finally { $key.Dispose() }
    } catch { return $false }
}

function Set-ExoHagsEnabled {
    # HwSchMode 2 = Hardware-accelerated GPU scheduling ON (supported Win10 2004+)
    param([switch]$Force)
    try {
        $key = [Microsoft.Win32.Registry]::LocalMachine.CreateSubKey('SYSTEM\CurrentControlSet\Control\GraphicsDrivers', $true)
        try {
            $cur = $key.GetValue('HwSchMode', $null)
            if (-not $Force -and $null -ne $cur -and [int]$cur -eq 2) { return 0 }
            $key.SetValue('HwSchMode', 2, [Microsoft.Win32.RegistryValueKind]::DWord)
            return 1
        } finally { $key.Dispose() }
    } catch { return 0 }
}

function Restore-ExoHagsFromSnapshot {
    param($Entry)
    if (-not $Entry) { return 0 }
    try {
        $key = [Microsoft.Win32.Registry]::LocalMachine.CreateSubKey('SYSTEM\CurrentControlSet\Control\GraphicsDrivers', $true)
        try {
            if ([bool]$Entry.existed) {
                $kind = [Microsoft.Win32.RegistryValueKind]::DWord
                if ($Entry.kind) {
                    [void][enum]::TryParse([Microsoft.Win32.RegistryValueKind], [string]$Entry.kind, $true, [ref]$kind)
                }
                $key.SetValue('HwSchMode', $Entry.value, $kind)
            } else {
                try { $key.DeleteValue('HwSchMode', $false) } catch { }
            }
            return 1
        } finally { $key.Dispose() }
    } catch { return 0 }
}

function Get-ExoGameModeSnapshot {
    $list = [System.Collections.Generic.List[object]]::new()
    foreach ($t in @(
        @{ Path = 'Software\Microsoft\GameBar'; Name = 'AutoGameModeEnabled'; Desired = 1 },
        @{ Path = 'Software\Microsoft\GameBar'; Name = 'AllowAutoGameMode'; Desired = 1 },
        @{ Path = 'System\GameConfigStore'; Name = 'GameMode_Enabled'; Desired = 1 }
    )) {
        $entry = [ordered]@{ path = $t.Path; name = $t.Name; desired = $t.Desired; existed = $false; value = $null; kind = $null }
        try {
            $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($t.Path)
            if ($key) {
                try {
                    if ($t.Name -in @($key.GetValueNames())) {
                        $entry.existed = $true
                        $entry.value = $key.GetValue($t.Name)
                        $entry.kind = [string]$key.GetValueKind($t.Name)
                    }
                } finally { $key.Dispose() }
            }
        } catch { }
        [void]$list.Add([pscustomobject]$entry)
    }
    return @($list)
}

function Test-ExoGameModeOn {
    # Require AutoGameModeEnabled=1 (primary Win10/11 signal). GameConfigStore is secondary.
    try {
        $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Software\Microsoft\GameBar')
        if (-not $key) { return $false }
        try {
            $a = $key.GetValue('AutoGameModeEnabled', $null)
            $b = $key.GetValue('AllowAutoGameMode', $null)
            $ok = ($null -ne $a -and [int]$a -eq 1) -or ($null -ne $b -and [int]$b -eq 1)
            if (-not $ok) { return $false }
        } finally { $key.Dispose() }
        # Secondary: GameMode_Enabled when present
        try {
            $gcs = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('System\GameConfigStore')
            if ($gcs) {
                try {
                    $c = $gcs.GetValue('GameMode_Enabled', $null)
                    if ($null -ne $c -and [int]$c -eq 0) { return $false }
                } finally { $gcs.Dispose() }
            }
        } catch { }
        return $true
    } catch { return $false }
}

function Set-ExoGameModeOn {
    param([switch]$Force)
    $n = 0
    foreach ($t in @(
        @{ Path = 'Software\Microsoft\GameBar'; Name = 'AutoGameModeEnabled'; Val = 1 },
        @{ Path = 'Software\Microsoft\GameBar'; Name = 'AllowAutoGameMode'; Val = 1 },
        @{ Path = 'System\GameConfigStore'; Name = 'GameMode_Enabled'; Val = 1 }
    )) {
        try {
            $key = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($t.Path, $true)
            try {
                $cur = $key.GetValue($t.Name, $null)
                if (-not $Force -and $null -ne $cur -and [int]$cur -eq [int]$t.Val) { continue }
                $key.SetValue($t.Name, [int]$t.Val, [Microsoft.Win32.RegistryValueKind]::DWord)
                $n++
            } finally { $key.Dispose() }
        } catch { }
    }
    return $n
}

function Restore-ExoGameModeFromSnapshot {
    param($Entries)
    if (-not $Entries) { return 0 }
    $n = 0
    foreach ($entry in @($Entries)) {
        try {
            $key = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey([string]$entry.path, $true)
            try {
                if ([bool]$entry.existed) {
                    $kind = [Microsoft.Win32.RegistryValueKind]::DWord
                    if ($entry.kind) {
                        [void][enum]::TryParse([Microsoft.Win32.RegistryValueKind], [string]$entry.kind, $true, [ref]$kind)
                    }
                    $key.SetValue([string]$entry.name, $entry.value, $kind)
                } else {
                    try { $key.DeleteValue([string]$entry.name, $false) } catch { }
                }
                $n++
            } finally { $key.Dispose() }
        } catch { }
    }
    return $n
}

function Get-ExoWin32PrioritySnapshot {
    $path = 'SYSTEM\CurrentControlSet\Control\PriorityControl'
    $entry = [ordered]@{ path = $path; name = 'Win32PrioritySeparation'; existed = $false; value = $null; kind = $null }
    try {
        $key = [Microsoft.Win32.Registry]::LocalMachine.OpenSubKey($path)
        if ($key) {
            try {
                if ('Win32PrioritySeparation' -in @($key.GetValueNames())) {
                    $entry.existed = $true
                    $entry.value = $key.GetValue('Win32PrioritySeparation')
                    $entry.kind = [string]$key.GetValueKind('Win32PrioritySeparation')
                }
            } finally { $key.Dispose() }
        }
    } catch { }
    return [pscustomobject]$entry
}

function Test-ExoWin32PriorityGaming {
    # 0x26 (38) = short, variable, high foreground boost  -  common competitive default
    try {
        $key = [Microsoft.Win32.Registry]::LocalMachine.OpenSubKey('SYSTEM\CurrentControlSet\Control\PriorityControl')
        if (-not $key) { return $false }
        try {
            $v = $key.GetValue('Win32PrioritySeparation', $null)
            return ($null -ne $v -and [int]$v -eq 38)
        } finally { $key.Dispose() }
    } catch { return $false }
}

function Set-ExoWin32PriorityGaming {
    param([switch]$Force)
    try {
        $key = [Microsoft.Win32.Registry]::LocalMachine.CreateSubKey('SYSTEM\CurrentControlSet\Control\PriorityControl', $true)
        try {
            $cur = $key.GetValue('Win32PrioritySeparation', $null)
            if (-not $Force -and $null -ne $cur -and [int]$cur -eq 38) { return 0 }
            $key.SetValue('Win32PrioritySeparation', 38, [Microsoft.Win32.RegistryValueKind]::DWord)
            return 1
        } finally { $key.Dispose() }
    } catch { return 0 }
}

function Restore-ExoWin32PriorityFromSnapshot {
    param($Entry)
    if (-not $Entry) { return 0 }
    try {
        $key = [Microsoft.Win32.Registry]::LocalMachine.CreateSubKey('SYSTEM\CurrentControlSet\Control\PriorityControl', $true)
        try {
            if ([bool]$Entry.existed) {
                $kind = [Microsoft.Win32.RegistryValueKind]::DWord
                if ($Entry.kind) {
                    [void][enum]::TryParse([Microsoft.Win32.RegistryValueKind], [string]$Entry.kind, $true, [ref]$kind)
                }
                $key.SetValue('Win32PrioritySeparation', $Entry.value, $kind)
            } else {
                # Stock desktop default is often 2
                $key.SetValue('Win32PrioritySeparation', 2, [Microsoft.Win32.RegistryValueKind]::DWord)
            }
            return 1
        } finally { $key.Dispose() }
    } catch { return 0 }
}

function Test-ExoMousePrecision {
    try {
        $m = Get-ItemProperty -Path 'HKCU:\Control Panel\Mouse' -ErrorAction Stop
        $speed = [string]$m.MouseSpeed
        $t1 = [string]$m.MouseThreshold1
        $t2 = [string]$m.MouseThreshold2
        return ($speed -eq '0' -and $t1 -eq '0' -and $t2 -eq '0')
    } catch { return $false }
}

function Set-ExoMousePrecision {
    param([switch]$Force)
    try {
        $path = 'HKCU:\Control Panel\Mouse'
        if (-not (Test-Path $path)) { New-Item -Path $path -Force | Out-Null }
        if (-not $Force -and (Test-ExoMousePrecision)) { return 0 }
        # 1:1 raw feel  -  common competitive / custom-OS default (Enhance pointer precision off)
        Set-ItemProperty -Path $path -Name 'MouseSpeed' -Value '0' -Type String -Force
        Set-ItemProperty -Path $path -Name 'MouseThreshold1' -Value '0' -Type String -Force
        Set-ItemProperty -Path $path -Name 'MouseThreshold2' -Value '0' -Type String -Force
        return 1
    } catch { return 0 }
}

function Test-ExoMpoDisabled {
    try {
        $v = Get-ItemProperty -Path 'HKLM:\SOFTWARE\Microsoft\Windows\Dwm' -Name 'OverlayTestMode' -ErrorAction SilentlyContinue
        return ($null -ne $v -and [int]$v.OverlayTestMode -eq 5)
    } catch { return $false }
}

function Set-ExoMpoDisabled {
    # Multi-Plane Overlay off  -  Nexus/Atlas/CTT-class fix for multi-monitor stutter / FPS drops
    param([switch]$Force)
    $n = 0
    try {
        $path = 'HKLM:\SOFTWARE\Microsoft\Windows\Dwm'
        if (-not (Test-Path $path)) { New-Item -Path $path -Force | Out-Null }
        if ($Force -or -not (Test-ExoMpoDisabled)) {
            New-ItemProperty -Path $path -Name 'OverlayTestMode' -Value 5 -PropertyType DWord -Force | Out-Null
            $n++
        }
        # CTT also sets DisableOverlays under GraphicsDrivers
        $gd = 'HKLM:\SYSTEM\CurrentControlSet\Control\GraphicsDrivers'
        if (-not (Test-Path $gd)) { New-Item -Path $gd -Force | Out-Null }
        New-ItemProperty -Path $gd -Name 'DisableOverlays' -Value 1 -PropertyType DWord -Force -ErrorAction SilentlyContinue | Out-Null
        $n++
    } catch { }
    return $n
}

function Set-ExoStickyKeysOff {
    # Competitive aim: sticky keys off (shift spam in games). UI-only; not AC-related.
    param([switch]$Force)
    try {
        $path = 'HKCU:\Control Panel\Accessibility\StickyKeys'
        if (-not (Test-Path $path)) { New-Item -Path $path -Force | Out-Null }
        # 506 = off (common gaming pack); stock often 510/511
        # Write as String  -  Windows accepts both; detect accepts int or string.
        Set-ItemProperty -Path $path -Name 'Flags' -Value '506' -Type String -Force
        return 1
    } catch { return 0 }
}

function Test-ExoStickyKeysOff {
    try {
        $raw = Get-ItemPropertyValue -Path 'HKCU:\Control Panel\Accessibility\StickyKeys' -Name 'Flags' -ErrorAction Stop
        $n = 0
        if ($raw -is [int] -or $raw -is [long]) { $n = [int]$raw }
        elseif ([int]::TryParse([string]$raw, [ref]$n)) { }
        else { return $false }
        # 506/58 = off; 0 often means never configured / disabled path  -  treat as quiet for gaming
        return ($n -eq 506 -or $n -eq 58 -or $n -eq 0)
    } catch { return $false }
}

function Set-ExoMenuSnap {
    # Snappier UI: MenuShowDelay low (CTT / performance packs)
    param([switch]$Force)
    try {
        Set-ItemProperty -Path 'HKCU:\Control Panel\Desktop' -Name 'MenuShowDelay' -Value '0' -Type String -Force
        return 1
    } catch { return 0 }
}

function Set-ExoHighPerfPower {
    # Custom Exo Competitive plan (Intel/AMD auto) with hidden powercfg knobs.
    # NEVER leave bare Ultimate Performance duplicates (that was the 50-plan spam).
    param([switch]$Force)
    if (-not (Get-Command Set-ExoCompetitivePowerPlan -ErrorAction SilentlyContinue)) {
        $pp = Join-Path $PSScriptRoot 'Exo.PowerPlan.ps1'
        if (Test-Path -LiteralPath $pp) { . $pp }
    }
    if (Get-Command Set-ExoCompetitivePowerPlan -ErrorAction SilentlyContinue) {
        return [int](Set-ExoCompetitivePowerPlan -Force:$Force)
    }
    try {
        # Fallback only: High performance — do not duplicatescheme Ultimate.
        powercfg -S SCHEME_MIN | Out-Null
        return 1
    } catch { return 0 }
}

function Set-ExoHostLatencyProfile {
    # Extra host knobs not covered by power plan: power throttling off,
    # MMCSS system responsiveness, network throttling index (games-friendly).
    param([switch]$Force)
    $n = 0
    try {
        $pt = 'HKLM:\SYSTEM\CurrentControlSet\Control\Power\PowerThrottling'
        if (-not (Test-Path $pt)) { New-Item -Path $pt -Force | Out-Null }
        New-ItemProperty -Path $pt -Name 'PowerThrottlingOff' -Value 1 -PropertyType DWord -Force | Out-Null
        $n++
    } catch { }
    try {
        $mm = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile'
        if (-not (Test-Path $mm)) { New-Item -Path $mm -Force | Out-Null }
        # MS clamps values <10 to 20 (default). 10 is the real gaming minimum that takes effect.
        New-ItemProperty -Path $mm -Name 'SystemResponsiveness' -Value 10 -PropertyType DWord -Force | Out-Null
        # Keep default-class NTI=10 (ffffffff can raise DPC/audio issues; forbidden in ExoInternetLogic)
        New-ItemProperty -Path $mm -Name 'NetworkThrottlingIndex' -Value 10 -PropertyType DWord -Force | Out-Null
        $n++
    } catch { }
    try {
        $games = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\Tasks\Games'
        if (-not (Test-Path -LiteralPath $games)) { New-Item -Path $games -Force | Out-Null }
        New-ItemProperty -Path $games -Name 'GPU Priority' -Value 8 -PropertyType DWord -Force | Out-Null
        New-ItemProperty -Path $games -Name 'Priority' -Value 6 -PropertyType DWord -Force | Out-Null
        New-ItemProperty -Path $games -Name 'Scheduling Category' -Value 'High' -PropertyType String -Force | Out-Null
        New-ItemProperty -Path $games -Name 'SFIO Priority' -Value 'High' -PropertyType String -Force | Out-Null
        $n++
    } catch { }
    return $n
}

function Test-ExoHostLatencyProfile {
    try {
        $pt = Get-ItemProperty -Path 'HKLM:\SYSTEM\CurrentControlSet\Control\Power\PowerThrottling' -Name 'PowerThrottlingOff' -ErrorAction SilentlyContinue
        if ($null -eq $pt -or [int]$pt.PowerThrottlingOff -ne 1) { return $false }
        $sr = [int](Get-ItemPropertyValue -Path 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile' -Name 'SystemResponsiveness' -ErrorAction Stop)
        # Exactly 10 = Exo competitive pin. 0 is folklore (clamps to 20). 20 is stock default.
        if ($sr -ne 10) { return $false }
        return $true
    } catch { return $false }
}

function Invoke-ExoCompetitiveGamingGlue {
    # Windows host stack only (also callable standalone). Ban-safe: no VBS/Defender/timer-res/AC.
    param([switch]$Force)
    # Ensure optional packs loaded when GamingStack is alone
    foreach ($extra in @('Exo.InputDevices.ps1', 'Exo.AmoledTheme.ps1', 'Exo.PowerPlan.ps1')) {
        if (-not (Get-Command "Set-ExoMouseGaming" -ErrorAction SilentlyContinue) -or $extra -ne 'Exo.InputDevices.ps1') {
            $p = Join-Path $PSScriptRoot $extra
            if (Test-Path -LiteralPath $p) {
                try { . $p } catch { }
            }
        }
    }
    $result = [ordered]@{
        gameBar = 0
        hags = 0
        gameMode = 0
        win32Priority = 0
        mousePrecision = 0
        mpo = 0
        stickyKeys = 0
        menuSnap = 0
        powerPlan = 0
        hostLatency = 0
        inputPack = $null
        amoled = 0
        snapshot = [ordered]@{
            gameBar = @()
            hags = $null
            gameMode = @()
            win32Priority = $null
            amoled = @()
        }
    }
    if (Get-Command Get-ExoGameBarSnapshot -ErrorAction SilentlyContinue) {
        $result.snapshot.gameBar = @(Get-ExoGameBarSnapshot)
        $result.gameBar = Set-ExoGameBarQuiet -Force:$Force
    }
    $result.snapshot.hags = Get-ExoHagsSnapshot
    $result.hags = Set-ExoHagsEnabled -Force:$Force
    $result.snapshot.gameMode = @(Get-ExoGameModeSnapshot)
    $result.gameMode = Set-ExoGameModeOn -Force:$Force
    $result.snapshot.win32Priority = Get-ExoWin32PrioritySnapshot
    $result.win32Priority = Set-ExoWin32PriorityGaming -Force:$Force
    $result.mousePrecision = Set-ExoMousePrecision -Force:$Force
    $result.mpo = Set-ExoMpoDisabled -Force:$Force
    $result.stickyKeys = Set-ExoStickyKeysOff -Force:$Force
    $result.menuSnap = Set-ExoMenuSnap -Force:$Force
    $result.powerPlan = Set-ExoHighPerfPower -Force:$Force
    $result.hostLatency = Set-ExoHostLatencyProfile -Force:$Force
    if (Get-Command Invoke-ExoInputDevicePack -ErrorAction SilentlyContinue) {
        try {
            $pg = $null
            if (Get-Command Get-ExoCompetitivePowerPlanStatus -ErrorAction SilentlyContinue) {
                $st = Get-ExoCompetitivePowerPlanStatus
                if ($st -and $st.PlanGuid) { $pg = [string]$st.PlanGuid }
            }
            $result.inputPack = Invoke-ExoInputDevicePack -Force:$Force -PowerSchemeGuid $(if ($pg) { $pg } else { '' })
            # Keep mousePrecision in sync with expanded mouse pack
            $result.mousePrecision = if (Get-Command Test-ExoMouseGaming -ErrorAction SilentlyContinue) {
                if (Test-ExoMouseGaming) { 1 } else { [int]$result.mousePrecision }
            } else { $result.mousePrecision }
        } catch { $result.inputPack = $null }
    }
    if (Get-Command Set-ExoAmoledTheme -ErrorAction SilentlyContinue) {
        try {
            if (Get-Command Get-ExoAmoledThemeSnapshot -ErrorAction SilentlyContinue) {
                $result.snapshot.amoled = @(Get-ExoAmoledThemeSnapshot)
            }
            $result.amoled = Set-ExoAmoledTheme -Force:$Force
        } catch { $result.amoled = 0 }
    }
    if (Get-Command Get-ExoCompetitivePowerPlanStatus -ErrorAction SilentlyContinue) {
        try { $result.powerPlanInfo = Get-ExoCompetitivePowerPlanStatus } catch { $result.powerPlanInfo = $null }
    } else {
        $result.powerPlanInfo = $null
    }
    return [pscustomobject]$result
}

function Test-ExoCompetitiveGamingGlue {
    $gameBar = if (Get-Command Test-ExoGameBarQuiet -ErrorAction SilentlyContinue) { [bool](Test-ExoGameBarQuiet) } else { $false }
    return [pscustomobject]@{
        gameBar = $gameBar
        hags = [bool](Test-ExoHagsEnabled)
        gameMode = [bool](Test-ExoGameModeOn)
        win32Priority = [bool](Test-ExoWin32PriorityGaming)
        mousePrecision = [bool](Test-ExoMousePrecision)
        mpo = [bool](Test-ExoMpoDisabled)
        stickyKeys = [bool](Test-ExoStickyKeysOff)
        ok = $gameBar -and (Test-ExoHagsEnabled) -and (Test-ExoGameModeOn) -and (Test-ExoWin32PriorityGaming)
    }
}

# Canonical feature copy  -  same titles/details on every module card.
function Get-ExoSharedGamingFeatureRows {
    param([pscustomobject]$State = $null)
    $t = Test-ExoCompetitiveGamingGlue
    # Fall back to state markers when live probe is soft
    # Live probe only for host glue rows  -  never promote Game Bar/HAGS from markers alone.
    # Preserve stickyKeys (StrictMode) if a soft rebuild is ever needed.
    if ($null -eq $t) {
        $t = [pscustomobject]@{
            gameBar = $false; hags = $false; gameMode = $false; win32Priority = $false
            mousePrecision = $false; mpo = $false; stickyKeys = $false; ok = $false
        }
    } elseif ($null -eq $t.PSObject.Properties['stickyKeys']) {
        $t | Add-Member -NotePropertyName stickyKeys -NotePropertyValue (
            if (Get-Command Test-ExoStickyKeysOff -ErrorAction SilentlyContinue) { [bool](Test-ExoStickyKeysOff) } else { $false }
        ) -Force
    }
    $planStatus = $null
    $planOk = $false
    if (-not (Get-Command Get-ExoCompetitivePowerPlanStatus -ErrorAction SilentlyContinue)) {
        $pp = Join-Path $PSScriptRoot 'Exo.PowerPlan.ps1'
        if (Test-Path -LiteralPath $pp) { . $pp }
    }
    if (Get-Command Get-ExoCompetitivePowerPlanStatus -ErrorAction SilentlyContinue) {
        try {
            $planStatus = Get-ExoCompetitivePowerPlanStatus
            $planOk = [bool]$planStatus.Active
        } catch { }
    }
    if (-not $planOk -and $State -and $State.PSObject.Properties.Name -contains 'powerPlanOk') {
        $planOk = [bool]$State.powerPlanOk
    }
    $hostOk = if (Get-Command Test-ExoHostLatencyProfile -ErrorAction SilentlyContinue) {
        [bool](Test-ExoHostLatencyProfile)
    } else { $false }
    if (-not $hostOk -and $State -and $State.PSObject.Properties.Name -contains 'hostLatencyOk') {
        $hostOk = [bool]$State.hostLatencyOk
    }
    if (-not (Get-Command Get-ExoInputDeviceFeatureRows -ErrorAction SilentlyContinue)) {
        $ip = Join-Path $PSScriptRoot 'Exo.InputDevices.ps1'
        if (Test-Path -LiteralPath $ip) { try { . $ip } catch { } }
    }
    if (-not (Get-Command Test-ExoAmoledTheme -ErrorAction SilentlyContinue)) {
        $ap = Join-Path $PSScriptRoot 'Exo.AmoledTheme.ps1'
        if (Test-Path -LiteralPath $ap) { try { . $ap } catch { } }
    }
    $mouseOk = if (Get-Command Test-ExoMouseGaming -ErrorAction SilentlyContinue) {
        [bool](Test-ExoMouseGaming)
    } else { [bool]$t.mousePrecision }
    $amoledOk = if (Get-Command Test-ExoAmoledTheme -ErrorAction SilentlyContinue) {
        [bool](Test-ExoAmoledTheme)
    } else { $false }

    $rows = [System.Collections.Generic.List[object]]::new()
    foreach ($row in @(
        [ordered]@{
            title  = 'Xbox Game Bar quiet'
            detail = 'No overlay pop-ins mid-clutch. DVR and Game Bar stay silent so FPS stays yours.'
            active = [bool]$t.gameBar
        },
        [ordered]@{
            title  = 'Hardware GPU scheduling'
            detail = 'Your GPU schedules work itself  -  less CPU tax, smoother frame times on modern cards.'
            active = [bool]$t.hags
        },
        [ordered]@{
            title  = 'Windows Game Mode'
            detail = 'Windows treats your game like the only thing that matters while it is focused.'
            active = [bool]$t.gameMode
        },
        [ordered]@{
            title  = 'Foreground boost'
            detail = 'What you are playing gets the CPU first. Background junk waits its turn.'
            active = [bool]$t.win32Priority
        },
        [ordered]@{
            title  = 'Smoother multi-monitor'
            detail = 'Desktop composition plane that causes FPS hitches is disabled for cleaner gaming frames.'
            active = [bool]$t.mpo
        },
        [ordered]@{
            title  = 'No sticky-key popups'
            detail = 'Shift-spam in games will not trigger Sticky Keys. Pure competitive aim setup.'
            active = [bool]$t.stickyKeys
        },
        [ordered]@{
            title  = $(if ($planStatus) { "Exo power plan ($($planStatus.Vendor))" } else { 'Exo competitive power plan' })
            detail = if ($planStatus) { [string]$planStatus.Detail } else {
                'Custom Intel/AMD power scheme with hidden CPU, PCIe, USB, and parking knobs.'
            }
            active = [bool]$planOk
        },
        [ordered]@{
            title  = 'Host latency profile'
            detail = 'Power throttling off, MMCSS SystemResponsiveness 10 (MS-safe), Games task High. No timer-res hacks.'
            active = [bool]$hostOk
        },
        [ordered]@{
            title  = 'AMOLED pure black'
            detail = 'Dark app + system theme, transparency off, black accent  -  pure black shell for OLED.'
            active = [bool]$amoledOk
        }
    )) { [void]$rows.Add($row) }

    if (Get-Command Get-ExoInputDeviceFeatureRows -ErrorAction SilentlyContinue) {
        foreach ($row in @(Get-ExoInputDeviceFeatureRows)) { [void]$rows.Add($row) }
    } else {
        [void]$rows.Add([ordered]@{
            title = 'Raw mouse feel'
            detail = 'Pointer acceleration off  -  1:1 aim, the competitive default across custom gaming OSes.'
            active = [bool]$mouseOk
        })
    }
    return @($rows)
}
