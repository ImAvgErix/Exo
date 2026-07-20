# Exo.InputDevices.ps1 - mouse, keyboard, mic, USB host pack (ban-safe).
# UI/accessibility + power only. No drivers, no HID injectors, no AC surface.

Set-StrictMode -Version Latest

function Set-ExoMouseGaming {
    # 1:1 feel + no trails/snap; does not change raw input APIs games use.
    param([switch]$Force)
    $n = 0
    try {
        $path = 'HKCU:\Control Panel\Mouse'
        if (-not (Test-Path $path)) { New-Item -Path $path -Force | Out-Null }
        # Enhance pointer precision off (MouseSpeed/Thresholds)
        Set-ItemProperty -Path $path -Name 'MouseSpeed' -Value '0' -Type String -Force
        Set-ItemProperty -Path $path -Name 'MouseThreshold1' -Value '0' -Type String -Force
        Set-ItemProperty -Path $path -Name 'MouseThreshold2' -Value '0' -Type String -Force
        Set-ItemProperty -Path $path -Name 'MouseTrails' -Value '0' -Type String -Force
        Set-ItemProperty -Path $path -Name 'MouseSensitivity' -Value '10' -Type String -Force
        Set-ItemProperty -Path $path -Name 'SnapToDefaultButton' -Value '0' -Type String -Force
        Set-ItemProperty -Path $path -Name 'SwapMouseButtons' -Value '0' -Type String -Force -ErrorAction SilentlyContinue
        $n += 6
    } catch { }
    try {
        # Hover open delay (menus/tooltips)  -  snappier desktop, not game raw input
        Set-ItemProperty -Path 'HKCU:\Control Panel\Mouse' -Name 'MouseHoverTime' -Value '10' -Type String -Force
        $n++
    } catch { }
    return $n
}

function Test-ExoMouseGaming {
    try {
        $p = 'HKCU:\Control Panel\Mouse'
        $speed = [string](Get-ItemPropertyValue -Path $p -Name 'MouseSpeed' -ErrorAction Stop)
        $t1 = [string](Get-ItemPropertyValue -Path $p -Name 'MouseThreshold1' -ErrorAction Stop)
        $t2 = [string](Get-ItemPropertyValue -Path $p -Name 'MouseThreshold2' -ErrorAction Stop)
        return ($speed -eq '0' -and $t1 -eq '0' -and $t2 -eq '0')
    } catch { return $false }
}

function Set-ExoKeyboardGaming {
    # Fast typematic + filter/toggle keys quiet. No remap/scancode hacks.
    param([switch]$Force)
    $n = 0
    try {
        $path = 'HKCU:\Control Panel\Keyboard'
        if (-not (Test-Path $path)) { New-Item -Path $path -Force | Out-Null }
        # 0 = shortest delay, 31 = fastest repeat (Windows scale)
        Set-ItemProperty -Path $path -Name 'KeyboardDelay' -Value '0' -Type String -Force
        Set-ItemProperty -Path $path -Name 'KeyboardSpeed' -Value '31' -Type String -Force
        $n += 2
    } catch { }
    try {
        $fk = 'HKCU:\Control Panel\Accessibility\Keyboard Response'
        if (-not (Test-Path $fk)) { New-Item -Path $fk -Force | Out-Null }
        # FilterKeys off (122 = common off)
        Set-ItemProperty -Path $fk -Name 'Flags' -Value '122' -Type String -Force
        $n++
    } catch { }
    try {
        $tk = 'HKCU:\Control Panel\Accessibility\ToggleKeys'
        if (-not (Test-Path $tk)) { New-Item -Path $tk -Force | Out-Null }
        Set-ItemProperty -Path $tk -Name 'Flags' -Value '58' -Type String -Force
        $n++
    } catch { }
    try {
        $mk = 'HKCU:\Control Panel\Accessibility\Keyboard Preference'
        if (-not (Test-Path $mk)) { New-Item -Path $mk -Force | Out-Null }
        Set-ItemProperty -Path $mk -Name 'On' -Value '0' -Type String -Force
        $n++
    } catch { }
    return $n
}

function Test-ExoKeyboardGaming {
    try {
        $d = [string](Get-ItemPropertyValue -Path 'HKCU:\Control Panel\Keyboard' -Name 'KeyboardDelay' -ErrorAction Stop)
        $s = [string](Get-ItemPropertyValue -Path 'HKCU:\Control Panel\Keyboard' -Name 'KeyboardSpeed' -ErrorAction Stop)
        return ($d -eq '0' -and $s -eq '31')
    } catch { return $false }
}

function Set-ExoMicCommunications {
    # Windows "Communications" tab: Do nothing when a call starts (no ducking).
    # Does not touch exclusive mode, drivers, or sample rate (AC-safe).
    param([switch]$Force)
    $n = 0
    try {
        $path = 'HKCU:\Software\Microsoft\Multimedia\Audio'
        if (-not (Test-Path $path)) { New-Item -Path $path -Force | Out-Null }
        # 3 = Do nothing (do not mute/reduce other sounds)
        New-ItemProperty -Path $path -Name 'UserDuckingPreference' -Value 3 -PropertyType DWord -Force | Out-Null
        $n++
    } catch { }
    try {
        # Background apps less aggressive with capture when present
        $cap = 'HKCU:\Software\Microsoft\Multimedia\Audio'
        New-ItemProperty -Path $cap -Name 'AccessibilityMonoMixState' -Value 0 -PropertyType DWord -Force -ErrorAction SilentlyContinue | Out-Null
    } catch { }
    return $n
}

function Test-ExoMicCommunications {
    try {
        $v = [int](Get-ItemPropertyValue -Path 'HKCU:\Software\Microsoft\Multimedia\Audio' -Name 'UserDuckingPreference' -ErrorAction Stop)
        return ($v -eq 3)
    } catch { return $false }
}

function Set-ExoUsbPowerGaming {
    # USB selective suspend off via powercfg (plan + aliases). Device drivers untouched.
    param([switch]$Force, [string]$SchemeGuid = 'SCHEME_CURRENT')
    $n = 0
    $targets = @()
    if ($SchemeGuid -and $SchemeGuid -ne 'SCHEME_CURRENT') { $targets += $SchemeGuid }
    $targets += 'SCHEME_CURRENT'
    foreach ($g in $targets) {
        foreach ($mode in @('setacvalueindex', 'setdcvalueindex')) {
            try {
                # Alias path
                & powercfg /$mode $g SUB_USB USBSELECTIVESUSPEND 0 2>$null | Out-Null
                if ($LASTEXITCODE -eq 0) { $n++ }
            } catch { }
            try {
                # GUID path (more reliable)
                & powercfg /$mode $g 2a737441-1930-4402-8d77-b2bebba308a3 48e6b7a6-50f5-4782-a5d4-53bb8f07e226 0 2>$null | Out-Null
                if ($LASTEXITCODE -eq 0) { $n++ }
            } catch { }
        }
    }
    try { & powercfg /S SCHEME_CURRENT 2>$null | Out-Null } catch { }
    # Hub selective suspend policy (soft)
    try {
        $usb = 'HKLM:\SYSTEM\CurrentControlSet\Services\USB'
        if (-not (Test-Path $usb)) { New-Item -Path $usb -Force -ErrorAction SilentlyContinue | Out-Null }
        if (Test-Path $usb) {
            New-ItemProperty -Path $usb -Name 'DisableSelectiveSuspend' -Value 1 -PropertyType DWord -Force -ErrorAction SilentlyContinue | Out-Null
            $n++
        }
    } catch { }
    return $n
}

function Test-ExoUsbPowerGaming {
    # Soft: active plan USB selective suspend AC = 0 when readable
    try {
        $out = powercfg /q SCHEME_CURRENT SUB_USB 2>$null | Out-String
        if ($out -match '(?i)USB selective suspend|USBSELECTIVESUSPEND|48e6b7a6') {
            # Look for Current AC Power Setting Index: 0x00000000
            if ($out -match 'Current AC Power Setting Index:\s*0x00000000') { return $true }
        }
    } catch { }
    try {
        $v = Get-ItemPropertyValue -Path 'HKLM:\SYSTEM\CurrentControlSet\Services\USB' -Name 'DisableSelectiveSuspend' -ErrorAction Stop
        return ([int]$v -eq 1)
    } catch { return $false }
}

function Set-ExoDesktopSnappiness {
    # Ban-safe desktop responsiveness (not game-file, not AC).
    param([switch]$Force)
    $n = 0
    try {
        # Foreground flash / lock timeout
        Set-ItemProperty -Path 'HKCU:\Control Panel\Desktop' -Name 'ForegroundLockTimeout' -Value 0 -Type DWord -Force
        Set-ItemProperty -Path 'HKCU:\Control Panel\Desktop' -Name 'MenuShowDelay' -Value '0' -Type String -Force
        Set-ItemProperty -Path 'HKCU:\Control Panel\Desktop' -Name 'AutoEndTasks' -Value '1' -Type String -Force
        Set-ItemProperty -Path 'HKCU:\Control Panel\Desktop' -Name 'HungAppTimeout' -Value '1000' -Type String -Force
        Set-ItemProperty -Path 'HKCU:\Control Panel\Desktop' -Name 'WaitToKillAppTimeout' -Value '2000' -Type String -Force
        $n += 5
    } catch { }
    try {
        # Faster startup of Startup folder apps (not disabling anything)
        $ser = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\Serialize'
        if (-not (Test-Path $ser)) { New-Item -Path $ser -Force | Out-Null }
        New-ItemProperty -Path $ser -Name 'StartupDelayInMSec' -Value 0 -PropertyType DWord -Force | Out-Null
        $n++
    } catch { }
    try {
        # Explorer: less low-priority I/O stalling feel
        $adv = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced'
        if (-not (Test-Path $adv)) { New-Item -Path $adv -Force | Out-Null }
        New-ItemProperty -Path $adv -Name 'TaskbarAnimations' -Value 0 -PropertyType DWord -Force | Out-Null
        New-ItemProperty -Path $adv -Name 'ListviewAlphaSelect' -Value 0 -PropertyType DWord -Force | Out-Null
        New-ItemProperty -Path $adv -Name 'ListviewShadow' -Value 0 -PropertyType DWord -Force | Out-Null
        $n += 3
    } catch { }
    try {
        # Visual effects: "Adjust for best performance" is heavy-handed; only disable animations
        $dwm = 'HKCU:\Software\Microsoft\Windows\DWM'
        if (-not (Test-Path $dwm)) { New-Item -Path $dwm -Force | Out-Null }
        New-ItemProperty -Path $dwm -Name 'EnableAeroPeek' -Value 0 -PropertyType DWord -Force -ErrorAction SilentlyContinue | Out-Null
        New-ItemProperty -Path $dwm -Name 'AlwaysHibernateThumbnails' -Value 0 -PropertyType DWord -Force -ErrorAction SilentlyContinue | Out-Null
        $n++
    } catch { }
    return $n
}

function Test-ExoDesktopSnappiness {
    try {
        $d = [string](Get-ItemPropertyValue -Path 'HKCU:\Control Panel\Desktop' -Name 'MenuShowDelay' -ErrorAction Stop)
        $s = 0
        try { $s = [int](Get-ItemPropertyValue -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\Serialize' -Name 'StartupDelayInMSec' -ErrorAction Stop) } catch { $s = -1 }
        return ($d -eq '0' -and $s -eq 0)
    } catch { return $false }
}

function Invoke-ExoInputDevicePack {
    param([switch]$Force, [string]$PowerSchemeGuid = '')
    $r = [ordered]@{
        mouse = Set-ExoMouseGaming -Force:$Force
        keyboard = Set-ExoKeyboardGaming -Force:$Force
        mic = Set-ExoMicCommunications -Force:$Force
        usb = Set-ExoUsbPowerGaming -Force:$Force -SchemeGuid $(if ($PowerSchemeGuid) { $PowerSchemeGuid } else { 'SCHEME_CURRENT' })
        desktop = Set-ExoDesktopSnappiness -Force:$Force
    }
    return [pscustomobject]$r
}

function Get-ExoInputDeviceFeatureRows {
    @(
        [ordered]@{
            title = 'Raw mouse feel'
            detail = 'Pointer acceleration off, no trails, no snap-to-button  -  1:1 desktop feel.'
            active = [bool](Test-ExoMouseGaming)
        },
        [ordered]@{
            title = 'Fast keyboard repeat'
            detail = 'Shortest key delay and max repeat rate; Filter/Toggle Keys stay quiet.'
            active = [bool](Test-ExoKeyboardGaming)
        },
        [ordered]@{
            title = 'No mic ducking'
            detail = 'Windows Communications set to Do nothing  -  other apps are not muted mid-game.'
            active = [bool](Test-ExoMicCommunications)
        },
        [ordered]@{
            title = 'USB always awake'
            detail = 'USB selective suspend off so mice/keyboards/headsets do not sleep mid-fight.'
            active = [bool](Test-ExoUsbPowerGaming)
        },
        [ordered]@{
            title = 'Snappy desktop'
            detail = 'Zero menu delay, no startup delay, lighter taskbar animations.'
            active = [bool](Test-ExoDesktopSnappiness)
        }
    )
}
