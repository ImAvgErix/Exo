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

function Set-AllNvtweakDevices {
    $devicesRoot = 'HKCU:\Software\NVIDIA Corporation\Global\NVTweak\Devices'
    if (-not (Test-Path $devicesRoot)) { New-Item -Path $devicesRoot -Force | Out-Null }

    $names = @(Get-ChildItem -LiteralPath $devicesRoot -ErrorAction SilentlyContinue | ForEach-Object { $_.PSChildName })
    if ($names.Count -eq 0) {
        Write-DLog 'NVTweak has no driver-created device keys yet; NVAPI will apply active-display settings directly'
    }

    Write-DLog "NVTweak stamp: $($names.Count) device key(s)"
    foreach ($name in $names) {
        $devPath = Join-Path $devicesRoot $name
        if (-not (Test-Path $devPath)) { New-Item -Path $devPath -Force | Out-Null }

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
        Set-ItemProperty -LiteralPath $colorPath -Name 'DynamicRange' -Value 0 -Type DWord -Force
        Set-ItemProperty -LiteralPath $colorPath -Name 'NvCplDynamicRange' -Value 0 -Type DWord -Force
        # Do not hard-code color depth here. NVAPI chooses a depth supported by the
        # active resolution/refresh pair; a stale 10-bpc cache can lower refresh rate.

        $d = Get-ItemProperty -LiteralPath $devPath
        Write-DLog ("  {0}: GPU={1} Override={2} NoScale={3}" -f $name, ($d.PerformScalingOn -eq 0), ($d.ScalingOverride -eq 1), ($d.ScalingMode -eq 2))
    }

    # Remove markers written by older OptiHub builds. They were never consumed
    # by the NVIDIA driver and should not accumulate in Control Panel storage.
    $legacyClient = 'HKCU:\Software\NVIDIA Corporation\NVControlPanel2\Client'
    if (Test-Path -LiteralPath $legacyClient) {
        foreach ($name in @(
            'OptiHubPreferGpuScaling', 'OptiHubPreferNoScaling',
            'OptiHubPreferScalingOverride', 'OptiHubPreferFullRgb'
        )) {
            Remove-ItemProperty -LiteralPath $legacyClient -Name $name -Force -ErrorAction SilentlyContinue
        }
    }
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
    Get-Process nvcplui -EA 0 | Stop-Process -Force -EA 0
    Unregister-LegacyPersistTask
}

# 1) Registry
Set-AllNvtweakDevices

if (-not $light) {
    # Refresh the user-mode display container once before the final NVAPI apply.
    [void](Invoke-SoftDriverRefresh)
    Set-AllNvtweakDevices
}

# 2) Final NVAPI modes/color/path apply, then registry stamp.
# Sticky without a scheduled task: registry + NVAPI persist until a driver reset;
# re-run NVIDIA Apply in OptiHub if prefs drift.
$nvApiOk = Invoke-NvApiHelper
Set-AllNvtweakDevices

# Verify any driver-created registry keys. NVAPI success remains required.
$registryOk = $true
$deviceKeys = @(Get-ChildItem 'HKCU:\Software\NVIDIA Corporation\Global\NVTweak\Devices' -EA 0)
$deviceKeys | ForEach-Object {
    $d = Get-ItemProperty $_.PSPath
    if ([int]$d.PerformScalingOn -ne 0 -or [int]$d.ScalingOverride -ne 1 -or [int]$d.ScalingMode -ne 2) {
        Write-DLog "VERIFY FAIL $($_.PSChildName)"
        $registryOk = $false
    } else {
        Write-DLog "VERIFY OK $($_.PSChildName): GPU + Override + NoScale"
    }
}
$ok = [bool]$nvApiOk -and [bool]$registryOk

if ($ok) {
    Write-DLog 'SUCCESS'
    Write-DLog 'Active NVIDIA displays were applied without Control Panel mouse/keyboard automation.'
    exit 0
}
Write-DLog 'FAIL verify'
exit 1
