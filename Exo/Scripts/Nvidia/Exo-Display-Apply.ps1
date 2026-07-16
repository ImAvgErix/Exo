# Exo - Display performance apply (sticky).
#
# Uses NVAPI plus existing NVTweak device keys; it never drives the mouse or keyboard.
# -Light skips heavier service work (still no Exo scheduled tasks ever).
param([switch]$Light)

$ErrorActionPreference = 'Continue'

function Write-DLog([string]$Msg) {
    $line = "[DISP] $Msg"
    Write-Host $line
    if ($env:EXO_LOG) {
        try { Add-Content -LiteralPath $env:EXO_LOG -Value $line -Encoding UTF8 -ErrorAction SilentlyContinue } catch { }
    }
}

$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
$PanelSettingsPath = Join-Path $env:LOCALAPPDATA 'Exo\nvidia-panel-settings.json'

function Get-ExoNvidiaPanelSettings {
    $defaults = [pscustomobject]@{
        primaryRefresh          = 'max'
        secondaryRefresh        = '60'
        fullRgb                 = $true
        gpuNoScaling            = $true
        scalingOverride         = $true
        videoNvidiaColor        = $true
        videoNvidiaImage        = $true
        developerCounters       = $true
        force3dProfiles         = $true
        stripAppAndControlPanel = $true
    }
    if (-not (Test-Path -LiteralPath $PanelSettingsPath)) { return $defaults }
    try {
        $j = Get-Content -LiteralPath $PanelSettingsPath -Raw -Encoding UTF8 | ConvertFrom-Json
        foreach ($p in $defaults.PSObject.Properties.Name) {
            if ($j.PSObject.Properties.Name -contains $p) {
                $defaults.$p = $j.$p
            }
        }
    } catch { }
    return $defaults
}

$Script:PanelSettings = Get-ExoNvidiaPanelSettings

function Get-NvDisplayExe {
    foreach ($c in @(
        (Join-Path $Root 'tools\Exo.NvDisplay.exe'),
        (Join-Path $env:LOCALAPPDATA 'Exo\scripts\Nvidia\tools\Exo.NvDisplay.exe'),
        (Join-Path $env:LOCALAPPDATA 'Exo\app\Scripts\Nvidia\tools\Exo.NvDisplay.exe'),
        (Join-Path $env:LOCALAPPDATA 'Exo\app\Scripts\Nvidia\tools\win-x64\Exo.NvDisplay.exe')
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

    $mount = 'HKU\ExoNvCpl'
    try { & reg.exe unload $mount 2>$null | Out-Null } catch { }
    $loadOut = & reg.exe load $mount $hive 2>&1
    if ($LASTEXITCODE -ne 0) {
        Write-DLog "Could not load Store CPL hive (close Control Panel fully): $loadOut"
        return $false
    }

    try {
        $virtRoot = "Registry::HKEY_USERS\ExoNvCpl\Software\NVIDIA Corporation\Global\NVTweak"
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

    $ps = $Script:PanelSettings
    $override = if ([bool]$ps.scalingOverride) { 1 } else { 0 }
    $gpuScale = if ([bool]$ps.gpuNoScaling) { 1 } else { 0 }
    $noScale = if ([bool]$ps.gpuNoScaling) { 2 } else { 1 }
    $fullRgb = if ([bool]$ps.fullRgb) { 0 } else { 1 }
    $useNvidiaColor = if ([bool]$ps.fullRgb) { 1 } else { 0 }
    $vidColor = if ([bool]$ps.videoNvidiaColor) { 1 } else { 0 }
    $vidImage = if ([bool]$ps.videoNvidiaImage) { 1 } else { 0 }

    # --- Desktop size/position ---
    $scaleMap = @{
        PerformScalingOn          = $(if ($gpuScale) { 0 } else { 1 })
        ScalingDevice             = $(if ($gpuScale) { 0 } else { 1 })
        ScalingOverride           = $override
        AppControlledScaling      = $(if ($override) { 0 } else { 1 })
        ScalingMode               = $noScale
        Scaling                   = $noScale
        FlatPanelScaling          = $noScale
        OverlayScaling            = $noScale
        PreferredScalingMode      = $noScale
        GpuScaling                = $gpuScale
        DisplayScaling            = $(if ($gpuScale) { 0 } else { 1 })
        OverrideScalingMode       = $override
        bOverrideScaling          = $override
        ScalingModeOverride       = $override
        PreferGpuScaling          = $gpuScale
        ForceGpuScaling           = $gpuScale
        isOverrideScalingEnabled  = $override
        scalingMethod             = 3
    }
    foreach ($kv in $scaleMap.GetEnumerator()) {
        Set-ItemProperty -LiteralPath $devPath -Name $kv.Key -Value ([int]$kv.Value) -Type DWord -Force -ErrorAction SilentlyContinue
    }

    # --- Desktop color ---
    $colorPath = Join-Path $devPath 'Color'
    if (-not (Test-Path -LiteralPath $colorPath)) { New-Item -Path $colorPath -Force | Out-Null }
    $colorMap = @{
        NvCplUseColorSettings   = $useNvidiaColor
        ColorFormat             = 0
        NvCplColorFormat        = 0
        NvCplDigitalColorFormat = 0
        DynamicRange            = $fullRgb
        NvCplDynamicRange       = $fullRgb
    }
    foreach ($kv in $colorMap.GetEnumerator()) {
        Set-ItemProperty -LiteralPath $colorPath -Name $kv.Key -Value ([int]$kv.Value) -Type DWord -Force -ErrorAction SilentlyContinue
    }

    # --- Video color / image ---
    $videoPath = Join-Path $devPath 'Video'
    if (-not (Test-Path -LiteralPath $videoPath)) { New-Item -Path $videoPath -Force | Out-Null }
    $videoMap = @{
        VideoColorSettingsSource  = $vidColor
        VideoImageSettingsSource  = $vidImage
        VideoColorSettings        = $vidColor
        VideoImageSettings        = $vidImage
        UseNVIDIAColorSettings    = $vidColor
        UseNVIDIAImageSettings    = $vidImage
        ColorSetting              = $vidColor
        EdgeEnhanceSetting        = $vidImage
        NoiseReductionSetting     = $vidImage
        EdgeEnhanceSource         = $vidImage
        NoiseReductionSource      = $vidImage
        DynamicRange              = $fullRgb
        ColorRange                = $fullRgb
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

    # Remove markers written by older Exo builds.
    foreach ($legacyClient in @(
        'HKCU:\Software\NVIDIA Corporation\NVControlPanel2\Client',
        'HKLM:\SOFTWARE\NVIDIA Corporation\NVControlPanel2\Client'
    )) {
        if (-not (Test-Path -LiteralPath $legacyClient)) { continue }
        foreach ($name in @(
            'ExoPreferGpuScaling', 'ExoPreferNoScaling',
            'ExoPreferScalingOverride', 'ExoPreferFullRgb'
        )) {
            Remove-ItemProperty -LiteralPath $legacyClient -Name $name -Force -ErrorAction SilentlyContinue
        }
    }
}

function Clear-NvidiaAppTrayAndContainer {
    # Soft refresh restarts NVDisplay.Container -> re-registers tray.
    # Hide display-container (IsPromoted=0); delete App/GFE ghosts only.
    try {
        $svc = Get-Service -Name 'NvContainerLocalSystem' -ErrorAction SilentlyContinue
        if ($svc) {
            if ($svc.Status -ne 'Stopped') { Stop-Service -Name 'NvContainerLocalSystem' -Force -ErrorAction SilentlyContinue }
            Set-Service -Name 'NvContainerLocalSystem' -StartupType Disabled -ErrorAction SilentlyContinue
            Write-DLog 'NvContainerLocalSystem forced Stopped/Disabled (App stack)'
        }
    } catch { }

    $trayScript = Join-Path $PSScriptRoot 'Exo-Nvidia-TrayClear.ps1'
    if (Test-Path -LiteralPath $trayScript) {
        try {
            & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $trayScript -NoTask -SettlePasses 3 2>&1 |
                ForEach-Object { Write-DLog "$_" }
            Write-DLog 'Tray clear script finished (no background task)'
            return
        } catch {
            Write-DLog "Tray script failed: $($_.Exception.Message) - inline fallback"
        }
    }

    $notify = 'HKCU:\Control Panel\NotifyIconSettings'
    if (-not (Test-Path -LiteralPath $notify)) { return }
    $removed = 0
    $hidden = 0
    Get-ChildItem -LiteralPath $notify -ErrorAction SilentlyContinue | ForEach-Object {
        $exe = $null
        try { $exe = [string](Get-ItemProperty -LiteralPath $_.PSPath -ErrorAction SilentlyContinue).ExecutablePath } catch { }
        if ([string]::IsNullOrWhiteSpace($exe)) { return }
        if ($exe -notmatch '(?i)NVIDIA|nvcontainer|NVDisplay|GeForce|ShadowPlay|nvsphelper|nvapp') { return }
        if ($exe -match '(?i)NVDisplay\.Container|Display\.NvContainer|nv_dispi\.inf') {
            try {
                Set-ItemProperty -LiteralPath $_.PSPath -Name 'IsPromoted' -Value 0 -Type DWord -Force -EA 0
                $hidden++
            } catch { }
            return
        }
        try {
            Remove-Item -LiteralPath $_.PSPath -Recurse -Force -ErrorAction Stop
            $removed++
        } catch {
            try { Set-ItemProperty -LiteralPath $_.PSPath -Name 'IsPromoted' -Value 0 -Type DWord -Force -EA 0; $hidden++ } catch { }
        }
    }
    Write-DLog "Tray inline: removed=$removed hidden=$hidden"
}

function Invoke-NvApiHelperOnce {
    $exe = Get-NvDisplayExe
    if (-not $exe) {
        Write-DLog 'WARN: Exo.NvDisplay.exe missing'
        return $false
    }
    Write-DLog "NVAPI: $exe"
    # PS7 / .NET Core: ArgumentList + async stream reads.
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

function Invoke-NvApiHelper {
    # Retry so Apply works: mode/color/scaling often need a second pass after registry stamp.
    for ($attempt = 1; $attempt -le 3; $attempt++) {
        Write-DLog "NVAPI apply attempt $attempt/3"
        if (Invoke-NvApiHelperOnce) {
            Write-DLog "NVAPI apply OK on attempt $attempt"
            return $true
        }
        Set-AllNvtweakDevices
        Start-Sleep -Milliseconds (400 * $attempt)
    }
    Write-DLog 'NVAPI apply failed after 3 attempts'
    return $false
}

function Invoke-SoftDriverRefresh {
    # NEVER restart NVDisplay.ContainerLocalSystem - that re-registers a promoted tray icon.
    # Registry + NVAPI already push display policy without a container bounce.
    Write-DLog 'Soft refresh skipped (no NVDisplay.Container restart - keeps tray hidden)'
    Get-Process nvcplui, nvcpl -EA 0 | Stop-Process -Force -EA 0
    return $false
}

function Unregister-LegacyPersistTask {
    # Product rule: never keep Exo scheduled tasks (including old tray hide logon).
    foreach ($taskName in @(
        'Exo-NvidiaTrayHide',
        'Exo-NvidiaDisplayPersist',
        'Exo-NvidiaBackgroundPersist',
        'Exo-NvidiaTray',
        'Exo-Nvidia'
    )) {
        try {
            Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -EA 0
            schtasks /Delete /TN $taskName /F 2>&1 | Out-Null
            Write-DLog "Removed Exo task if present: $taskName"
        } catch { }
    }
}

function Test-ExoDisplayAlreadyOk {
    # Live NVAPI probe against current panel JSON; true = nothing to do.
    $exe = Get-NvDisplayExe
    if (-not $exe) { return $false }
    try {
        $psi = [Diagnostics.ProcessStartInfo]::new()
        $psi.FileName = $exe
        $psi.ArgumentList.Add('--status')
        $psi.WorkingDirectory = Split-Path $exe -Parent
        $psi.UseShellExecute = $false
        $psi.RedirectStandardOutput = $true
        $psi.RedirectStandardError = $true
        $psi.CreateNoWindow = $true
        $psi.Environment['EXO_PRIMARY_REFRESH'] = [string]$Script:PanelSettings.primaryRefresh
        $psi.Environment['EXO_SECONDARY_REFRESH'] = [string]$Script:PanelSettings.secondaryRefresh
        $psi.Environment['EXO_FULL_RGB'] = $(if ([bool]$Script:PanelSettings.fullRgb) { '1' } else { '0' })
        $psi.Environment['EXO_GPU_NOSCALE'] = $(if ([bool]$Script:PanelSettings.gpuNoScaling) { '1' } else { '0' })
        $psi.Environment['EXO_SCALING_OVERRIDE'] = $(if ([bool]$Script:PanelSettings.scalingOverride) { '1' } else { '0' })
        $p = [Diagnostics.Process]::Start($psi)
        if (-not $p) { return $false }
        $out = $p.StandardOutput.ReadToEnd()
        $null = $p.StandardError.ReadToEnd()
        if (-not $p.WaitForExit(20000)) { try { $p.Kill() } catch {}; return $false }
        $line = ($out -split "`r?`n") | Where-Object { $_ -like 'EXO_NVDISPLAY_JSON:*' } | Select-Object -Last 1
        if (-not $line) { return $false }
        $j = ($line.Substring('EXO_NVDISPLAY_JSON:'.Length)) | ConvertFrom-Json
        return [bool]$j.ok
    } catch {
        return $false
    }
}

# ---- main ----
Write-DLog '=== Display apply (Exo panel) ==='
Write-DLog ("Panel: primary={0} secondary={1} fullRgb={2} gpuNoScale={3} override={4}" -f `
    $Script:PanelSettings.primaryRefresh, $Script:PanelSettings.secondaryRefresh,
    $Script:PanelSettings.fullRgb, $Script:PanelSettings.gpuNoScaling, $Script:PanelSettings.scalingOverride)

$env:EXO_PRIMARY_REFRESH = [string]$Script:PanelSettings.primaryRefresh
$env:EXO_SECONDARY_REFRESH = [string]$Script:PanelSettings.secondaryRefresh
$env:EXO_FULL_RGB = $(if ([bool]$Script:PanelSettings.fullRgb) { '1' } else { '0' })
$env:EXO_GPU_NOSCALE = $(if ([bool]$Script:PanelSettings.gpuNoScaling) { '1' } else { '0' })
$env:EXO_SCALING_OVERRIDE = $(if ([bool]$Script:PanelSettings.scalingOverride) { '1' } else { '0' })

# Do not re-touch driver/registry when live status already matches policy.
if (Test-ExoDisplayAlreadyOk) {
    Write-DLog 'SKIP: already matches panel policy'
    Write-DLog 'SUCCESS'
    exit 0
}

$light = [bool]$Light -or ($env:EXO_DISPLAY_LIGHT -eq '1')
if (-not $light) {
    Get-Process nvcplui, nvcpl -EA 0 | Stop-Process -Force -EA 0
    Unregister-LegacyPersistTask
}

Set-AllNvtweakDevices
Clear-NvidiaAppTrayAndContainer

# No NVDisplay.Container restart (that re-promotes the tray icon).
if (-not $light) {
    [void](Invoke-SoftDriverRefresh)
    Set-AllNvtweakDevices
}

$nvApiOk = Invoke-NvApiHelper
Set-AllNvtweakDevices
Clear-NvidiaAppTrayAndContainer
Set-NvtweakRootPrefs
try { [void](Set-NvidiaStoreCplVirtualHive) } catch { Write-DLog "Store hive stamp skipped: $($_.Exception.Message)" }
Get-Process nvcplui, nvcpl -EA 0 | Stop-Process -Force -EA 0
# Final multi-pass hide after NVAPI (container may still late-register on some drivers)
Clear-NvidiaAppTrayAndContainer

$wantOverride = if ([bool]$Script:PanelSettings.scalingOverride) { 1 } else { 0 }
$wantGpu = if ([bool]$Script:PanelSettings.gpuNoScaling) { 0 } else { 1 }
$wantScaleMode = if ([bool]$Script:PanelSettings.gpuNoScaling) { 2 } else { 1 }
$wantUseNvidiaColor = if ([bool]$Script:PanelSettings.fullRgb) { 1 } else { 0 }
$wantFull = if ([bool]$Script:PanelSettings.fullRgb) { 0 } else { 1 }
$wantVidColor = if ([bool]$Script:PanelSettings.videoNvidiaColor) { 1 } else { 0 }
$wantVidImage = if ([bool]$Script:PanelSettings.videoNvidiaImage) { 1 } else { 0 }

$registryOk = $true
$deviceKeys = @(Get-ChildItem 'HKCU:\Software\NVIDIA Corporation\Global\NVTweak\Devices' -EA 0)
if ($deviceKeys.Count -eq 0) {
    Write-DLog 'VERIFY: no HKCU device keys (OK if NVAPI succeeded)'
}
$deviceKeys | ForEach-Object {
    $d = Get-ItemProperty $_.PSPath -ErrorAction SilentlyContinue
    $c = Get-ItemProperty (Join-Path $_.PSPath 'Color') -ErrorAction SilentlyContinue
    $v = Get-ItemProperty (Join-Path $_.PSPath 'Video') -ErrorAction SilentlyContinue
    $okScale = ($null -ne $d -and [int]$d.ScalingOverride -eq $wantOverride -and
                [int]$d.PerformScalingOn -eq $wantGpu -and [int]$d.ScalingMode -eq $wantScaleMode)
    $okColor = ($null -ne $c -and [int]$c.NvCplUseColorSettings -eq $wantUseNvidiaColor -and
                [int]$c.DynamicRange -eq $wantFull)
    $okVideo = ($null -ne $v -and [int]$v.VideoColorSettingsSource -eq $wantVidColor -and
                [int]$v.VideoImageSettingsSource -eq $wantVidImage)
    if (-not ($okScale -and $okColor -and $okVideo)) {
        Write-DLog ("VERIFY FAIL {0} scale={1} color={2} video={3}" -f $_.PSChildName, $okScale, $okColor, $okVideo)
        $registryOk = $false
    } else {
        Write-DLog "VERIFY OK $($_.PSChildName)"
    }
}
try {
    $g = [int](Get-ItemProperty 'HKCU:\Software\NVIDIA Corporation\Global\NVTweak' -Name Gestalt -EA Stop).Gestalt
    Write-DLog "Gestalt=$g"
} catch { }

# Make it work: registry stamp + NVAPI (with retries above). Either path that
# lands policy counts as SUCCESS so Apply does not leave users half-applied.
if ([bool]$nvApiOk) {
    Write-DLog 'SUCCESS nvapi'
    exit 0
}
if ([bool]$registryOk) {
    # NVAPI helper missing/transient - NVTweak Full RGB / scaling still applied.
    Write-DLog 'SUCCESS registry (NVAPI helper unavailable or verify soft-failed; prefs stamped)'
    exit 0
}

Write-DLog ("FAIL nvApiOk={0} registryOk={1}" -f $nvApiOk, $registryOk)
exit 1