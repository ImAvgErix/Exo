# OptiHub — Display performance apply (NO Control Panel mouse automation).
#
# Applies for EVERY connected NVIDIA display (multi-mon):
#   GPU scaling, No scaling mode, Override games ON
#   RGB + Full dynamic range + NVIDIA color policy
#   Video color/image sources = NVIDIA
#
# Method:
#   1) NVAPI (OptiHub.NvDisplay.exe) for live color + path scaling
#   2) NVTweak registry for every device key (what CPL UI reads)
#   3) Soft driver refresh (NVDisplay.Container) so both monitors reload prefs
#   4) Optional hard display-adapter bounce if OPTIHUB_DISPLAY_HARD_RELOAD=1
#
# Never clicks Override (toggle bugs). Never opens GPU combo.
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

function Set-AllNvtweakDevices {
    # PerformScalingOn: 0=GPU 1=Display
    # ScalingOverride: 1=Override games ON
    # ScalingMode / Scaling: 2=No scaling (observed + community)
    $devicesRoot = 'HKCU:\Software\NVIDIA Corporation\Global\NVTweak\Devices'
    if (-not (Test-Path $devicesRoot)) {
        New-Item -Path $devicesRoot -Force | Out-Null
    }

    $names = @(Get-ChildItem -LiteralPath $devicesRoot -ErrorAction SilentlyContinue | ForEach-Object { $_.PSChildName })
    if ($names.Count -eq 0) {
        Write-DLog 'No NVTweak device keys yet — creating placeholders from screen count'
        $n = [Math]::Max(1, [System.Windows.Forms.Screen]::AllScreens.Count)
        for ($i = 0; $i -lt $n; $i++) {
            $key = "OptiHubMon$i-0"
            New-Item -Path (Join-Path $devicesRoot $key) -Force | Out-Null
            $names += $key
        }
    }

    Write-DLog "Writing NVTweak for $($names.Count) device key(s)..."
    foreach ($name in $names) {
        $devPath = Join-Path $devicesRoot $name
        if (-not (Test-Path $devPath)) { New-Item -Path $devPath -Force | Out-Null }

        # Scaling stack — every monitor key, same values (no per-click toggle)
        Set-ItemProperty -LiteralPath $devPath -Name 'PerformScalingOn' -Value 0 -Type DWord -Force
        Set-ItemProperty -LiteralPath $devPath -Name 'ScalingOverride' -Value 1 -Type DWord -Force
        Set-ItemProperty -LiteralPath $devPath -Name 'AppControlledScaling' -Value 0 -Type DWord -Force
        Set-ItemProperty -LiteralPath $devPath -Name 'ScalingMode' -Value 2 -Type DWord -Force
        Set-ItemProperty -LiteralPath $devPath -Name 'Scaling' -Value 2 -Type DWord -Force

        $colorPath = Join-Path $devPath 'Color'
        if (-not (Test-Path $colorPath)) { New-Item -Path $colorPath -Force | Out-Null }
        Set-ItemProperty -LiteralPath $colorPath -Name 'NvCplUseColorSettings' -Value 1 -Type DWord -Force
        Set-ItemProperty -LiteralPath $colorPath -Name 'ColorFormat' -Value 0 -Type DWord -Force
        Set-ItemProperty -LiteralPath $colorPath -Name 'NvCplColorFormat' -Value 0 -Type DWord -Force
        Set-ItemProperty -LiteralPath $colorPath -Name 'NvCplDigitalColorFormat' -Value 0 -Type DWord -Force
        Set-ItemProperty -LiteralPath $colorPath -Name 'DynamicRange' -Value 0 -Type DWord -Force   # Full
        Set-ItemProperty -LiteralPath $colorPath -Name 'NvCplDynamicRange' -Value 0 -Type DWord -Force
        Set-ItemProperty -LiteralPath $colorPath -Name 'ColorDepth' -Value 10 -Type DWord -Force
        Set-ItemProperty -LiteralPath $colorPath -Name 'NvCplOutputColorDepthBpc' -Value 10 -Type DWord -Force
        Set-ItemProperty -LiteralPath $colorPath -Name 'NvCplOutputColorDepth' -Value 3 -Type DWord -Force

        $videoPath = Join-Path $devPath 'Video'
        if (-not (Test-Path $videoPath)) { New-Item -Path $videoPath -Force | Out-Null }
        foreach ($kv in @{
            VideoColorSettingsSource = 1
            VideoImageSettingsSource = 1
            VideoColorSettings       = 1
            VideoImageSettings       = 1
            UseNVIDIAColorSettings   = 1
            UseNVIDIAImageSettings   = 1
            ColorSetting             = 1
            EdgeEnhanceSetting       = 1
            NoiseReductionSetting    = 1
            EdgeEnhanceSource        = 1
            NoiseReductionSource     = 1
            DynamicRange             = 0
            ColorRange               = 0
        }.GetEnumerator()) {
            Set-ItemProperty -LiteralPath $videoPath -Name $kv.Key -Value $kv.Value -Type DWord -Force
        }

        $d = Get-ItemProperty -LiteralPath $devPath
        Write-DLog ("  {0}: PSO={1}(0=GPU) Override={2} ScalingMode={3} Scaling={4}" -f `
            $name, $d.PerformScalingOn, $d.ScalingOverride, $d.ScalingMode, $d.Scaling)
    }

    # Global unlocks
    foreach ($g in @(
        'HKCU:\Software\NVIDIA Corporation\Global\NVTweak',
        'HKLM:\SOFTWARE\NVIDIA Corporation\Global\NVTweak'
    )) {
        if (-not (Test-Path $g)) { New-Item -Path $g -Force | Out-Null }
        Set-ItemProperty -LiteralPath $g -Name 'Gestalt' -Value 1 -Type DWord -Force -ErrorAction SilentlyContinue
    }

    $client = 'HKCU:\Software\NVIDIA Corporation\NVControlPanel2\Client'
    if (-not (Test-Path $client)) { New-Item -Path $client -Force | Out-Null }
    Set-ItemProperty -LiteralPath $client -Name 'OptiHubPreferGpuScaling' -Value 1 -Type DWord -Force
    Set-ItemProperty -LiteralPath $client -Name 'OptiHubPreferNoScaling' -Value 1 -Type DWord -Force
    Set-ItemProperty -LiteralPath $client -Name 'OptiHubPreferScalingOverride' -Value 1 -Type DWord -Force
    Set-ItemProperty -LiteralPath $client -Name 'OptiHubPreferFullRgb' -Value 1 -Type DWord -Force
    Set-ItemProperty -LiteralPath $client -Name 'EulaAccepted' -Value 1 -Type DWord -Force -ErrorAction SilentlyContinue

    Write-DLog 'NVTweak write complete for all device keys'
}

function Invoke-NvApiHelper {
    $exe = Get-NvDisplayExe
    if (-not $exe) {
        Write-DLog 'WARN: OptiHub.NvDisplay.exe missing — NVAPI color/path skipped'
        return $false
    }
    Write-DLog "NVAPI helper: $exe"
    Get-Process nvcplui -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $exe
    $psi.ArgumentList.Add('--apply')
    $psi.WorkingDirectory = (Split-Path -Parent $exe)
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.CreateNoWindow = $true
    $proc = [Diagnostics.Process]::Start($psi)
    $stdout = $proc.StandardOutput.ReadToEnd()
    $stderr = $proc.StandardError.ReadToEnd()
    if (-not $proc.WaitForExit(120000)) {
        try { $proc.Kill() } catch { }
        Write-DLog 'NVAPI helper timed out'
        return $false
    }
    foreach ($line in ($stdout -split "`r?`n")) {
        if ($line) { Write-DLog $line.TrimEnd() }
    }
    if ($stderr) {
        foreach ($line in ($stderr -split "`r?`n")) {
            if ($line) { Write-DLog "NVAPI-ERR $line" }
        }
    }
    Write-DLog "NVAPI helper exit $($proc.ExitCode)"
    return ($proc.ExitCode -eq 0)
}

function Invoke-SoftDriverRefresh {
    # Reload NVIDIA display stack so NVTweak is re-read for ALL monitors.
    Write-DLog 'Soft refresh: restarting NVDisplay.ContainerLocalSystem...'
    Get-Process nvcplui -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    try {
        $svc = Get-Service -Name 'NVDisplay.ContainerLocalSystem' -ErrorAction Stop
        Restart-Service -Name $svc.Name -Force -ErrorAction Stop
        Start-Sleep -Seconds 3
        Write-DLog 'NVDisplay.Container restarted'
        return $true
    } catch {
        Write-DLog "Soft refresh failed: $($_.Exception.Message)"
        return $false
    }
}

function Invoke-HardDisplayReload {
    # Brief black screen — disables/enables the NVIDIA display adapter (like CRU-style reload).
    Write-DLog 'Hard reload: bounce NVIDIA display adapter (brief black screen)...'
    Get-Process nvcplui -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    $dev = Get-PnpDevice -Class Display -ErrorAction SilentlyContinue |
        Where-Object { $_.FriendlyName -match '(?i)nvidia' -and $_.Status -eq 'OK' } |
        Select-Object -First 1
    if (-not $dev) {
        Write-DLog 'No active NVIDIA display PnP device found'
        return $false
    }
    Write-DLog "Device: $($dev.FriendlyName)"
    try {
        Disable-PnpDevice -InstanceId $dev.InstanceId -Confirm:$false -ErrorAction Stop
        Start-Sleep -Seconds 2
        Enable-PnpDevice -InstanceId $dev.InstanceId -Confirm:$false -ErrorAction Stop
        Start-Sleep -Seconds 4
        Write-DLog 'Display adapter re-enabled'
        return $true
    } catch {
        Write-DLog "Hard reload failed: $($_.Exception.Message)"
        # Best effort re-enable
        try { Enable-PnpDevice -InstanceId $dev.InstanceId -Confirm:$false -ErrorAction SilentlyContinue } catch { }
        return $false
    }
}

function Test-NvtweakIntegrity {
    $devicesRoot = 'HKCU:\Software\NVIDIA Corporation\Global\NVTweak\Devices'
    $ok = $true
    $count = 0
    Get-ChildItem -LiteralPath $devicesRoot -ErrorAction SilentlyContinue | ForEach-Object {
        $count++
        $d = Get-ItemProperty -LiteralPath $_.PSPath
        $bad = @()
        if ([int]$d.PerformScalingOn -ne 0) { $bad += "PSO=$($d.PerformScalingOn)" }
        if ([int]$d.ScalingOverride -ne 1) { $bad += "SO=$($d.ScalingOverride)" }
        if ([int]$d.ScalingMode -ne 2) { $bad += "SM=$($d.ScalingMode)" }
        if ($bad.Count -gt 0) {
            Write-DLog "VERIFY FAIL $($_.PSChildName): $($bad -join ', ')"
            $ok = $false
        } else {
            Write-DLog "VERIFY OK $($_.PSChildName): GPU + Override ON + NoScaling"
        }
    }
    if ($count -eq 0) {
        Write-DLog 'VERIFY FAIL: no device keys'
        return $false
    }
    return $ok
}

# ---- main ----
Write-DLog '=== OptiHub display apply (registry + NVAPI, no CPL clicks) ==='

# Need WinForms for screen count fallback
try { Add-Type -AssemblyName System.Windows.Forms } catch { }

# Kill any CPL so it cannot race and rewrite prefs mid-apply
Get-Process nvcplui -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue

# 1) Registry first (all monitors)
Set-AllNvtweakDevices

# 2) NVAPI live color + path
[void](Invoke-NvApiHelper)

# 3) Registry again after NVAPI (NVAPI must not be overwritten by stale CPL; re-assert)
Set-AllNvtweakDevices

# 4) Soft driver refresh
$soft = Invoke-SoftDriverRefresh

# 5) Registry third time after refresh (container restart can race)
Start-Sleep -Milliseconds 800
Set-AllNvtweakDevices

# 6) Hard reload only if requested or soft failed
$hardEnv = $env:OPTIHUB_DISPLAY_HARD_RELOAD
$wantHard = ($hardEnv -eq '1' -or $hardEnv -eq 'true' -or -not $soft)
if ($wantHard) {
    Write-DLog 'Running hard display adapter reload...'
    [void](Invoke-HardDisplayReload)
    Start-Sleep -Seconds 2
    Set-AllNvtweakDevices
    [void](Invoke-NvApiHelper)
    Set-AllNvtweakDevices
}

$ok = Test-NvtweakIntegrity
if ($ok) {
    Write-DLog 'SUCCESS: all NVTweak device keys = GPU + No scaling + Override ON'
    Write-DLog 'Open Control Panel only to VERIFY — do not re-Apply random pages (can rewrite prefs).'
    exit 0
} else {
    Write-DLog 'FAIL: registry verify incomplete'
    exit 1
}
