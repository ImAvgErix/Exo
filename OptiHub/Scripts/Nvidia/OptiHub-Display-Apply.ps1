# OptiHub — Display performance apply (sticky).
#
# Order is critical:
#   1) Kill CPL (so it cannot race)
#   2) Write NVTweak for ALL device keys (GPU / NoScaling / Override / Full / Video NVIDIA)
#   3) NVAPI: native res + max Hz, Full RGB, path scaling
#   4) Soft container restart + re-write registry
#   5) Pixel-safe CPL scaling commit (one monitor per CPL session; Override only if OFF)
#   6) Final registry stamp — NO hard adapter bounce after CPL (that wiped live prefs)
#   7) Register logon scheduled task to re-stamp registry (survives reboots)
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
        try { Add-Type -AssemblyName System.Windows.Forms } catch { }
        $n = 1
        try { $n = [Math]::Max(1, [System.Windows.Forms.Screen]::AllScreens.Count) } catch { }
        for ($i = 0; $i -lt $n; $i++) {
            $key = "OptiHubMon$i-0"
            New-Item -Path (Join-Path $devicesRoot $key) -Force | Out-Null
            $names += $key
        }
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
        Set-ItemProperty -LiteralPath $colorPath -Name 'ColorDepth' -Value 10 -Type DWord -Force
        Set-ItemProperty -LiteralPath $colorPath -Name 'NvCplOutputColorDepthBpc' -Value 10 -Type DWord -Force
        Set-ItemProperty -LiteralPath $colorPath -Name 'NvCplOutputColorDepth' -Value 3 -Type DWord -Force

        $videoPath = Join-Path $devPath 'Video'
        if (-not (Test-Path $videoPath)) { New-Item -Path $videoPath -Force | Out-Null }
        foreach ($kv in @{
            VideoColorSettingsSource = 1; VideoImageSettingsSource = 1
            VideoColorSettings = 1; VideoImageSettings = 1
            UseNVIDIAColorSettings = 1; UseNVIDIAImageSettings = 1
            ColorSetting = 1; EdgeEnhanceSetting = 1; NoiseReductionSetting = 1
            EdgeEnhanceSource = 1; NoiseReductionSource = 1
            DynamicRange = 0; ColorRange = 0
        }.GetEnumerator()) {
            Set-ItemProperty -LiteralPath $videoPath -Name $kv.Key -Value $kv.Value -Type DWord -Force
        }

        $d = Get-ItemProperty -LiteralPath $devPath
        Write-DLog ("  {0}: GPU={1} Override={2} NoScale={3}" -f $name, ($d.PerformScalingOn -eq 0), ($d.ScalingOverride -eq 1), ($d.ScalingMode -eq 2))
    }

    foreach ($g in @(
        'HKCU:\Software\NVIDIA Corporation\Global\NVTweak',
        'HKLM:\SOFTWARE\NVIDIA Corporation\Global\NVTweak'
    )) {
        if (-not (Test-Path $g)) { New-Item -Path $g -Force | Out-Null }
        Set-ItemProperty -LiteralPath $g -Name 'Gestalt' -Value 1 -Type DWord -Force -ErrorAction SilentlyContinue
    }
    $client = 'HKCU:\Software\NVIDIA Corporation\NVControlPanel2\Client'
    if (-not (Test-Path $client)) { New-Item -Path $client -Force | Out-Null }
    foreach ($kv in @{
        OptiHubPreferGpuScaling = 1; OptiHubPreferNoScaling = 1
        OptiHubPreferScalingOverride = 1; OptiHubPreferFullRgb = 1
        EulaAccepted = 1; ShowSedoanEula = 0
    }.GetEnumerator()) {
        Set-ItemProperty -LiteralPath $client -Name $kv.Key -Value $kv.Value -Type DWord -Force -ErrorAction SilentlyContinue
    }
}

function Invoke-NvApiHelper {
    $exe = Get-NvDisplayExe
    if (-not $exe) {
        Write-DLog 'WARN: OptiHub.NvDisplay.exe missing'
        return $false
    }
    Write-DLog "NVAPI: $exe"
    Get-Process nvcplui -EA 0 | Stop-Process -Force -EA 0
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
    if (-not $proc.WaitForExit(180000)) {
        try { $proc.Kill() } catch { }
        Write-DLog 'NVAPI timeout'
        return $false
    }
    foreach ($line in ($stdout -split "`r?`n")) { if ($line) { Write-DLog $line.TrimEnd() } }
    foreach ($line in ($stderr -split "`r?`n")) { if ($line) { Write-DLog "ERR $line" } }
    Write-DLog "NVAPI exit $($proc.ExitCode)"
    return ($proc.ExitCode -eq 0)
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

function Register-PersistTask {
    # Re-stamp registry at logon so prefs survive reboots / driver resets
    $taskName = 'OptiHub-NvidiaDisplayPersist'
    $script = Join-Path $Root 'OptiHub-Display-Apply.ps1'
    $ps = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    # Persist task only re-stamps registry + NVAPI (no hard reload, no CPL) via env flag
    $arg = "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$script`""
    try {
        $exists = schtasks /Query /TN $taskName 2>$null
        if ($LASTEXITCODE -eq 0) {
            schtasks /Delete /TN $taskName /F 2>$null | Out-Null
        }
        # Run as current user at logon
        $user = "$env:USERDOMAIN\$env:USERNAME"
        schtasks /Create /TN $taskName /TR "`"$ps`" $arg" /SC ONLOGON /RL HIGHEST /F /RU $user 2>&1 | Out-Null
        if ($LASTEXITCODE -eq 0) {
            Write-DLog "Persist task registered: $taskName (re-apply at logon)"
        } else {
            Write-DLog "Persist task register skipped/failed (exit $LASTEXITCODE)"
        }
    } catch {
        Write-DLog "Persist task error: $($_.Exception.Message)"
    }
}

# ---- main ----
Write-DLog '=== Display apply (sticky path) ==='
Get-Process nvcplui -EA 0 | Stop-Process -Force -EA 0

# Persist flag: logon task only does light path
$light = ($env:OPTIHUB_DISPLAY_LIGHT -eq '1')

# 1) Registry
Set-AllNvtweakDevices

# 2) NVAPI modes + color + path
[void](Invoke-NvApiHelper)
Set-AllNvtweakDevices

if (-not $light) {
    # 3) Soft refresh then re-stamp (do NOT hard-bounce after CPL)
    [void](Invoke-SoftDriverRefresh)
    Set-AllNvtweakDevices
    [void](Invoke-NvApiHelper)
    Set-AllNvtweakDevices

    # 4) Pixel-safe CPL commit for Override/No scaling per monitor
    $scaleScript = Join-Path $Root 'OptiHub-Cpl-ScalingCommit.ps1'
    if (Test-Path -LiteralPath $scaleScript) {
        Write-DLog 'CPL scaling commit (pixel-safe Override, one mon per session)...'
        $prev = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        try {
            & $scaleScript 2>&1 | ForEach-Object {
                if ($_) { Write-DLog "$_" }
            }
            Write-DLog "Scaling commit exit $LASTEXITCODE"
        } catch {
            Write-DLog "Scaling commit error: $($_.Exception.Message)"
        } finally {
            $ErrorActionPreference = $prev
            Get-Process nvcplui -EA 0 | Stop-Process -Force -EA 0
        }
    } else {
        Write-DLog 'WARN: OptiHub-Cpl-ScalingCommit.ps1 missing'
    }

    # 5) Final stamp (never hard-reload after CPL — that undoes live Override)
    Set-AllNvtweakDevices
    Register-PersistTask
}

# Verify
$ok = $true
Get-ChildItem 'HKCU:\Software\NVIDIA Corporation\Global\NVTweak\Devices' -EA 0 | ForEach-Object {
    $d = Get-ItemProperty $_.PSPath
    if ([int]$d.PerformScalingOn -ne 0 -or [int]$d.ScalingOverride -ne 1 -or [int]$d.ScalingMode -ne 2) {
        Write-DLog "VERIFY FAIL $($_.PSChildName)"
        $ok = $false
    } else {
        Write-DLog "VERIFY OK $($_.PSChildName): GPU + Override + NoScale"
    }
}

if ($ok) {
    Write-DLog 'SUCCESS'
    Write-DLog 'Verify in CPL: both monitors = No scaling + Override ON. GPU is forced via registry.'
    exit 0
}
Write-DLog 'FAIL verify'
exit 1
