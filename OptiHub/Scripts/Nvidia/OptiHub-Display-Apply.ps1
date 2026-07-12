# OptiHub - Display performance apply (sticky).
#
# Uses NVAPI plus existing NVTweak device keys; it never drives the mouse or keyboard.
# -Light is used by the logon task and skips the service refresh/task registration.
param([switch]$Light)

$ErrorActionPreference = 'Continue'

function Write-DLog([string]$Msg) {
    $line = "[DISP] $Msg"
    Write-Host $line
    if ($env:OPTIHUB_LOG) {
        try { Add-Content -LiteralPath $env:OPTIHUB_LOG -Value $line -Encoding UTF8 -ErrorAction SilentlyContinue } catch { }
    }
}

$Root = Split-Path -Parent $MyInvocation.MyCommand.Path

function Get-NvDisplayExe {
    foreach ($c in @(
        (Join-Path $Root 'tools\OptiHub.NvDisplay.exe'),
        (Join-Path $env:LOCALAPPDATA 'OptiHub\scripts\Nvidia\tools\OptiHub.NvDisplay.exe'),
        (Join-Path $env:LOCALAPPDATA 'OptiHub\app\Scripts\Nvidia\tools\OptiHub.NvDisplay.exe')
    )) {
        if ($c -and (Test-Path -LiteralPath $c)) { return $c }
    }
    return $null
}

function Set-NvtweakRootPrefs {
    # Gestalt=2 => Use the advanced 3D image settings (not Let the 3D application decide)
    # NvDevToolsVisible=1 / RmProfilingAdminOnly=0 => Developer + performance counters
    foreach ($root in @(
        'HKCU:\Software\NVIDIA Corporation\Global\NVTweak',
        'HKLM:\SOFTWARE\NVIDIA Corporation\Global\NVTweak',
        'HKLM:\SYSTEM\CurrentControlSet\Services\nvlddmkm\Global\NVTweak',
        'HKLM:\SYSTEM\CurrentControlSet\Services\nvlddmkm\Parameters\Global\NVTweak'
    )) {
        if (-not (Test-Path -LiteralPath $root)) {
            try { New-Item -Path $root -Force | Out-Null } catch { continue }
        }
        Set-ItemProperty -LiteralPath $root -Name 'Gestalt' -Value 2 -Type DWord -Force -ErrorAction SilentlyContinue
        Set-ItemProperty -LiteralPath $root -Name 'NvDevToolsVisible' -Value 1 -Type DWord -Force -ErrorAction SilentlyContinue
        Set-ItemProperty -LiteralPath $root -Name 'RmProfilingAdminOnly' -Value 0 -Type DWord -Force -ErrorAction SilentlyContinue
    }
    Write-DLog 'Root NVTweak: Gestalt=2 (advanced 3D) + Developer ON + counters allowed'
}

function Set-NvidiaStoreCplVirtualHive {
    # Microsoft Store NVIDIA Control Panel does NOT read real HKCU for many prefs.
    # It uses a virtualized hive: Packages\...\SystemAppData\Helium\User.dat
    # Writing only to real HKCU is why CPL UI looked unchanged.
    Get-Process -Name 'nvcplui', 'nvcpl' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 400

    $pkg = Get-AppxPackage -ErrorAction SilentlyContinue | Where-Object {
        $_.Name -match '(?i)NVIDIAControlPanel|NVIDIACorp\.NVIDIAControlPanel'
    } | Select-Object -First 1
    if (-not $pkg) {
        Write-DLog 'Store Control Panel package not installed - skip virtual hive stamp'
        return $false
    }
    $hive = Join-Path $env:LOCALAPPDATA ("Packages\{0}\SystemAppData\Helium\User.dat" -f $pkg.PackageFamilyName)
    if (-not (Test-Path -LiteralPath $hive)) {
        Write-DLog "Store CPL virtual hive missing: $hive"
        return $false
    }

    $mount = 'HKU\OptiHubNvCpl'
    try { & reg.exe unload $mount 2>$null | Out-Null } catch { }
    $loadOut = & reg.exe load $mount $hive 2>&1
    if ($LASTEXITCODE -ne 0) {
        Write-DLog "Could not load Store CPL hive (close Control Panel fully): $loadOut"
        return $false
    }

    try {
        $virtRoot = "Registry::HKEY_USERS\OptiHubNvCpl\Software\NVIDIA Corporation\Global\NVTweak"
        if (-not (Test-Path -LiteralPath $virtRoot)) {
            New-Item -Path $virtRoot -Force | Out-Null
        }
        # Classic advanced-3D = 2. Store builds often OR 0x100 (initialized).
        # Observed wrong UI with 259 (0x103). Force 2 and 0x102 (258).
        Set-ItemProperty -LiteralPath $virtRoot -Name 'Gestalt' -Value 2 -Type DWord -Force
        Set-ItemProperty -LiteralPath $virtRoot -Name 'SedonaHasRun' -Value 1 -Type DWord -Force -ErrorAction SilentlyContinue
        Set-ItemProperty -LiteralPath $virtRoot -Name 'NvDevToolsVisible' -Value 1 -Type DWord -Force -ErrorAction SilentlyContinue
        Set-ItemProperty -LiteralPath $virtRoot -Name 'RmProfilingAdminOnly' -Value 0 -Type DWord -Force -ErrorAction SilentlyContinue

        # Copy live device stamps from real HKCU into virtual hive so CPL pages can read them
        $realDev = 'HKCU:\Software\NVIDIA Corporation\Global\NVTweak\Devices'
        $virtDev = Join-Path $virtRoot 'Devices'
        if (-not (Test-Path -LiteralPath $virtDev)) { New-Item -Path $virtDev -Force | Out-Null }
        if (Test-Path -LiteralPath $realDev) {
            Get-ChildItem -LiteralPath $realDev -ErrorAction SilentlyContinue | ForEach-Object {
                $name = $_.PSChildName
                $src = $_.PSPath
                $dst = Join-Path $virtDev $name
                if (-not (Test-Path -LiteralPath $dst)) { New-Item -Path $dst -Force | Out-Null }
                Set-OneNvtweakDevice -devPath $dst
                # Prefer values already proven on real hive when present
                try {
                    $srcProps = Get-ItemProperty -LiteralPath $src -ErrorAction SilentlyContinue
                    foreach ($pn in @('ScalingOverride','PerformScalingOn','ScalingMode','Scaling')) {
                        if ($null -ne $srcProps.$pn) {
                            Set-ItemProperty -LiteralPath $dst -Name $pn -Value ([int]$srcProps.$pn) -Type DWord -Force -ErrorAction SilentlyContinue
                        }
                    }
                } catch { }
            }
        }

        $g = [int](Get-ItemProperty -LiteralPath $virtRoot -Name 'Gestalt' -ErrorAction SilentlyContinue).Gestalt
        Write-DLog "Store CPL virtual hive stamped (Gestalt=$g, devices mirrored)"
        return ($g -eq 2)
    } catch {
        Write-DLog "Store CPL virtual hive stamp failed: $($_.Exception.Message)"
        return $false
    } finally {
        try { & reg.exe unload $mount 2>&1 | Out-Null } catch { }
    }
}

function Set-OneNvtweakDevice([string]$devPath) {
    if (-not (Test-Path -LiteralPath $devPath)) {
        try { New-Item -Path $devPath -Force | Out-Null } catch { return }
    }

    # --- Adjust desktop size and position: GPU + No scaling + Override the scaling mode ---
    $scaleMap = @{
        PerformScalingOn          = 0  # GPU
        ScalingDevice             = 0  # GPU
        ScalingOverride           = 1
        AppControlledScaling      = 0
        ScalingMode               = 2  # No scaling
        Scaling                   = 2
        FlatPanelScaling          = 2
        OverlayScaling            = 2
        PreferredScalingMode      = 2
        GpuScaling                = 1
        DisplayScaling            = 0
        OverrideScalingMode       = 1
        bOverrideScaling          = 1
        ScalingModeOverride       = 1
        PreferGpuScaling          = 1
        ForceGpuScaling           = 1
        isOverrideScalingEnabled  = 1
        scalingMethod             = 3
    }
    foreach ($kv in $scaleMap.GetEnumerator()) {
        Set-ItemProperty -LiteralPath $devPath -Name $kv.Key -Value ([int]$kv.Value) -Type DWord -Force -ErrorAction SilentlyContinue
    }

    # --- Adjust desktop color settings: Use NVIDIA settings + Full dynamic range + RGB ---
    $colorPath = Join-Path $devPath 'Color'
    if (-not (Test-Path -LiteralPath $colorPath)) { New-Item -Path $colorPath -Force | Out-Null }
    $colorMap = @{
        NvCplUseColorSettings   = 1  # Use NVIDIA settings
        ColorFormat             = 0  # RGB
        NvCplColorFormat        = 0
        NvCplDigitalColorFormat = 0
        DynamicRange            = 0  # Full (VESA)
        NvCplDynamicRange       = 0
    }
    foreach ($kv in $colorMap.GetEnumerator()) {
        Set-ItemProperty -LiteralPath $colorPath -Name $kv.Key -Value ([int]$kv.Value) -Type DWord -Force -ErrorAction SilentlyContinue
    }
    # Do not hard-code color depth; NVAPI picks a depth valid for the active mode.

    # --- Adjust video color / image settings: Use NVIDIA settings on EVERY monitor ---
    $videoPath = Join-Path $devPath 'Video'
    if (-not (Test-Path -LiteralPath $videoPath)) { New-Item -Path $videoPath -Force | Out-Null }
    $videoMap = @{
        VideoColorSettingsSource  = 1  # NVIDIA
        VideoImageSettingsSource  = 1  # NVIDIA
        VideoColorSettings        = 1
        VideoImageSettings        = 1
        UseNVIDIAColorSettings    = 1
        UseNVIDIAImageSettings    = 1
        ColorSetting              = 1
        EdgeEnhanceSetting        = 1
        NoiseReductionSetting     = 1
        EdgeEnhanceSource         = 1
        NoiseReductionSource      = 1
        DynamicRange              = 0  # Full
        ColorRange                = 0  # Full
    }
    foreach ($kv in $videoMap.GetEnumerator()) {
        Set-ItemProperty -LiteralPath $videoPath -Name $kv.Key -Value ([int]$kv.Value) -Type DWord -Force -ErrorAction SilentlyContinue
    }
}

function Set-AllNvtweakDevices {
    Set-NvtweakRootPrefs

    $deviceRoots = @(
        'HKCU:\Software\NVIDIA Corporation\Global\NVTweak\Devices',
        'HKLM:\SOFTWARE\NVIDIA Corporation\Global\NVTweak\Devices'
    )
    $total = 0
    foreach ($devicesRoot in $deviceRoots) {
        if (-not (Test-Path -LiteralPath $devicesRoot)) {
            try { New-Item -Path $devicesRoot -Force | Out-Null } catch { continue }
        }
        $names = @(Get-ChildItem -LiteralPath $devicesRoot -ErrorAction SilentlyContinue | ForEach-Object { $_.PSChildName })
        Write-DLog "NVTweak stamp: $($names.Count) device key(s) under $devicesRoot"
        if ($names.Count -eq 0) {
            Write-DLog '  (no driver-created keys yet; NVAPI still applies active displays)'
        }
        foreach ($name in $names) {
            $devPath = Join-Path $devicesRoot $name
            Set-OneNvtweakDevice -devPath $devPath
            $total++
            $d = Get-ItemProperty -LiteralPath $devPath -ErrorAction SilentlyContinue
            $c = Get-ItemProperty -LiteralPath (Join-Path $devPath 'Color') -ErrorAction SilentlyContinue
            $v = Get-ItemProperty -LiteralPath (Join-Path $devPath 'Video') -ErrorAction SilentlyContinue
            Write-DLog ("  {0}: Override={1} NoScale={2} ColorNvidia={3} Full={4} VideoNvidia={5}" -f `
                $name,
                ([int]$d.ScalingOverride -eq 1),
                ([int]$d.ScalingMode -eq 2),
                ([int]$c.NvCplUseColorSettings -eq 1),
                ([int]$c.DynamicRange -eq 0),
                ([int]$v.VideoColorSettingsSource -eq 1 -and [int]$v.VideoImageSettingsSource -eq 1))
        }
    }
    Write-DLog "NVTweak stamped $total device path(s) (scaling override + desktop color Full + video NVIDIA)"

    # Remove markers written by older OptiHub builds.
    foreach ($legacyClient in @(
        'HKCU:\Software\NVIDIA Corporation\NVControlPanel2\Client',
        'HKLM:\SOFTWARE\NVIDIA Corporation\NVControlPanel2\Client'
    )) {
        if (-not (Test-Path -LiteralPath $legacyClient)) { continue }
        foreach ($name in @(
            'OptiHubPreferGpuScaling', 'OptiHubPreferNoScaling',
            'OptiHubPreferScalingOverride', 'OptiHubPreferFullRgb'
        )) {
            Remove-ItemProperty -LiteralPath $legacyClient -Name $name -Force -ErrorAction SilentlyContinue
        }
    }
}

function Clear-NvidiaAppTrayAndContainer {
    # Soft container refresh can re-arm App tray ghosts / App container service.
    try {
        $svc = Get-Service -Name 'NvContainerLocalSystem' -ErrorAction SilentlyContinue
        if ($svc) {
            if ($svc.Status -ne 'Stopped') { Stop-Service -Name 'NvContainerLocalSystem' -Force -ErrorAction SilentlyContinue }
            Set-Service -Name 'NvContainerLocalSystem' -StartupType Disabled -ErrorAction SilentlyContinue
            Write-DLog 'NvContainerLocalSystem forced Stopped/Disabled (App stack)'
        }
    } catch { }

    $notify = 'HKCU:\Control Panel\NotifyIconSettings'
    if (-not (Test-Path -LiteralPath $notify)) { return }
    $removed = 0
    Get-ChildItem -LiteralPath $notify -ErrorAction SilentlyContinue | ForEach-Object {
        $exe = $null
        try { $exe = [string](Get-ItemProperty -LiteralPath $_.PSPath -ErrorAction SilentlyContinue).ExecutablePath } catch { }
        if ([string]::IsNullOrWhiteSpace($exe)) { return }
        # Keep DriverStore Display.NvContainer (display driver); strip App nvcontainer paths
        if ($exe -match '(?i)DriverStore\\.*Display\.NvContainer|NVDisplay\.Container') { return }
        if ($exe -match '(?i)NVIDIA Corporation\\NvContainer\\nvcontainer|NVIDIA Corporation\\NVIDIA App|NVIDIA Overlay|GeForce Experience|ShadowPlay|nvsphelper|NVIDIA App\.exe') {
            try {
                Remove-Item -LiteralPath $_.PSPath -Recurse -Force -ErrorAction Stop
                $removed++
                Write-DLog "Removed tray ghost: $exe"
            } catch { }
        }
    }
    if ($removed -gt 0) { Write-DLog "Cleared $removed NVIDIA App tray ghost(s)" }
}

function Invoke-NvApiHelper {
    $exe = Get-NvDisplayExe
    if (-not $exe) {
        Write-DLog 'WARN: OptiHub.NvDisplay.exe missing'
        return $false
    }
    Write-DLog "NVAPI: $exe"
    # PS7 Preview / .NET Core: ArgumentList + async stream reads.
    $psi = [System.Diagnostics.ProcessStartInfo]::new()
    $psi.FileName = $exe
    $psi.ArgumentList.Add('--apply')
    $psi.WorkingDirectory = (Split-Path -Parent $exe)
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.CreateNoWindow = $true
    $proc = $null
    try {
        $proc = [System.Diagnostics.Process]::Start($psi)
        if (-not $proc) { throw 'Process did not start' }
        $stdoutTask = $proc.StandardOutput.ReadToEndAsync()
        $stderrTask = $proc.StandardError.ReadToEndAsync()
        if (-not $proc.WaitForExit(180000)) {
            try { $proc.Kill($true) } catch { try { $proc.Kill() } catch { } }
            Write-DLog 'NVAPI timeout'
            return $false
        }
        $stdout = $stdoutTask.GetAwaiter().GetResult()
        $stderr = $stderrTask.GetAwaiter().GetResult()
        foreach ($line in ($stdout -split "`r?`n")) { if ($line) { Write-DLog $line.TrimEnd() } }
        foreach ($line in ($stderr -split "`r?`n")) { if ($line) { Write-DLog "ERR $line" } }
        Write-DLog "NVAPI exit $($proc.ExitCode)"
        return ($proc.ExitCode -eq 0)
    } catch {
        Write-DLog "NVAPI launch failed: $($_.Exception.Message)"
        return $false
    } finally {
        if ($proc) { try { $proc.Dispose() } catch { } }
    }
}

function Invoke-SoftDriverRefresh {
    Write-DLog 'Soft refresh: NVDisplay.ContainerLocalSystem'
    Get-Process nvcplui -EA 0 | Stop-Process -Force -EA 0
    try {
        Restart-Service -Name 'NVDisplay.ContainerLocalSystem' -Force -ErrorAction Stop
        Start-Sleep -Seconds 3
        Write-DLog 'Container restarted'
        return $true
    } catch {
        Write-DLog "Soft refresh failed: $($_.Exception.Message)"
        return $false
    }
}

function Unregister-LegacyPersistTask {
    # Logon tasks are background overhead - OptiHub no longer registers any.
    # Remove leftovers from older builds.
    foreach ($taskName in @('OptiHub-NvidiaDisplayPersist', 'OptiHub-NvidiaBackgroundPersist')) {
        try {
            schtasks /Delete /TN $taskName /F 2>&1 | Out-Null
            if ($LASTEXITCODE -eq 0) {
                Write-DLog "Removed legacy task: $taskName"
            }
        } catch { }
    }
}

# ---- main ----
Write-DLog '=== Display apply (no logon task) ==='
$light = [bool]$Light -or ($env:OPTIHUB_DISPLAY_LIGHT -eq '1')
if (-not $light) {
    Get-Process nvcplui, nvcpl -EA 0 | Stop-Process -Force -EA 0
    Unregister-LegacyPersistTask
}

# 1) Registry first (advanced 3D + scaling/color/video on every device key)
Set-AllNvtweakDevices
Clear-NvidiaAppTrayAndContainer

if (-not $light) {
    # Refresh the user-mode display container once before the final NVAPI apply.
    [void](Invoke-SoftDriverRefresh)
    # Container restart rewrites NVTweak / can re-arm App tray - stamp + clean again
    Set-AllNvtweakDevices
    Clear-NvidiaAppTrayAndContainer
}

# 2) Final NVAPI modes/color/path apply, then registry stamp AGAIN (authoritative).
# Sticky without a scheduled task: registry + NVAPI persist until a driver reset;
# re-run NVIDIA Apply in OptiHub if prefs drift.
$nvApiOk = Invoke-NvApiHelper
Set-AllNvtweakDevices
Clear-NvidiaAppTrayAndContainer
Set-NvtweakRootPrefs
# Critical: stamp Store CPL virtual hive (real HKCU alone does not update CPL UI)
[void](Set-NvidiaStoreCplVirtualHive)
Get-Process nvcplui, nvcpl -EA 0 | Stop-Process -Force -EA 0

# Verify any driver-created registry keys. NVAPI success remains required.
$registryOk = $true
$deviceKeys = @(Get-ChildItem 'HKCU:\Software\NVIDIA Corporation\Global\NVTweak\Devices' -EA 0)
if ($deviceKeys.Count -eq 0) {
    Write-DLog 'VERIFY: no HKCU device keys (NVAPI-only path)'
}
$deviceKeys | ForEach-Object {
    $d = Get-ItemProperty $_.PSPath -ErrorAction SilentlyContinue
    $c = Get-ItemProperty (Join-Path $_.PSPath 'Color') -ErrorAction SilentlyContinue
    $v = Get-ItemProperty (Join-Path $_.PSPath 'Video') -ErrorAction SilentlyContinue
    $okScale = ([int]$d.PerformScalingOn -eq 0 -and [int]$d.ScalingOverride -eq 1 -and [int]$d.ScalingMode -eq 2)
    $okColor = ($null -ne $c -and [int]$c.NvCplUseColorSettings -eq 1 -and [int]$c.DynamicRange -eq 0)
    $okVideo = ($null -ne $v -and [int]$v.VideoColorSettingsSource -eq 1 -and [int]$v.VideoImageSettingsSource -eq 1)
    if (-not ($okScale -and $okColor -and $okVideo)) {
        Write-DLog "VERIFY FAIL $($_.PSChildName) scale=$okScale color=$okColor video=$okVideo"
        $registryOk = $false
    } else {
        Write-DLog "VERIFY OK $($_.PSChildName): Override + Full RGB + Video NVIDIA"
    }
}
$g = $null
try { $g = [int](Get-ItemProperty 'HKCU:\Software\NVIDIA Corporation\Global\NVTweak' -Name Gestalt -EA Stop).Gestalt } catch { }
if ($g -ne 2) {
    Write-DLog "VERIFY FAIL Gestalt=$g (want 2 advanced 3D)"
    $registryOk = $false
} else {
    Write-DLog 'VERIFY OK Gestalt=2 (advanced 3D image settings)'
}
$ok = [bool]$nvApiOk -and [bool]$registryOk

if ($ok) {
    Write-DLog 'SUCCESS'
    Write-DLog 'Active NVIDIA displays were applied without Control Panel mouse/keyboard automation.'
    exit 0
}
Write-DLog 'FAIL verify'
exit 1
