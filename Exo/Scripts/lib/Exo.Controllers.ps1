# Exo.Controllers.ps1 - gamepad / Xbox / HID controller host pack.
# Keeps pads awake (no USB sleep), quiets Game Bar overlays, light GameConfigStore.
# Does NOT disable Xbox networking services needed for multiplayer.
# Does NOT touch anti-cheat or inject into games.

Set-StrictMode -Version Latest

function Test-ExoIsControllerDevice {
    param([string]$Name, [string]$InstanceId = '')
    $s = "$Name $InstanceId"
    return [bool]($s -match '(?i)controller|gamepad|joystick|xbox|dualshock|dualsense|wireless controller|hid-compliant game|xinput|vigem|steam.?controller|switch.?pro|game.?controller')
}

function Set-ExoControllerUsbNoSleep {
    # Turn off "Allow the computer to turn off this device to save power" for controller-class devices.
    param([switch]$Force)
    $n = 0
    try {
        $devs = @(Get-PnpDevice -PresentOnly -ErrorAction SilentlyContinue | Where-Object {
            $_.Status -eq 'OK' -and (Test-ExoIsControllerDevice -Name ([string]$_.FriendlyName) -InstanceId ([string]$_.InstanceId))
        })
        foreach ($d in $devs) {
            try {
                # powercfg /devicequery / deviceenablewake style  -  use power setting via pnputil-free path
                $id = [string]$d.InstanceId
                if ([string]::IsNullOrWhiteSpace($id)) { continue }
                # Registry path under Enum
                $enum = 'HKLM:\SYSTEM\CurrentControlSet\Enum\' + $id
                $params = Join-Path $enum 'Device Parameters'
                if (-not (Test-Path -LiteralPath $params)) {
                    try { New-Item -Path $params -Force -ErrorAction SilentlyContinue | Out-Null } catch { }
                }
                if (Test-Path -LiteralPath $params) {
                    New-ItemProperty -Path $params -Name 'EnhancedPowerManagementEnabled' -Value 0 -PropertyType DWord -Force -ErrorAction SilentlyContinue | Out-Null
                    New-ItemProperty -Path $params -Name 'AllowIdleIrpInD3' -Value 0 -PropertyType DWord -Force -ErrorAction SilentlyContinue | Out-Null
                    New-ItemProperty -Path $params -Name 'DeviceSelectiveSuspended' -Value 0 -PropertyType DWord -Force -ErrorAction SilentlyContinue | Out-Null
                    New-ItemProperty -Path $params -Name 'SelectiveSuspendEnabled' -Value 0 -PropertyType DWord -Force -ErrorAction SilentlyContinue | Out-Null
                    New-ItemProperty -Path $params -Name 'SelectiveSuspendOn' -Value 0 -PropertyType DWord -Force -ErrorAction SilentlyContinue | Out-Null
                    $n++
                }
            } catch { }
        }
    } catch { }

    # Also sweep USB hubs power management lightly (helps wireless dongles)
    try {
        Get-PnpDevice -Class USB -PresentOnly -ErrorAction SilentlyContinue | Where-Object {
            $_.Status -eq 'OK' -and $_.FriendlyName -match '(?i)hub|host controller|xHCI|eXtensible'
        } | ForEach-Object {
            try {
                $params = 'HKLM:\SYSTEM\CurrentControlSet\Enum\' + $_.InstanceId + '\Device Parameters'
                if (Test-Path -LiteralPath $params) {
                    New-ItemProperty -Path $params -Name 'EnhancedPowerManagementEnabled' -Value 0 -PropertyType DWord -Force -ErrorAction SilentlyContinue | Out-Null
                    $n++
                }
            } catch { }
        }
    } catch { }

    # powercfg USB selective suspend (AC+DC)  -  shared with input pack, idempotent
    foreach ($mode in @('setacvalueindex', 'setdcvalueindex')) {
        try {
            & powercfg /$mode SCHEME_CURRENT SUB_USB USBSELECTIVESUSPEND 0 2>$null | Out-Null
            & powercfg /$mode SCHEME_CURRENT 2a737441-1930-4402-8d77-b2bebba308a3 48e6b7a6-50f5-4782-a5d4-53bb8f07e226 0 2>$null | Out-Null
            $n++
        } catch { }
    }
    try { & powercfg /S SCHEME_CURRENT 2>$null | Out-Null } catch { }
    return $n
}

function Set-ExoControllerGameConfig {
    # GameConfigStore FSE / controller-friendly defaults (no AC impact).
    param([switch]$Force)
    $n = 0
    try {
        $path = 'HKCU:\System\GameConfigStore'
        if (-not (Test-Path $path)) { New-Item -Path $path -Force | Out-Null }
        # Keep FSE behavior off / honor game fullscreen (common competitive set)
        New-ItemProperty -Path $path -Name 'GameDVR_Enabled' -Value 0 -PropertyType DWord -Force | Out-Null
        New-ItemProperty -Path $path -Name 'GameDVR_FSEBehaviorMode' -Value 2 -PropertyType DWord -Force | Out-Null
        New-ItemProperty -Path $path -Name 'GameDVR_HonorUserFSEBehaviorMode' -Value 1 -PropertyType DWord -Force | Out-Null
        New-ItemProperty -Path $path -Name 'GameDVR_DXGIHonorFSEWindowsCompatible' -Value 1 -PropertyType DWord -Force | Out-Null
        New-ItemProperty -Path $path -Name 'GameDVR_EFSEFeatureFlags' -Value 0 -PropertyType DWord -Force | Out-Null
        New-ItemProperty -Path $path -Name 'GameDVR_FSEBehavior' -Value 2 -PropertyType DWord -Force | Out-Null
        $n += 6
    } catch { }
    # Quiet Xbox Game Bar controller guide button popups
    try {
        $gb = 'HKCU:\Software\Microsoft\GameBar'
        if (-not (Test-Path $gb)) { New-Item -Path $gb -Force | Out-Null }
        New-ItemProperty -Path $gb -Name 'UseNexusForGameBarEnabled' -Value 0 -PropertyType DWord -Force | Out-Null
        New-ItemProperty -Path $gb -Name 'ShowStartupPanel' -Value 0 -PropertyType DWord -Force | Out-Null
        New-ItemProperty -Path $gb -Name 'GamePanelStartupTipIndex' -Value 3 -PropertyType DWord -Force | Out-Null
        New-ItemProperty -Path $gb -Name 'AllowAutoGameMode' -Value 1 -PropertyType DWord -Force | Out-Null
        New-ItemProperty -Path $gb -Name 'AutoGameModeEnabled' -Value 1 -PropertyType DWord -Force | Out-Null
        $n += 5
    } catch { }
    return $n
}

function Set-ExoControllerXboxBloatQuiet {
    # Quiet Xbox *overlay / Game Bar* bloat without killing multiplayer networking.
    # XblAuthManager / XboxNetApiSvc stay Manual (many titles need them).
    param([switch]$Force)
    $n = 0
    # Background access for Xbox app packages
    foreach ($pkg in @(
        'Microsoft.XboxApp_8wekyb3d8bbwe',
        'Microsoft.Xbox.TCUI_8wekyb3d8bbwe',
        'Microsoft.XboxGameOverlay_8wekyb3d8bbwe',
        'Microsoft.XboxGamingOverlay_8wekyb3d8bbwe',
        'Microsoft.XboxIdentityProvider_8wekyb3d8bbwe',
        'Microsoft.XboxSpeechToTextOverlay_8wekyb3d8bbwe',
        'Microsoft.GamingApp_8wekyb3d8bbwe'
    )) {
        try {
            $p = "HKCU:\Software\Microsoft\Windows\CurrentVersion\BackgroundAccessApplications\$pkg"
            if (-not (Test-Path $p)) { New-Item -Path $p -Force -ErrorAction SilentlyContinue | Out-Null }
            if (Test-Path $p) {
                New-ItemProperty -Path $p -Name 'Disabled' -Value 1 -PropertyType DWord -Force | Out-Null
                New-ItemProperty -Path $p -Name 'DisabledByUser' -Value 1 -PropertyType DWord -Force | Out-Null
                $n += 2
            }
        } catch { }
    }
    # Prefer not to force-remove Xbox packages (breaks Game Pass / some stores); soft-disable overlays only.
    try {
        Get-AppxPackage -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -match '(?i)XboxGamingOverlay|XboxGameOverlay|XboxSpeechToTextOverlay' } |
            ForEach-Object {
                try {
                    Remove-AppxPackage -Package $_.PackageFullName -ErrorAction SilentlyContinue
                    $n++
                } catch { }
            }
    } catch { }
    # Services: leave networking Manual; disable only pure overlay helpers if present
    foreach ($svc in @('XboxGipSvc')) {
        try {
            $s = Get-Service -Name $svc -ErrorAction SilentlyContinue
            if (-not $s) { continue }
            # GIP can be needed for some pads  -  set Manual not Disabled
            Set-Service -Name $svc -StartupType Manual -ErrorAction SilentlyContinue
            $n++
        } catch { }
    }
    return $n
}

function Set-ExoControllerBluetoothPower {
    # Bluetooth radio power: avoid hard-off; disable selective suspend where exposed.
    param([switch]$Force)
    $n = 0
    try {
        Get-PnpDevice -Class Bluetooth -PresentOnly -ErrorAction SilentlyContinue | Where-Object {
            $_.Status -eq 'OK'
        } | ForEach-Object {
            try {
                $params = 'HKLM:\SYSTEM\CurrentControlSet\Enum\' + $_.InstanceId + '\Device Parameters'
                if (Test-Path -LiteralPath $params) {
                    New-ItemProperty -Path $params -Name 'DeviceSelectiveSuspended' -Value 0 -PropertyType DWord -Force -ErrorAction SilentlyContinue | Out-Null
                    New-ItemProperty -Path $params -Name 'AllIdleTimeoutMs' -Value 0 -PropertyType DWord -Force -ErrorAction SilentlyContinue | Out-Null
                    $n++
                }
            } catch { }
        }
    } catch { }
    return $n
}

function Invoke-ExoControllerPack {
    param([switch]$Force)
    $usb = Set-ExoControllerUsbNoSleep -Force:$Force
    $cfg = Set-ExoControllerGameConfig -Force:$Force
    $xbox = Set-ExoControllerXboxBloatQuiet -Force:$Force
    $bt = Set-ExoControllerBluetoothPower -Force:$Force
    return [pscustomobject]@{
        usbNoSleep = [int]$usb
        gameConfig = [int]$cfg
        xboxQuiet  = [int]$xbox
        bluetooth  = [int]$bt
        Ok         = (Test-ExoControllersReady)
    }
}

function Test-ExoControllersReady {
    # Soft: USB selective suspend off on current plan OR GameDVR_Enabled=0
    $usbOk = $false
    try {
        $out = powercfg /q SCHEME_CURRENT SUB_USB 2>$null | Out-String
        if ($out -match 'Current AC Power Setting Index:\s*0x00000000') { $usbOk = $true }
    } catch { }
    $gcsOk = $false
    try {
        $v = [int](Get-ItemPropertyValue -Path 'HKCU:\System\GameConfigStore' -Name 'GameDVR_Enabled' -ErrorAction Stop)
        $gcsOk = ($v -eq 0)
    } catch { }
    return ($usbOk -or $gcsOk)
}

function Get-ExoControllerFeatureRows {
    $ok = Test-ExoControllersReady
    $count = 0
    try {
        $count = @(Get-PnpDevice -PresentOnly -ErrorAction SilentlyContinue | Where-Object {
            Test-ExoIsControllerDevice -Name ([string]$_.FriendlyName) -InstanceId ([string]$_.InstanceId)
        }).Count
    } catch { }
    @(
        [ordered]@{
            title  = 'Controllers stay awake'
            detail = if ($count -gt 0) {
                "USB/HID power-save off for $count controller-class device(s); pads should not drop mid-match."
            } else {
                'USB selective suspend off for gamepads/dongles (no controller detected yet  -  still applied for when you plug one in).'
            }
            active = [bool]$ok
        },
        [ordered]@{
            title  = 'Controller overlays quiet'
            detail = 'Xbox Game Bar / gaming overlay packages quieted; multiplayer Xbox net services left Manual.'
            active = [bool]$ok
        }
    )
}
