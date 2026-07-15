# Exo - detect NVIDIA optimizer status (JSON for WinUI).
# Feature order matches apply pipeline: GPU -> driver -> 3D profile -> display/privacy.
# Classifiers: NvidiaDetectCore.ps1 (pure) - keep aligned with NvidiaPeakLogic.cs
$ErrorActionPreference = 'SilentlyContinue'

$core = Join-Path $PSScriptRoot 'NvidiaDetectCore.ps1'
if (Test-Path -LiteralPath $core) { . $core }

function Get-NvidiaGpus {
    @(Get-CimInstance Win32_VideoController -ErrorAction SilentlyContinue | Where-Object {
        $_.Name -match '(?i)nvidia|geforce|rtx|gtx|quadro|titan'
    } | ForEach-Object {
        [pscustomobject]@{
            Name   = [string]$_.Name
            Driver = [string]$_.DriverVersion
            PnpId  = [string]$_.PNPDeviceID
        }
    })
}

function Get-GpuSeriesFromName([string]$Name) {
    if (Get-Command Get-ExoGpuSeriesFromName -ErrorAction SilentlyContinue) {
        return Get-ExoGpuSeriesFromName -Name $Name
    }
    if ($Name -match '(?i)\b(?:RTX|GTX)\s*([1-5])0\d{2}\b') { return $Matches[1] + '0' }
    if ($Name -match '(?i)\b([1-5])0\d{2}\b') { return $Matches[1] + '0' }
    # GTX 16 has no RT/DLSS/rBAR; use the non-RTX performance pack.
    if ($Name -match '(?i)\b16\d{2}\b') { return '10' }
    return $null
}

function Get-DriverBranchSeriesFromName([string]$Name) {
    # GTX 16xx still on modern GRD; GTX 10xx (1080 etc.) is legacy security branch.
    if ($Name -match '(?i)\b16\d{2}\b') { return '20' }
    if ($Name -match '(?i)\b(?:RTX|GTX)\s*([1-5])0\d{2}\b') { return $Matches[1] + '0' }
    if ($Name -match '(?i)\b([1-5])0\d{2}\b') { return $Matches[1] + '0' }
    return $null
}

function Get-LatestDriverForSeries([string]$SeriesId) {
    $base = 'https://gfwsl.geforce.com/services_toolkit/services/com/nvidia/services/AjaxDriverService.php?func=DriverManualLookup'
    $q = '&osID=57&languageCode=1033&beta=0&isWHQL=1&dltype=-1&dch=1&upCRD=0&qnf=0&ctk=null&windowsVersion=10.0&windowsArchitecture=64bit'
    $pairs = switch ($SeriesId) {
        '10' { @(@{ psid = 101; pfid = 815 }, @{ psid = 101; pfid = 817 }) }
        '20' { @(@{ psid = 107; pfid = 879 }, @{ psid = 107; pfid = 887 }) }
        '30' { @(@{ psid = 120; pfid = 933 }, @{ psid = 120; pfid = 929 }) }
        '40' { @(@{ psid = 127; pfid = 995 }, @{ psid = 127; pfid = 1015 }) }
        '50' { @(@{ psid = 131; pfid = 1066 }, @{ psid = 131; pfid = 1070 }) }
        default { @(@{ psid = 120; pfid = 933 }, @{ psid = 127; pfid = 995 }) }
    }
    foreach ($p in $pairs) {
        try {
            $url = "$base&psid=$($p.psid)&pfid=$($p.pfid)$q"
            $r = Invoke-RestMethod -Uri $url -Headers @{ 'User-Agent' = 'Exo-Nvidia/1.2' } -TimeoutSec 12
            if ($r.Success -eq '1') {
                $ver = [string]$r.IDS[0].downloadInfo.Version
                if ($ver -match '^\d{3}\.\d{2}$') { return $ver }
            }
        } catch { }
    }
    return $null
}

function Test-IsNotebookGpuName([string]$Name) {
    if ([string]::IsNullOrWhiteSpace($Name)) { return $false }
    return [bool]($Name -match '(?i)\b(?:Laptop GPU|Notebook|Mobile|Max-Q)\b|\bMX\d+\b|\b\d{3,4}M\b')
}

function Convert-WindowsDriverToNvidia([string]$WinVer) {
    try {
        $parts = $WinVer -split '\.'
        if ($parts.Count -lt 4) { return $null }
        $c = [int]$parts[2]; $d = [int]$parts[3]
        $combined = ($c * 10000 + $d).ToString()
        if ($combined.Length -lt 5) { $combined = $combined.PadLeft(5, '0') }
        $last5 = $combined.Substring($combined.Length - 5)
        return ('{0}.{1:D2}' -f [int]$last5.Substring(0, 3), [int]$last5.Substring(3, 2))
    } catch { return $null }
}

function Test-ExoDriverInstallTweaks([string]$CurrentNv, $State) {
    # Same signals as Nvidia-Optimizer.ps1: stock Game Ready vs NVCleanstall-style install.
    $issues = New-Object System.Collections.Generic.List[string]

    foreach ($serviceName in @('NvTelemetryContainer', 'NvCamera', 'FvSvc')) {
        $svc = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
        if ($svc -and $svc.StartType -ne 'Disabled') {
            [void]$issues.Add("$serviceName still enabled")
        }
    }
    $networkService = Get-Service -Name 'NvContainerNetworkService' -ErrorAction SilentlyContinue
    if ($networkService -and ($networkService.StartType -eq 'Automatic' -or $networkService.Status -eq 'Running')) {
        [void]$issues.Add('NvContainerNetworkService still starts automatically or is running')
    }

    $msiSeen = 0
    $msiGaps = 0
    try {
        $pci = 'HKLM:\SYSTEM\CurrentControlSet\Enum\PCI'
        if (Test-Path $pci) {
            Get-ChildItem $pci -ErrorAction SilentlyContinue | Where-Object {
                $_.PSChildName -match 'VEN_10DE'
            } | ForEach-Object {
                Get-ChildItem $_.PSPath -ErrorAction SilentlyContinue | ForEach-Object {
                    $device = Get-ItemProperty -LiteralPath $_.PSPath -ErrorAction SilentlyContinue
                    if ($device.Class -ne 'Display' -and
                        $device.ClassGUID -ne '{4d36e968-e325-11ce-bfc1-08002be10318}') {
                        return
                    }
                    $msiSeen++
                    $msiKey = Join-Path $_.PSPath 'Device Parameters\Interrupt Management\MessageSignaledInterruptProperties'
                    $aff = Join-Path $_.PSPath 'Device Parameters\Interrupt Management\Affinity Policy'
                    $v = (Get-ItemProperty -LiteralPath $msiKey -ErrorAction SilentlyContinue).MSISupported
                    $priority = (Get-ItemProperty -LiteralPath $aff -ErrorAction SilentlyContinue).DevicePriority
                    if ($v -ne 1 -or $priority -ne 3) { $msiGaps++ }
                }
            }
        }
    } catch { }
    # Only fail MSI when we can see display PCI nodes and they lack High priority.
    # msiSeen=0 (enum/permissions) is best-effort skip - not a mid-tier false fail.
    if ($msiSeen -gt 0 -and $msiGaps -gt 0) {
        [void]$issues.Add("MSI High missing on $msiGaps of $msiSeen NVIDIA display device(s)")
    }

    $remembered = $false
    if ($State -and $State.driverTweaksVerified -and $State.driverTweaksVersion -and $CurrentNv -and
        [string]$State.driverTweaksVersion -eq [string]$CurrentNv) {
        $remembered = $true
    }

    return [pscustomobject]@{
        Ok         = [bool]($issues.Count -eq 0)
        Remembered = $remembered
        Issues     = @($issues)
        MsiSeen    = $msiSeen
    }
}

$features = New-Object System.Collections.Generic.List[hashtable]
$gpus = @(Get-NvidiaGpus)
$gpuOk = $gpus.Count -gt 0
$primary = if ($gpuOk) { $gpus[0] } else { $null }
$series = if ($primary) { Get-GpuSeriesFromName $primary.Name } else { $null }
$isNotebookGpu = [bool]($primary -and (Test-IsNotebookGpuName $primary.Name))
$profilesDir = Join-Path $PSScriptRoot 'profiles'
$profilePackVersion = ''
$profileVersionPath = Join-Path $profilesDir 'PROFILE_VERSION'
if (Test-Path -LiteralPath $profileVersionPath) {
    $profilePackVersion = (Get-Content -LiteralPath $profileVersionPath -Raw -ErrorAction SilentlyContinue).Trim()
}

function Test-NvidiaPerformanceDebloat {
    $issues = New-Object System.Collections.Generic.List[string]
    foreach ($serviceName in @('NvTelemetryContainer', 'NvCamera', 'FvSvc')) {
        $service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
        if ($service -and ($service.StartType -ne 'Disabled' -or $service.Status -eq 'Running')) {
            [void]$issues.Add("Service active: $serviceName")
        }
    }
    $networkService = Get-Service -Name 'NvContainerNetworkService' -ErrorAction SilentlyContinue
    if ($networkService -and ($networkService.StartType -eq 'Automatic' -or $networkService.Status -eq 'Running')) {
        [void]$issues.Add('NVIDIA network container starts automatically or is running')
    }

    # Fresh App is intentional and may be opened on demand. Only flag overlay/helpers/GFE noise.
    # Exact names, so filter in the service instead of enumerating every process.
    $background = @(Get-Process -Name @(
        'NVIDIA Overlay', 'NVIDIA Share', 'NVIDIA Web Helper',
        'GFExperience', 'nvsphelper', 'nvsphelper64') -ErrorAction SilentlyContinue)
    if ($background.Count -gt 0) {
        [void]$issues.Add("Background clients running: $($background.ProcessName -join ', ')")
    }

    $patterns = @('*NvTm*', '*NVIDIA*Telemetry*', '*NvProfile*', '*NvNode*', '*NvBackend*', '*NVIDIA*App*', '*NVIDIA*SelfUpdate*', 'NVIDIA App SelfUpdate*', '*FrameView*', 'NvDriverUpdateCheckDaily*', 'NVIDIA GeForce Experience SelfUpdate*')
    Get-ScheduledTask -ErrorAction SilentlyContinue | Where-Object {
        [bool]$_.Settings.Enabled -or $_.State -ne 'Disabled'
    } | ForEach-Object {
        $full = "$($_.TaskPath)$($_.TaskName)"
        if ($_.TaskName -match '(?i)Display|LocalSystem|^Exo') { return }
        foreach ($pattern in $patterns) {
            if ($_.TaskName -like $pattern -or $full -like $pattern) {
                [void]$issues.Add("Task enabled: $full")
                break
            }
        }
    }

    $runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
    if (Test-Path -LiteralPath $runKey) {
        $runValues = Get-ItemProperty -LiteralPath $runKey -ErrorAction SilentlyContinue
        foreach ($property in $runValues.PSObject.Properties) {
            if ($property.Name -like 'PS*') { continue }
            if ("$($property.Name) $($property.Value)" -match '(?i)NVIDIA App|GeForce Experience|GFExperience|NvBackend|ShadowPlay|FrameView') {
                [void]$issues.Add("Auto-start enabled: $($property.Name)")
            }
        }
    }

    return [pscustomobject]@{ Ok = [bool]($issues.Count -eq 0); Issues = @($issues) }
}

function Test-NvidiaOverlayDisabled {
    $issues = New-Object System.Collections.Generic.List[string]
    $processes = @(Get-Process -Name @(
        'NVIDIA Overlay', 'NVIDIA Share', 'nvsphelper', 'nvsphelper64') -ErrorAction SilentlyContinue)
    if ($processes.Count -gt 0) {
        [void]$issues.Add("Overlay processes running: $($processes.ProcessName -join ', ')")
    }

    foreach ($path in @(
        'HKCU:\Software\NVIDIA Corporation\NVIDIA App',
        'HKCU:\Software\NVIDIA Corporation\Global\GFExperience'
    )) {
        $properties = Get-ItemProperty -LiteralPath $path -ErrorAction SilentlyContinue
        foreach ($name in @('OverlayEnabled', 'EnableOverlay')) {
            $property = if ($properties) { $properties.PSObject.Properties[$name] } else { $null }
            if (-not $property -or [int]$property.Value -ne 0) {
                [void]$issues.Add("Overlay preference active or missing: $path\\$name")
            }
        }
    }

    $capsPath = 'HKCU:\Software\NVIDIA Corporation\Global\ShadowPlay\NVSPCAPS'
    $caps = Get-ItemProperty -LiteralPath $capsPath -ErrorAction SilentlyContinue
    foreach ($name in @('RecEnabled', 'DwmEnabled', 'DwmDvrEnabledV1', 'DisplayRecordingIndicator', 'DisplayGamecastIndicator', 'GameStreamPortal')) {
        $property = if ($caps) { $caps.PSObject.Properties[$name] } else { $null }
        $bytes = if ($property) { @($property.Value) } else { @() }
        if ($bytes.Count -eq 0 -or @($bytes | Where-Object { [int]$_ -ne 0 }).Count -gt 0) {
            [void]$issues.Add("ShadowPlay preference active or missing: $name")
        }
    }

    return [pscustomobject]@{ Ok = [bool]($issues.Count -eq 0); Issues = @($issues) }
}

function Test-NvidiaDisplayLive {
    $exe = $null
    foreach ($candidate in @(
        (Join-Path $PSScriptRoot 'tools\Exo.NvDisplay.exe'),
        (Join-Path $env:LOCALAPPDATA 'Exo\scripts\Nvidia\tools\Exo.NvDisplay.exe'),
        (Join-Path $env:LOCALAPPDATA 'Exo\app\Scripts\Nvidia\tools\Exo.NvDisplay.exe')
    )) {
        if ($candidate -and (Test-Path -LiteralPath $candidate)) { $exe = $candidate; break }
    }
    if (-not $exe) { return [pscustomobject]@{ Available = $false; Ok = $false; Detail = 'helper unavailable' } }

    $process = $null
    try {
        $psi = New-Object System.Diagnostics.ProcessStartInfo
        $psi.FileName = $exe
        $psi.Arguments = '--status'
        $psi.WorkingDirectory = Split-Path -Parent $exe
        $psi.UseShellExecute = $false
        $psi.RedirectStandardOutput = $true
        $psi.RedirectStandardError = $true
        $psi.CreateNoWindow = $true
        $process = [Diagnostics.Process]::Start($psi)
        if (-not $process) { throw 'display helper did not start' }
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(15000)) {
            try { $process.Kill() } catch { }
            throw 'display status timed out'
        }
        $stdout = $stdoutTask.GetAwaiter().GetResult()
        $stderr = $stderrTask.GetAwaiter().GetResult()
        $jsonLine = @($stdout -split "`r?`n") | Where-Object { $_ -like 'EXO_NVDISPLAY_JSON:*' } | Select-Object -Last 1
        if (-not $jsonLine) { throw "display helper returned no status JSON: $stderr" }
        $status = $jsonLine.Substring('EXO_NVDISPLAY_JSON:'.Length) | ConvertFrom-Json
        # Peak gate (matches Exo.NvDisplay): refresh + (registry active OR live color+scale)
        # Note: NvidiaDetectCore uses StrictMode - never touch optional props without existence checks.
        $ok = $false
        if ($null -ne $status.PSObject.Properties['ok']) { $ok = [bool]$status.ok }
        $checks = $null
        if ($null -ne $status.PSObject.Properties['checks']) { $checks = $status.checks }
        if ($checks -and (Get-Command Test-ExoDisplayStatusPeakOk -ErrorAction SilentlyContinue)) {
            $refreshOk = $false; $registryOk = $false; $colorOk = $false; $pathOk = $false
            if ($null -ne $checks.PSObject.Properties['refreshOk']) { $refreshOk = [bool]$checks.refreshOk }
            if ($null -ne $checks.PSObject.Properties['modesOk'] -and [bool]$checks.modesOk) { $refreshOk = $true }
            if ($null -ne $checks.PSObject.Properties['registryOk']) { $registryOk = [bool]$checks.registryOk }
            if ($null -ne $checks.PSObject.Properties['colorOk']) { $colorOk = [bool]$checks.colorOk }
            if ($null -ne $checks.PSObject.Properties['pathScalingOk']) { $pathOk = [bool]$checks.pathScalingOk }
            if ($null -ne $checks.PSObject.Properties['scalingOk'] -and [bool]$checks.scalingOk) { $pathOk = $true }
            $ok = Test-ExoDisplayStatusPeakOk -RefreshOk $refreshOk -RegistryOk $registryOk -ColorOk $colorOk -PathScalingOk $pathOk
        }
        $skipped = $null
        if ($null -ne $status.PSObject.Properties['skipped']) { $skipped = [string]$status.skipped }
        $detail = if ($skipped) { $skipped } elseif ($checks) {
            "color=$colorOk, refresh=$refreshOk, scaling=$pathOk, registry=$registryOk, peakOk=$ok"
        } else { "exit=$($process.ExitCode)" }
        return [pscustomobject]@{ Available = $true; Ok = $ok; Detail = $detail }
    } catch {
        return [pscustomobject]@{ Available = $true; Ok = $false; Detail = $_.Exception.Message }
    } finally {
        if ($process) { try { $process.Dispose() } catch { } }
    }
}

$statePath = Join-Path $env:LOCALAPPDATA 'Exo\nvidia-optimizer.json'
$state = $null
if (Test-Path $statePath) {
    try { $state = Get-Content $statePath -Raw -Encoding UTF8 | ConvertFrom-Json } catch { }
}

# --- Live DRS verification (managed NPI v3.0.1.11+ -exportCustomized) ---
# Reads the live driver database back and compares the Base Profile pins against
# the recorded pack. Classification is pure (NvidiaDetectCore.ps1); this block
# only does the I/O. Non-elevated: the export lands next to the managed exe under
# %LocalAppData% and is deleted after parsing.

function Get-ExoNipBaseProfileMap([string]$NipPath) {
    if (-not $NipPath -or -not (Test-Path -LiteralPath $NipPath)) { return $null }
    try { [xml]$doc = [IO.File]::ReadAllText($NipPath) } catch { return $null }
    $base = @($doc.ArrayOfProfile.Profile) |
        Where-Object { [string]$_.ProfileName -eq 'Base Profile' } |
        Select-Object -First 1
    if (-not $base) { return $null }
    $map = @{}
    foreach ($s in @($base.SelectNodes('Settings/ProfileSetting'))) {
        $id = [string]$s.SettingID
        if ($id) { $map[$id] = [string]$s.SettingValue }
    }
    return $map
}

function Invoke-ExoNpiExportCustomized([string]$NpiPath, [int]$TimeoutSec = 30) {
    if (-not $NpiPath -or -not (Test-Path -LiteralPath $NpiPath)) { return $null }
    $npiWorkDir = Split-Path -Parent $NpiPath
    $startUtc = (Get-Date).ToUniversalTime()
    $proc = $null
    try {
        $psi = New-Object System.Diagnostics.ProcessStartInfo
        $psi.FileName = $NpiPath
        $psi.Arguments = '-exportCustomized'
        $psi.WorkingDirectory = $npiWorkDir
        $psi.UseShellExecute = $false
        $psi.CreateNoWindow = $true
        $proc = [Diagnostics.Process]::Start($psi)
        if (-not $proc) { return $null }
        if (-not $proc.WaitForExit($TimeoutSec * 1000)) {
            try { $proc.Kill() } catch { }
            return $null
        }
    } catch {
        return $null
    } finally {
        if ($proc) { try { $proc.Dispose() } catch { } }
    }
    $exported = @(Get-ChildItem -LiteralPath $npiWorkDir -Filter '*.nip' -File -ErrorAction SilentlyContinue |
        Where-Object { $_.LastWriteTimeUtc -ge $startUtc.AddSeconds(-5) } |
        Sort-Object LastWriteTimeUtc -Descending) | Select-Object -First 1
    if (-not $exported) { return $null }
    return [string]$exported.FullName
}

function Get-ExoDrsExportBaseMap([string]$ExportPath) {
    # $null = unparseable export; empty map = parsed but no customized Base Profile (drift).
    try { [xml]$doc = [IO.File]::ReadAllText($ExportPath) } catch { return $null }
    $base = @($doc.ArrayOfProfile.Profile) |
        Where-Object { [string]$_.ProfileName -eq 'Base Profile' } |
        Select-Object -First 1
    if (-not $base) { return @{} }
    $map = @{}
    foreach ($s in @($base.SelectNodes('Settings/ProfileSetting'))) {
        $id = [string]$s.SettingID
        if ($id) { $map[$id] = [string]$s.SettingValue }
    }
    return $map
}

$drsVerifiedText = if (Get-Command Get-ExoDrsVerifiedDetailText -ErrorAction SilentlyContinue) {
    Get-ExoDrsVerifiedDetailText
} else { 'Verified in driver' }
$drsDriftedText = if (Get-Command Get-ExoDrsDriftedDetailText -ErrorAction SilentlyContinue) {
    Get-ExoDrsDriftedDetailText
} else { ('Drifted ' + [char]0x2014 + ' re-apply') }

$drsLive = 'unavailable'
$drsLiveText = ''
$drsMismatch = @()
$drsComparedCount = 0
$npiManagedExe = Join-Path $env:LOCALAPPDATA 'Exo\tools\nvidiaProfileInspector\nvidiaProfileInspector.exe'
if ($state -and [bool]$state.profileApplied -and $state.profileFile -and
    (Test-Path -LiteralPath $npiManagedExe) -and
    (Get-Command Get-ExoDrsVerificationResult -ErrorAction SilentlyContinue)) {
    $recordedPackPath = Join-Path $profilesDir ([string]$state.profileFile)
    $drsExpectedMap = Get-ExoNipBaseProfileMap $recordedPackPath
    $drsExportPath = Invoke-ExoNpiExportCustomized $npiManagedExe
    $drsExportedMap = $null
    if ($drsExportPath) {
        try { $drsExportedMap = Get-ExoDrsExportBaseMap $drsExportPath }
        finally { Remove-Item -LiteralPath $drsExportPath -Force -ErrorAction SilentlyContinue }
    }
    $drsRequiredPins = @('274197361', '390467', '277041152', '277041154', '294973784')
    $drsResult = Get-ExoDrsVerificationResult -Expected $drsExpectedMap -Exported $drsExportedMap -RequiredIds $drsRequiredPins
    $drsLive = [string]$drsResult.Status
    $drsMismatch = @($drsResult.Mismatches)
    $drsComparedCount = [int]$drsResult.ComparedCount
}
$drsLiveText = switch ($drsLive) {
    'verified' { $drsVerifiedText }
    'drifted'  { $drsDriftedText }
    default    { '' }
}

# 1) GPU - name only (series is for profile pick; no "30 Series" suffix, no fancy dots that turn into ?)
$gpuDetail = if (-not $gpuOk) {
    'NVIDIA GPU + drivers required.'
} else {
    [string]$primary.Name
}
$features.Add(@{
    title  = 'NVIDIA GPU'
    detail = $gpuDetail
    active = $gpuOk
})

# 2) Driver (first pipeline step)
$winDrv = ''
if ($primary) {
    $winDrv = [string]$primary.Driver
}
$currentNv = Convert-WindowsDriverToNvidia $winDrv
$latestNv = $null
$needsUpdate = $false
$driverBranch = if ($primary) { Get-DriverBranchSeriesFromName $primary.Name } else { $null }
if (-not $driverBranch) { $driverBranch = $series }
if (-not $isNotebookGpu -and $driverBranch) {
    $latestNv = Get-LatestDriverForSeries $driverBranch
}

$needsUpdate = -not [bool]$currentNv
if ($latestNv -and $currentNv) {
    try {
        if ([version]$currentNv -lt [version]$latestNv) { $needsUpdate = $true }
    } catch {
        if ($currentNv -ne $latestNv) { $needsUpdate = $true }
    }
}

# Newest version alone is not enough - stock installs need NVCleanstall reinstall with tweaks.
$tweaks = Test-ExoDriverInstallTweaks $currentNv $state
$debloat = Test-NvidiaPerformanceDebloat
$overlay = Test-NvidiaOverlayDisabled
$needsRetweak = (-not $needsUpdate) -and [bool]$currentNv -and (-not $tweaks.Ok)
# Notebook: never auto-download desktop GRD - but do NOT treat that as a permanent fail.
# Profiles, display policy, and debloat still apply on laptops.
$needsDriverAction = if ($isNotebookGpu) {
    -not [bool]$currentNv   # only fail driver stage if we cannot see any NVIDIA driver
} else {
    $needsUpdate -or $needsRetweak
}

$driverNote = if ($isNotebookGpu) {
    if ($currentNv) {
        "Laptop GPU with driver $currentNv. Desktop auto-update is skipped; use NVIDIA's notebook driver if you need a newer build. Profiles and display still apply via Exo."
    } else {
        'Laptop GPU detected but no driver version was read. Install the official NVIDIA notebook driver, then Apply.'
    }
} elseif (-not $currentNv) {
    'NVIDIA driver version could not be read. Install or repair the display driver, then refresh.'
} elseif ($needsUpdate) {
    $curLabel = if ($currentNv) { $currentNv } else { 'unknown' }
    $branchHint = if ($driverBranch -eq '10') { ' (10-series security branch)' } else { '' }
    "Update available for this GPU series${branchHint}: $curLabel -> $latestNv. Apply runs Exo Clean Driver."
} elseif ($needsRetweak) {
    $gap = if ($tweaks.Issues.Count -gt 0) { ($tweaks.Issues -join '; ') } else { 'stock-style install signals' }
    "On newest Game Ready ($currentNv) but without Exo tweaks ($gap). Apply fixes MSI/privacy in-place."
} elseif ($latestNv -and $currentNv) {
    "On newest Game Ready ($currentNv) with Exo clean-driver tweaks."
} elseif ($currentNv) {
    "Installed Game Ready $currentNv with Exo tweaks. NVIDIA's update service is currently unavailable."
}
$features.Add(@{
    title  = 'Driver (newest + install tweaks)'
    detail = $driverNote
    active = (-not $needsDriverAction) -and [bool]$currentNv
})

# 3) 3D profile - fail closed on interrupted runs, driver changes, legacy
# markers, missing pack metadata, or an asset hash mismatch.
$pendingAfterDriver = [bool]($state -and $state.pendingAfterDriver)
$applyInProgress = [bool]($state -and $state.applyInProgress)
$profileOk = $false
if ($state -and -not $pendingAfterDriver -and -not $applyInProgress) {
    $requiredProfileFields = @('profileApplied', 'profileFile', 'profileVersion', 'profileSha256', 'profileDriverVersion')
    $hasProfileContract = @($requiredProfileFields | Where-Object {
        $state.PSObject.Properties.Name -notcontains $_
    }).Count -eq 0
    if ($hasProfileContract) {
        $profileHash = [string]$state.profileSha256
        $profileOk = [bool]$state.profileApplied -and
                     [bool]$state.profileFile -and
                     [bool]$state.profileVersion -and
                     $profileHash -match '^[a-fA-F0-9]{64}$' -and
                     [bool]$state.profileDriverVersion -and
                     [bool]$series -and
                     [string]$state.series -eq [string]$series -and
                     [bool]$profilePackVersion -and
                     [string]$state.profileVersion -eq $profilePackVersion -and
                     [bool]$currentNv -and
                     [string]$state.profileDriverVersion -eq [string]$currentNv

        $expectedProfile = if ([bool]$state.gsync) { "$series Series G-SYNC.nip" } else { "$series Series.nip" }
        if ($profileOk -and [string]$state.profileFile -ne $expectedProfile) { $profileOk = $false }
        $expectedPath = Join-Path $profilesDir $expectedProfile
        if ($profileOk -and (Test-Path -LiteralPath $expectedPath)) {
            $currentHash = (Get-FileHash -LiteralPath $expectedPath -Algorithm SHA256 -ErrorAction SilentlyContinue).Hash
            if (-not $currentHash -or $currentHash -ine $profileHash) { $profileOk = $false }
        } elseif ($profileOk) {
            $profileOk = $false
        }
    }
}
# Profile stage applied = durable state record AND live DRS not drifted.
$applied = $profileOk -and ($drsLive -ne 'drifted')
$gsyncDetail = if ($state -and $state.gsync) { 'G-SYNC pack' } else { 'Max FPS / latency pack' }
$features.Add(@{
    title  = '3D Base Profile'
    detail = $(if ($profileOk -and $drsLive -eq 'drifted') {
        $drsDriftedText
    } elseif ($applied -and $drsLive -eq 'verified') {
        $pf = if ($state.profileFile) { [string]$state.profileFile } else { 'profile applied' }
        "$gsyncDetail - $pf ($drsVerifiedText)"
    } elseif ($applied) {
        $pf = if ($state.profileFile) { [string]$state.profileFile } else { 'profile applied' }
        "$gsyncDetail - $pf (silent import verified; live DRS check unavailable)"
    } else {
        'Not applied yet. Apply runs Profile Inspector -silentImport (no GUI / replace click).'
    })
    active = $applied
    drsLive = $drsLive
    drsLiveText = $drsLiveText
})

$gameOk = $false
$gameDetail = 'Per-game profiles not recorded. Apply to import Base + Val/CS2/R6/Rivals and other big titles.'
if ($state -and $applied) {
    $count = 0
    if ($state.PSObject.Properties.Name -contains 'gameProfileCount') {
        try { $count = [int]$state.gameProfileCount } catch { $count = 0 }
    }
    $names = @()
    if ($state.PSObject.Properties.Name -contains 'gameProfiles' -and $state.gameProfiles) {
        $names = @($state.gameProfiles | ForEach-Object { "$_" })
    }
    $deltas = $false
    if ($state.PSObject.Properties.Name -contains 'gameProfileDeltas') {
        $deltas = [bool]$state.gameProfileDeltas
    }
    $gameOk = [bool]$state.gameProfilesApplied -and $count -ge 10
    if ($gameOk) {
        $sample = ($names | Select-Object -First 6) -join ', '
        $deltaNote = if ($deltas) { ' + competitive/hybrid deltas' } else { ' (reapply for tier deltas)' }
        $gameDetail = "Imported $count game profiles from your series pack$deltaNote ($sample...)."
    } elseif ($count -gt 0) {
        $gameDetail = "Only $count game profiles recorded - reapply for the full catalog."
    }
}
$features.Add(@{
    title  = 'Per-game profiles'
    detail = $gameDetail
    active = $gameOk
})

# 4+) Exo is the control panel - verify LIVE via NVAPI/DRS, not NVIDIA CPL UI.
# Store NVIDIA Control Panel uses a virtualized registry hive and often shows stale/wrong radios.
$displayMarkerOk = [bool]($state -and $state.displayPrefs -and [string]$state.displayMethod -eq 'nvapi')
$displayLive = Test-NvidiaDisplayLive
# Optimus / iGPU-only panels: helper returns ok + skipped=no-active-nvidia-displays.
$displaySkippedNoPanels = [bool]($displayLive.Available -and (
    ([string]$displayLive.Detail -match 'no-active-nvidia-displays') -or
    ([string]$displayLive.Detail -eq 'no-active-nvidia-displays')
))
$displayLiveOk = [bool]$displayLive.Available -and ([bool]$displayLive.Ok -or $displaySkippedNoPanels)
# Live peak policy alone is enough after apply; marker is best-effort.
$displayOk = (-not $pendingAfterDriver) -and (-not $applyInProgress) -and (
    $displaySkippedNoPanels -or
    ($displayLiveOk -and ($displayMarkerOk -or [bool]$displayLive.Ok))
)

$features.Add(@{
    title  = 'Exo display policy (driver)'
    detail = $(if ($displaySkippedNoPanels) {
        'No active NVIDIA-connected panels (common on Optimus). Display step not required; 3D profiles still apply.'
    } elseif ($displayLive.Available) {
        "Live NVAPI: $($displayLive.Detail) | primary=max Hz, secondary=60 Hz, Full RGB, GPU no-scaling"
    } else {
        'Live NVAPI helper unavailable; display state cannot be verified.'
    })
    active = $displayOk
})

# Control Panel only path: App should be absent; classic CPL present (optional UI only).
$appInstalled = $false
foreach ($appPath in @(
    (Join-Path $env:ProgramFiles 'NVIDIA Corporation\NVIDIA App\CEF\NVIDIA App.exe'),
    (Join-Path $env:ProgramFiles 'NVIDIA Corporation\NVIDIA App\NVIDIA App.exe'),
    (Join-Path $env:ProgramFiles 'NVIDIA Corporation\NVIDIA Overlay\NVIDIA App.exe')
)) {
    if (Test-Path -LiteralPath $appPath) { $appInstalled = $true; break }
}
$cplInstalled = $false
$cplAppx = Get-AppxPackage -ErrorAction SilentlyContinue | Where-Object {
    $_.Name -match '(?i)NVIDIAControlPanel|NVIDIACorp\.NVIDIAControlPanel'
}
if ($cplAppx) { $cplInstalled = $true }
foreach ($cplPath in @(
    (Join-Path $env:ProgramFiles 'NVIDIA Corporation\Control Panel Client\nvcplui.exe'),
    (Join-Path $env:ProgramFiles 'NVIDIA Corporation\NVIDIA Control Panel\nvcplui.exe')
)) {
    if (Test-Path -LiteralPath $cplPath) { $cplInstalled = $true; break }
}
$controlPanelOnly = [bool]($state -and $state.PSObject.Properties.Name -contains 'controlPanelOnly' -and [bool]$state.controlPanelOnly)
$cplOk = $cplInstalled -or [bool]($state -and $state.nvidiaControlPanel) -or $controlPanelOnly
# Success = App gone (and preferably CPL gone). Display via Exo/NVAPI.
$clientOk = -not $appInstalled
if (-not $appInstalled -and $displayOk) { $clientOk = $true }
if ($state -and $state.PSObject.Properties.Name -contains 'exoPanel' -and [bool]$state.exoPanel -and -not $appInstalled) {
    $clientOk = $true
}

# 3D "advanced" = driver DRS profiles applied (Profile Inspector), NOT the CPL radio button.
# Store CPL virtual hive often shows "Let the 3D application decide" even when DRS is forced.
$advanced3dOk = [bool]$applied -and [bool]$gameOk
if ($state -and $state.PSObject.Properties.Name -contains 'profileApplied' -and [bool]$state.profileApplied -and $applied) {
    $advanced3dOk = $true
}

$features.Add(@{
    title  = 'Exo 3D profile (driver DRS)'
    detail = $(if ($advanced3dOk -and $drsLive -eq 'verified') {
        "$drsVerifiedText - Base + per-game profiles forced at driver level via Profile Inspector. Trust this over NVIDIA Control Panel radios."
    } elseif ($advanced3dOk) {
        'Base + per-game profiles forced at driver level via Profile Inspector. Trust this over NVIDIA Control Panel radios.'
    } elseif ($profileOk -and $drsLive -eq 'drifted') {
        $drsDriftedText
    } else {
        '3D profiles not fully verified. Apply to import Base + per-game packs.'
    })
    active = $advanced3dOk
    drsLive = $drsLive
    drsLiveText = $drsLiveText
})

$features.Add(@{
    title  = 'Driver only (no App / CPL)'
    detail = $(if (-not $appInstalled -and -not $cplInstalled) {
        'NVIDIA App and Control Panel removed. Use Exo NVIDIA Panel for display/video policy.'
    } elseif (-not $appInstalled -and $cplInstalled) {
        'App gone but Control Panel still installed. Re-Apply to strip CPL; Exo panel is the UI.'
    } else {
        'NVIDIA App still installed. Re-Apply to strip App + Control Panel.'
    })
    active = (-not $appInstalled) -and (-not $cplInstalled)
})

$backgroundOk = [bool]$debloat.Ok -and [bool]$overlay.Ok
$backgroundIssues = @($debloat.Issues) + @($overlay.Issues)
$features.Add(@{
    title  = 'Privacy / telemetry / overlay off'
    detail = $(if ($backgroundOk) {
        'Overlay preferences, capture, telemetry, updater, background helpers, and auto-start paths are inactive.'
    } else {
        "Performance background gap: $($backgroundIssues -join '; ')"
    })
    active = $backgroundOk
})

# Tray: hide NVDisplay container (IsPromoted=0); App ghosts should be gone - no logon tasks.
$trayHideOk = $true
$trayDetail = 'No NVIDIA tray keys found (or display container demoted).'
try {
    $trayRoot = 'HKCU:\Control Panel\NotifyIconSettings'
    $displayKeys = 0
    $displayHidden = 0
    $appGhosts = 0
    if (Test-Path -LiteralPath $trayRoot) {
        foreach ($key in @(Get-ChildItem -LiteralPath $trayRoot -ErrorAction SilentlyContinue)) {
            $item = Get-ItemProperty -LiteralPath $key.PSPath -ErrorAction SilentlyContinue
            $exe = [string]$item.ExecutablePath
            if (-not $exe) { continue }
            $isDisplay = if (Get-Command Test-ExoDisplayContainerExe -EA SilentlyContinue) {
                Test-ExoDisplayContainerExe $exe
            } else { $exe -match '(?i)NVDisplay\.Container|Display\.NvContainer' }
            $isApp = if (Get-Command Test-ExoNvidiaAppTrayExe -EA SilentlyContinue) {
                Test-ExoNvidiaAppTrayExe $exe
            } else { $exe -match '(?i)NVIDIA App|GFExperience|ShadowPlay' }
            if ($isDisplay) {
                $displayKeys++
                $prom = $null
                try { $prom = [int]$item.IsPromoted } catch { }
                if ($prom -eq 0) { $displayHidden++ } else { $trayHideOk = $false }
            } elseif ($isApp) {
                $appGhosts++
                $trayHideOk = $false
            }
        }
    }
    if ($displayKeys -gt 0) {
        $trayDetail = "Display container tray IsPromoted=0 on $displayHidden/$displayKeys; App ghosts=$appGhosts."
    } elseif ($appGhosts -gt 0) {
        $trayDetail = "App/GFE tray ghosts still present ($appGhosts)."
    }
} catch {
    $trayHideOk = $false
    $trayDetail = 'Tray inspection failed.'
}
$features.Add(@{
    title  = 'Taskbar tray (display hide / App gone)'
    detail = $trayDetail
    active = $trayHideOk
})

# Driver stage for isApplied: notebooks only need a readable driver; desktop needs tweaks/update gate.
$driverStageOk = if ($isNotebookGpu) { [bool]$currentNv } else { (-not $needsDriverAction) -and [bool]$currentNv }

$isApplied = $gpuOk -and (-not $pendingAfterDriver) -and (-not $applyInProgress) -and
             $applied -and $gameOk -and $displayOk -and $backgroundOk -and $clientOk -and $advanced3dOk -and
             $trayHideOk -and $driverStageOk

$driverChanged = $false
if ($state -and $currentNv -and $state.profileDriverVersion -and
    [string]$state.profileDriverVersion -ne [string]$currentNv) {
    $driverChanged = $true
}

$statusText = if (-not $gpuOk) { 'No NVIDIA GPU' }
elseif ($pendingAfterDriver) { 'Restart required' }
elseif (-not $currentNv) { 'Driver status unavailable' }
elseif (-not $isNotebookGpu -and $needsUpdate) { 'Driver update available' }
elseif (-not $isNotebookGpu -and $needsRetweak) { 'Driver tweaks available' }
elseif ($driverChanged -or (-not $profileOk -and $state -and $state.profileApplied)) { 'Driver changed - reapply' }
elseif ($profileOk -and $drsLive -eq 'drifted') { 'Profile drifted - reapply' }
elseif (-not $profileOk) { '3D profile incomplete' }
elseif (-not $gameOk) { 'Game profiles incomplete' }
elseif (-not $displayOk) { 'Display policy incomplete' }
elseif (-not $clientOk) { 'NVIDIA App still present' }
elseif (-not $advanced3dOk) { '3D profile incomplete' }
elseif (-not $backgroundOk) { 'Background re-armed - reapply' }
elseif (-not $trayHideOk) { 'Tray needs hide pass' }
elseif ($isApplied) { 'All applied' }
else { 'Not applied' }

$detail = if (-not $gpuOk) { 'Needs an NVIDIA GPU and current drivers.' }
elseif ($pendingAfterDriver) { 'Restart Windows, then Apply once more to finish profile and display setup.' }
elseif (-not $currentNv) { 'Could not read the NVIDIA driver version. Repair the driver, then refresh.' }
elseif ($isNotebookGpu -and -not $isApplied) { 'Laptop GPU: desktop auto-update is skipped. Apply still imports 3D profiles and display policy when panels are NVIDIA-connected.' }
elseif (-not $isNotebookGpu -and $needsUpdate) { 'Apply can install a clean display driver package, then continues with profiles and display prefs.' }
elseif (-not $isNotebookGpu -and $needsRetweak) { 'Driver version is current; Apply will set MSI/privacy tweaks in place.' }
elseif ($driverChanged) { "Driver is now $currentNv but last verified $($state.profileDriverVersion). Apply again." }
elseif ($profileOk -and $drsLive -eq 'drifted') { "The driver DRS no longer matches the imported Exo pack ($($drsMismatch.Count) pin(s) drifted). Apply again to re-import." }
elseif (-not $profileOk) { $(if ($applyInProgress) { 'Previous Apply was interrupted. Apply again.' } else { '3D profile not fully verified. Apply again.' }) }
elseif (-not $gameOk) { 'Base profile is present but per-game catalog is incomplete. Apply again.' }
elseif (-not $displayOk) { 'Display policy incomplete (resolution/refresh/color/scaling). Apply again or use Display panel.' }
elseif (-not $clientOk) { 'NVIDIA App is still installed. Apply removes it; Exo uses the driver directly.' }
elseif (-not $advanced3dOk) { '3D profiles not fully verified. Apply imports them at driver level.' }
elseif (-not $backgroundOk) { "Background settings need another pass ($($backgroundIssues -join '; '))." }
elseif ($isApplied) { 'Driver policy, 3D profiles, and display settings look good on this machine.' }
else { 'Apply to set profiles and display policy for this GPU.' }

[ordered]@{
    isApplied          = $isApplied
    statusText         = $statusText
    detail             = $detail
    features           = @($features)
    gpuName            = $(if ($primary) { $primary.Name } else { $null })
    series             = $series
    gsync              = [bool]($state -and $state.gsync)
    currentDriver      = $currentNv
    latestDriver       = $latestNv
    notebookGpu        = $isNotebookGpu
    needsDriverUpdate  = $needsUpdate
    needsDriverRetweak = $needsRetweak
    driverTweaksOk     = [bool]$tweaks.Ok
    drsLive            = $drsLive
    drsLiveText        = $drsLiveText
    drsMismatch        = @($drsMismatch)
    drsComparedCount   = $drsComparedCount
} | ConvertTo-Json -Compress -Depth 5
