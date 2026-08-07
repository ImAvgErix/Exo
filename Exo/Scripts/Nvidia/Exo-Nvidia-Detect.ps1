# Exo - detect NVIDIA optimizer status (JSON for WinUI).
# Feature order matches apply pipeline: GPU -> driver -> 3D profile -> display/privacy.
# Classifiers: NvidiaDetectCore.ps1 (pure) - keep aligned with NvidiaDetectLogic.cs
$ErrorActionPreference = 'SilentlyContinue'

$core = Join-Path $PSScriptRoot 'NvidiaDetectCore.ps1'
if (Test-Path -LiteralPath $core) { . $core }

function Get-NvObjectProperty($Object, [string]$Name, $Default = $null) {
    # NvidiaDetectCore turns on StrictMode for this whole session, so reading an
    # absent property off a registry object THROWS rather than yielding $null.
    # Same shape as Get-SteamObjectProperty; see the MSI block below for what
    # happens when a detect path forgets it.
    if (-not $Object) { return $Default }
    if ($Object -is [hashtable] -and $Object.ContainsKey($Name)) { return $Object[$Name] }
    if ($Object.PSObject -and ($Object.PSObject.Properties.Name -contains $Name)) { return $Object.$Name }
    return $Default
}

function Get-NvRegValue([string]$Path, [string]$Name, $Default = $null) {
    if (-not $Path) { return $Default }
    $item = Get-ItemProperty -LiteralPath $Path -ErrorAction SilentlyContinue
    return (Get-NvObjectProperty $item $Name $Default)
}

function Get-NvLiveService([string]$Name) {
    # Get-Service is not proof a service still exists. When a service is uninstalled -- and
    # Exo's own NVIDIA App wipe uninstalls FrameViewSdk, which owns FvSvc -- the SCM keeps a
    # marked-for-delete entry until the next reboot, after the registry key and the binary
    # are already gone. That ghost reports no StartType at all, so "-ne 'Disabled'" was TRUE
    # and every caller below counted a service that no longer exists as still enabled.
    # Nvidia-Optimizer.ps1 failed a whole Apply at post-verify on exactly this, one stage
    # after Set-Service had told it "The system cannot find the file specified".
    # The registry key is the authority: no key, no service.
    $svc = Get-Service -Name $Name -ErrorAction SilentlyContinue
    if (-not $svc) { return $null }
    if (-not (Test-Path -LiteralPath ("HKLM:\SYSTEM\CurrentControlSet\Services\" + $Name))) { return $null }
    return $svc
}

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
    $msiOk = $false
    $hdcpOk = $false
    $powerMizerOk = $false
    $pstateDisabled = $false

    foreach ($serviceName in @('NvTelemetryContainer', 'NvCamera', 'FvSvc')) {
        $svc = Get-NvLiveService $serviceName
        if ($svc -and $svc.StartType -ne 'Disabled') {
            [void]$issues.Add("$serviceName still enabled")
        }
    }
    $networkService = Get-NvLiveService 'NvContainerNetworkService'
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
                    # Every read here goes through the guarded helpers. A PCI enum node
                    # legitimately has no 'Class' value, and under StrictMode
                    # "$device.Class" threw on the FIRST node - which escaped the whole
                    # ForEach-Object into the catch below with msiSeen=0 and msiGaps=0.
                    # That is the "no display nodes visible, skip" case, so the check
                    # reported MSI High as fine on every machine without ever reading a
                    # single value. Absent MSISupported is precisely the NOT-applied
                    # state, so the failure mode was a guaranteed false green.
                    $device = Get-ItemProperty -LiteralPath $_.PSPath -ErrorAction SilentlyContinue
                    $class = [string](Get-NvObjectProperty $device 'Class' '')
                    $classGuid = [string](Get-NvObjectProperty $device 'ClassGUID' '')
                    if ($class -ne 'Display' -and
                        $classGuid -ne '{4d36e968-e325-11ce-bfc1-08002be10318}') {
                        return
                    }
                    $msiSeen++
                    $msiKey = Join-Path $_.PSPath 'Device Parameters\Interrupt Management\MessageSignaledInterruptProperties'
                    $aff = Join-Path $_.PSPath 'Device Parameters\Interrupt Management\Affinity Policy'
                    $v = Get-NvRegValue $msiKey 'MSISupported'
                    $priority = Get-NvRegValue $aff 'DevicePriority'
                    if ($v -ne 1 -or $priority -ne 3) { $msiGaps++ }
                }
            }
        }
    } catch { }
    # Only fail MSI when we can see display PCI nodes and they lack High priority.
    # msiSeen=0 (enum/permissions) is best-effort skip - not a mid-tier false fail.
    if ($msiSeen -gt 0 -and $msiGaps -gt 0) {
        [void]$issues.Add("MSI High missing on $msiGaps of $msiSeen NVIDIA display device(s)")
        $msiOk = $false
    } else {
        $msiOk = $true
    }

    # HDCP / PowerMizer / optional DisableDynamicPstate on display class nodes
    $hdcpSeen = 0; $hdcpHits = 0; $pmHits = 0; $pstateHits = 0
    try {
        $classRoot = 'HKLM:\SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}'
        if (Test-Path -LiteralPath $classRoot) {
            Get-ChildItem -LiteralPath $classRoot -ErrorAction SilentlyContinue | Where-Object {
                $_.PSChildName -match '^\d{4}$'
            } | ForEach-Object {
                $props = Get-ItemProperty -LiteralPath $_.PSPath -ErrorAction SilentlyContinue
                if (-not $props) { return }
                $desc = if ($props.PSObject.Properties.Name -contains 'DriverDesc') { [string]$props.DriverDesc } else { '' }
                $provider = if ($props.PSObject.Properties.Name -contains 'ProviderName') { [string]$props.ProviderName } else { '' }
                if ($desc -notmatch '(?i)NVIDIA|GeForce|RTX|GTX' -and $provider -notmatch '(?i)NVIDIA') { return }
                $hdcpSeen++
                if ($props.PSObject.Properties.Name -contains 'RMHdcpKeyglobZero' -and [int]$props.RMHdcpKeyglobZero -eq 1) { $hdcpHits++ }
                if ($props.PSObject.Properties.Name -contains 'PowerMizerLevel' -and [int]$props.PowerMizerLevel -eq 1) { $pmHits++ }
                if ($props.PSObject.Properties.Name -contains 'DisableDynamicPstate' -and [int]$props.DisableDynamicPstate -eq 1) { $pstateHits++ }
            }
        }
    } catch { }
    $hdcpOk = ($hdcpSeen -eq 0) -or ($hdcpHits -ge $hdcpSeen)
    $powerMizerOk = ($hdcpSeen -eq 0) -or ($pmHits -ge 1)
    $pstateDisabled = ($pstateHits -gt 0)

    $remembered = $false
    # Sparse intermediate states (reboot-pending / driver-fail writes) lack these keys - guard.
    if ($State -and ($State.PSObject.Properties.Name -contains 'driverTweaksVerified') -and
        [bool]$State.driverTweaksVerified -and
        ($State.PSObject.Properties.Name -contains 'driverTweaksVersion') -and $CurrentNv -and
        [string]$State.driverTweaksVersion -eq [string]$CurrentNv) {
        $remembered = $true
    }

    return [pscustomobject]@{
        Ok             = [bool]($issues.Count -eq 0)
        Remembered     = $remembered
        Issues         = @($issues)
        MsiSeen        = $msiSeen
        MsiOk          = [bool]$msiOk
        HdcpOk         = [bool]$hdcpOk
        PowerMizerOk   = [bool]$powerMizerOk
        PstateDisabled = [bool]$pstateDisabled
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
        $service = Get-NvLiveService $serviceName
        if ($service -and ($service.StartType -ne 'Disabled' -or $service.Status -eq 'Running')) {
            [void]$issues.Add("Service active: $serviceName")
        }
    }
    $networkService = Get-NvLiveService 'NvContainerNetworkService'
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
        # Status gate (matches Exo.NvDisplay): refresh AND color AND scaling, all read back live.
        # Note: NvidiaDetectCore uses StrictMode - never touch optional props without existence checks.
        $ok = $false
        $registryOk = $false; $colorOk = $false; $refreshOk = $false; $pathOk = $false
        $checks = $null
        if ($null -ne $status.PSObject.Properties['checks']) { $checks = $status.checks }
        if ($checks -and (Get-Command Test-ExoDisplayStatusOk -ErrorAction SilentlyContinue)) {
            if ($null -ne $checks.PSObject.Properties['refreshOk']) { $refreshOk = [bool]$checks.refreshOk }
            if ($null -ne $checks.PSObject.Properties['modesOk'] -and [bool]$checks.modesOk) { $refreshOk = $true }
            if ($null -ne $checks.PSObject.Properties['registryOk']) { $registryOk = [bool]$checks.registryOk }
            if ($null -ne $checks.PSObject.Properties['colorOk']) { $colorOk = [bool]$checks.colorOk }
            if ($null -ne $checks.PSObject.Properties['pathScalingOk']) { $pathOk = [bool]$checks.pathScalingOk }
            if ($null -ne $checks.PSObject.Properties['scalingOk'] -and [bool]$checks.scalingOk) { $pathOk = $true }
            $ok = Test-ExoDisplayStatusOk -RefreshOk $refreshOk -ColorOk $colorOk -ScalingOk $pathOk
        } elseif ($null -ne $status.PSObject.Properties['ok']) {
            # No checks block (older helper): fall back to the helper's own verdict.
            $ok = [bool]$status.ok
        }
        $skipped = $null
        if ($null -ne $status.PSObject.Properties['skipped']) { $skipped = [string]$status.skipped }
        $detail = if ($skipped) { $skipped } elseif ($checks) {
            "color=$colorOk, refresh=$refreshOk, scaling=$pathOk, registry=$registryOk, statusOk=$ok"
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
# Which policy actually ran, according to the state the optimizer wrote.
#
# This was hardcoded to $true, on the reasoning that the clean-driver/debloat pipeline was
# retired and only old state files would say otherwise. It is not retired - 4.4.0 turned it
# back on, and ModuleTweakVersion records nvidia 4.4.1 as "SafePolicy removed: app removal,
# debloat, overlay, GPU power now run". So on any current machine the hardcode inverted the
# truth: a real rig here holds safePolicy=false with debloatApplied=true, overlayDisabled=true
# and a full displayDetails list, and detect still rendered "Left as-is by the
# Control-Panel-only policy" for displays and for GPU power, and dropped the debloat row
# altogether. Exo changed the display, removed the NVIDIA client, and then showed the user a
# tile saying it had left both alone - the most invasive work it does, invisible.
#
# A missing flag still means genuinely old state, from before it was written; stay
# conservative there.
$safePolicy = $true
if ($state -and $null -ne $state.PSObject.Properties['safePolicy']) {
    $safePolicy = [bool]$state.safePolicy
}

# --- Live DRS verification (managed NPI GitHub Latest -exportCustomized) ---
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
    # Apply keeps Prefer maximum performance on game profiles only, not Base.
    [void]$map.Remove('274197361')
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

# Live DRS via Exo.NvDisplay --drs-status (native NVAPI readback). Prefer this over
# offline pack maps so isApplied cannot green without reading the driver.
# EXO_NVIDIA_LIVE_DRS=0 skips live read (debug only).
$drsLive = 'unavailable'
$drsLiveText = ''
$drsMismatch = @()
$drsComparedCount = 0
$drsExpectedMap = $null
$drsAppProfilesOk = $false
$npiManagedExe = Join-Path $env:LOCALAPPDATA 'Exo\tools\nvidiaProfileInspector\nvidiaProfileInspector.exe'
$skipLiveDrs = ($env:EXO_NVIDIA_LIVE_DRS -eq '0')
if ($state -and [bool]$state.profileApplied -and $state.profileFile) {
    $recordedPackPath = Join-Path $profilesDir ([string]$state.profileFile)
    $packPresent = Test-Path -LiteralPath $recordedPackPath -PathType Leaf
    if ($packPresent) {
        $drsExpectedMap = Get-ExoNipBaseProfileMap $recordedPackPath
    }
    # Prefer the combined last-import path if state recorded one; else series pack on disk.
    $statusNip = $null
    if ($state.PSObject.Properties.Name -contains 'combinedProfileFile' -and $state.combinedProfileFile) {
        $cand = [string]$state.combinedProfileFile
        if (Test-Path -LiteralPath $cand) { $statusNip = $cand }
    }
    if (-not $statusNip -and $packPresent) { $statusNip = $recordedPackPath }

    if (-not $skipLiveDrs -and $statusNip) {
        $nvExe = $null
        if (Get-Command Get-ExoNvDisplayPath -ErrorAction SilentlyContinue) {
            try { $nvExe = Get-ExoNvDisplayPath } catch { }
        }
        if (-not $nvExe) {
            foreach ($c in @(
                (Join-Path $PSScriptRoot 'tools\Exo.NvDisplay.exe'),
                (Join-Path $env:LOCALAPPDATA 'Exo\app\Exo.NvDisplay.exe'),
                (Join-Path $env:LOCALAPPDATA 'Exo\tools\Exo.NvDisplay.exe')
            )) {
                if (Test-Path -LiteralPath $c) { $nvExe = $c; break }
            }
        }
        if ($nvExe) {
            try {
                $stOut = & $nvExe --drs-status "$statusNip" 2>&1
                $stExit = $LASTEXITCODE
                $appLine = @($stOut) | Where-Object { "$_" -match 'app-profiles written=' } | Select-Object -Last 1
                $appVerified = 0; $appExpected = 0
                if ("$appLine" -match 'verified=(\d+)\s+expected=(\d+)') {
                    $appVerified = [int]$Matches[1]
                    $appExpected = [int]$Matches[2]
                }
                # Base-only series .nip has app-profiles=0. Exit 3 then means some Base pins
                # this driver does not implement — normal, not "profiles drifted".
                $statusIsBaseOnly = $appExpected -eq 0
                if ($appExpected -gt 0 -and $appVerified -ge [Math]::Max(1, [int]($appExpected * 0.6))) {
                    $drsAppProfilesOk = $true
                }
                if ($stExit -eq 0) {
                    $drsLive = 'verified'
                    $drsComparedCount = 1
                } elseif ($stExit -eq 3 -and $statusIsBaseOnly) {
                    $drsLive = 'verified'
                    $drsComparedCount = 1
                    $drsMismatch = @()
                } elseif ($stExit -eq 3) {
                    $drsLive = if ($drsAppProfilesOk) { 'verified' } else { 'drifted' }
                    $drsComparedCount = 1
                    if ($drsLive -eq 'drifted') { $drsMismatch = @('live DRS partial / app profiles incomplete') }
                } else {
                    $drsLive = 'drifted'
                    $drsMismatch = @("drs-status exit $stExit")
                }
            } catch {
                $drsLive = 'unavailable'
            }
        }
    }

    # Optional slow NPI export path when forced
    if ($env:EXO_NVIDIA_LIVE_DRS -eq '1' -and $packPresent -and (Test-Path -LiteralPath $npiManagedExe) -and
        (Get-Command Get-ExoDrsVerificationResult -ErrorAction SilentlyContinue) -and $drsLive -eq 'unavailable') {
        $drsExportPath = Invoke-ExoNpiExportCustomized $npiManagedExe
        $drsExportedMap = $null
        if ($drsExportPath) {
            try { $drsExportedMap = Get-ExoDrsExportBaseMap $drsExportPath }
            finally { Remove-Item -LiteralPath $drsExportPath -Force -ErrorAction SilentlyContinue }
        }
        $drsRequiredPins = @('390467', '277041152', '277041154', '294973784', '11041279', '11041231')
        $drsResult = Get-ExoDrsVerificationResult -Expected $drsExpectedMap -Exported $drsExportedMap -RequiredIds $drsRequiredPins
        $drsLive = [string]$drsResult.Status
        $drsMismatch = @($drsResult.Mismatches)
        $drsComparedCount = [int]$drsResult.ComparedCount
    }
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
    title  = 'NVIDIA graphics ready'
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
if (-not $safePolicy -and -not $isNotebookGpu -and $driverBranch) {
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
$tweaks = if ($safePolicy) {
    [pscustomobject]@{
        Ok = $true; Remembered = $false; Issues = @(); MsiSeen = 0
        MsiOk = $true; HdcpOk = $true; PowerMizerOk = $true; PstateDisabled = $false
    }
} else { Test-ExoDriverInstallTweaks $currentNv $state }
$debloat = if ($safePolicy) {
    [pscustomobject]@{ Ok = $true; Issues = @() }
} else { Test-NvidiaPerformanceDebloat }
$overlay = if ($safePolicy) {
    [pscustomobject]@{ Ok = $true; Issues = @() }
} else { Test-NvidiaOverlayDisabled }
$needsRetweak = (-not $safePolicy) -and (-not $needsUpdate) -and [bool]$currentNv -and (-not $tweaks.Ok)
# Notebook: never auto-download desktop GRD - but do NOT treat that as a permanent fail.
# Profiles, display policy, and debloat still apply on laptops.
$needsDriverAction = if ($safePolicy) {
    -not [bool]$currentNv
} elseif ($isNotebookGpu) {
    -not [bool]$currentNv   # only fail driver stage if we cannot see any NVIDIA driver
} else {
    $needsUpdate -or $needsRetweak
}

$driverNote = if ($safePolicy -and $currentNv) {
    "Driver $currentNv detected. Exo leaves driver installation and MSI/service policy unchanged; update through NVIDIA when you choose."
} elseif ($isNotebookGpu) {
    if ($currentNv) {
        "Laptop GPU with driver $currentNv. Desktop auto-update is skipped; use NVIDIA notebook driver if you need a newer build. Profiles and display still apply via Exo."
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
    "Installed Game Ready $currentNv with Exo tweaks. NVIDIA update service is currently unavailable."
}
$features.Add(@{
    title  = $(if ($safePolicy) { 'Driver detected' } else { 'Game Ready driver tuned' })
    detail = $driverNote
    active = (-not $needsDriverAction) -and [bool]$currentNv
})

# 3) 3D profile - fail closed on interrupted runs, driver changes, legacy
# markers, missing pack metadata, or an asset hash mismatch.
# Sparse / legacy (2.x) state files may lack these keys - guard every read.
$pendingAfterDriver = [bool]($state -and ($state.PSObject.Properties.Name -contains 'pendingAfterDriver') -and [bool]$state.pendingAfterDriver)
$applyInProgress = [bool]($state -and ($state.PSObject.Properties.Name -contains 'applyInProgress') -and [bool]$state.applyInProgress)
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
$hardwarePolicy = if ($state -and ($state.PSObject.Properties.Name -contains 'hardwarePolicy')) { $state.hardwarePolicy } else { $null }
$hardwareSummary = if ($hardwarePolicy) {
    $gpuLabel = if ($primary) { [string]$primary.Name } else { [string]$state.gpuName }
    $modeLabel = if ($hardwarePolicy.primaryMode) { [string]$hardwarePolicy.primaryMode } else { 'display mode unknown' }
    $connectionLabel = if ($hardwarePolicy.primaryConnection) { [string]$hardwarePolicy.primaryConnection } else { 'connection unknown' }
    $policyLabel = if ([bool]$state.gsync) { 'adaptive sync' } else { 'raw latency' }
    "$gpuLabel - $modeLabel $connectionLabel - $policyLabel"
} elseif ($primary) {
    "$($primary.Name) - hardware policy will be selected on Apply"
} else { 'Hardware inventory unavailable' }
$features.Add(@{
    title  = 'Matched to your display'
    detail = $hardwareSummary
    active = [bool]$hardwarePolicy
})
$features.Add(@{
    title  = 'Competitive 3D profile'
    detail = $(if ($profileOk -and $drsLive -eq 'drifted') {
        $drsDriftedText
    } elseif ($applied -and $drsLive -eq 'verified') {
        $pf = if ($state.profileFile) { [string]$state.profileFile } else { 'profile applied' }
        "$gsyncDetail - $pf ($drsVerifiedText)"
    } elseif ($applied) {
        $pf = if ($state.profileFile) { [string]$state.profileFile } else { 'profile applied' }
        "$gsyncDetail - $pf (imported and verified)"
    } else {
        'Apply loads a silent competitive profile for max FPS or G-SYNC - no Control Panel clicking.'
    })
    active = $applied
    drsLive = $drsLive
    drsLiveText = $drsLiveText
})

$gsyncPolicy = [bool]($state -and $state.gsync)
$latencyPolicyOk = $profileOk -and ($drsLive -ne 'drifted') -and $drsExpectedMap -and
    [string]$drsExpectedMap['390467'] -eq '2' -and
    [string]$drsExpectedMap['277041152'] -eq '1' -and
    [string]$drsExpectedMap['11041279'] -eq '0' -and
    [string]$drsExpectedMap['11041231'] -eq $(if ($gsyncPolicy) { '1199655232' } else { '138504007' })
$features.Add(@{
    title  = 'Low-latency rendering'
    detail = $(if ($gsyncPolicy) {
        'G-SYNC / VRR pack: ultra low latency with adaptive sync. Reflex still wins in supported titles.'
    } else {
        'Competitive pack: G-SYNC off, VSync off, ultra low latency - built for minimum input lag.'
    })
    active = [bool]$latencyPolicyOk
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
    # Prefer live app-profile verification when --drs-status ran against a multi-profile pack.
    # Status often runs against the series Base-only .nip (0 app profiles) — exit 0 there must
    # not wipe a good Apply that already verified 46/46 game profiles at write time.
    $gameOk = $false
    if ($drsAppProfilesOk) {
        $gameOk = $true
    } elseif ($state -and ($state.PSObject.Properties.Name -contains 'gameProfilesApplied') -and [bool]$state.gameProfilesApplied -and $count -ge 10) {
        # Trust Apply state unless live DRS explicitly drifted.
        $gameOk = ($drsLive -ne 'drifted')
    }
    if ($gameOk) {
        $sample = ($names | Select-Object -First 6) -join ', '
        $deltaNote = if ($deltas) { ' + competitive/hybrid deltas' } else { ' (reapply for tier deltas)' }
        $gameDetail = "Imported $count game profiles from your series pack$deltaNote ($sample...)."
    } elseif ($count -gt 0) {
        $gameDetail = "Only $count game profiles recorded - reapply for the full catalog."
    }
}
$features.Add(@{
    title  = 'Per-title game profiles'
    detail = $gameDetail
    active = $gameOk
})

# Power management prefer-max is per-game (setting 274197361), not Base - after $gameOk.
$powerMgmtOk = [bool]$applied -and [bool]$gameOk
if ($drsExpectedMap -and $drsExpectedMap.ContainsKey('274197361')) {
    $powerMgmtOk = $powerMgmtOk -and ([string]$drsExpectedMap['274197361'] -eq '1')
}
$features.Add(@{
    title  = 'Maximum GPU power'
    detail = $(if ($powerMgmtOk) {
        'Games stay on Prefer maximum performance so clocks do not drop mid-fight.'
    } else {
        'Apply locks games to maximum performance power so the GPU does not idle down under load.'
    })
    active = [bool]$powerMgmtOk -or [bool]$tweaks.PowerMizerOk
})

# 4+) Exo is the control panel - verify LIVE via NVAPI/DRS, not NVIDIA CPL UI.
# Store NVIDIA Control Panel uses a virtualized registry hive and often shows stale/wrong radios.
$displayMarkerOk = [bool]($state -and $state.displayPrefs -and [string]$state.displayMethod -eq 'nvapi')
$displayLive = if ($safePolicy) {
    [pscustomobject]@{ Available = $true; Ok = $true; Detail = 'unchanged by safe policy' }
} else { Test-NvidiaDisplayLive }
# Optimus / iGPU-only panels: helper returns ok + skipped=no-active-nvidia-displays.
$displaySkippedNoPanels = [bool]($displayLive.Available -and (
    ([string]$displayLive.Detail -match 'no-active-nvidia-displays') -or
    ([string]$displayLive.Detail -eq 'no-active-nvidia-displays')
))
$displayLiveOk = [bool]$displayLive.Available -and ([bool]$displayLive.Ok -or $displaySkippedNoPanels)
# Live display policy alone is enough after apply; marker is best-effort.
$displayOk = $safePolicy -or ((-not $pendingAfterDriver) -and (-not $applyInProgress) -and (
    $displaySkippedNoPanels -or
    ($displayLiveOk -and ($displayMarkerOk -or [bool]$displayLive.Ok))
))

# Display state is measured above ($displayOk) from live NVAPI. It used to be reported
# as a hardcoded active=$true row reading "left for you in Control Panel" - a permanently
# green checkbox that stayed green with a monitor running below its max refresh or stuck
# on limited RGB. Exo drives these through Exo.NvDisplay now, so the row reports the truth.
$features.Add(@{
    title  = 'Display colors, refresh & scaling'
    detail = if ($safePolicy) {
        'Left as-is by the Control-Panel-only policy.'
    } elseif ($displaySkippedNoPanels) {
        'No NVIDIA-driven panels attached.'
    } elseif ($displayOk) {
        'Every panel at full RGB, its highest refresh, and GPU no-scaling.'
    } else {
        "Not at best values yet - $($displayLive.Detail)"
    }
    active = [bool]$displayOk
})

# GPU power and thermal ceilings. Read from the state file rather than probed live, because
# a live NVAPI query per detect would spin the helper up on every dashboard refresh.
#
# The honest complication: on a locked board (most laptops, many Founders cards) the maximum
# the VBIOS reports IS the default, so there is nothing to raise. Exo records that as a
# successful apply with nothing changed, and the row has to say so - reporting "not applied"
# for a card that cannot go higher would ask the user to fix something unfixable, every time
# they open the app.
$gpuPowerOk = [bool]($state -and $state.PSObject.Properties['gpuPower'] -and $state.gpuPower)
$gpuPowerDetail = ''
if ($state -and $state.PSObject.Properties['gpuPowerDetail']) { $gpuPowerDetail = [string]$state.gpuPowerDetail }
$gpuPowerAtCeiling = $gpuPowerDetail -match 'changed=0'
$features.Add(@{
    title  = 'GPU power & thermal ceiling'
    detail = if ($safePolicy) {
        'Left as-is by the Control-Panel-only policy.'
    } elseif ($gpuPowerOk -and $gpuPowerAtCeiling) {
        'Already at the highest limit this board allows - nothing left to raise.'
    } elseif ($gpuPowerOk) {
        'Power and thermal limits raised to the board maximum; fans on the driver curve.'
    } elseif ($gpuPowerDetail) {
        "Not raised yet - $gpuPowerDetail"
    } else {
        'Not raised yet. Apply lifts both to whatever ceiling the board reports.'
    }
    active = [bool]$gpuPowerOk
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
$cplOk = $cplInstalled -or [bool]($state -and ($state.PSObject.Properties.Name -contains 'nvidiaControlPanel') -and [bool]$state.nvidiaControlPanel) -or $controlPanelOnly
# Success = App gone (and preferably CPL gone). Display via Exo/NVAPI.
$clientOk = if ($safePolicy) { $cplOk } else { -not $appInstalled }
if (-not $safePolicy -and -not $appInstalled -and $displayOk) { $clientOk = $true }
if ($state -and $state.PSObject.Properties.Name -contains 'exoPanel' -and [bool]$state.exoPanel -and -not $appInstalled) {
    $clientOk = $true
}

# 3D "advanced" = driver DRS profiles applied (Profile Inspector), NOT the CPL radio button.
# Store CPL virtual hive often shows "Let the 3D application decide" even when DRS is forced.
$advanced3dOk = [bool]$applied -and [bool]$gameOk
if ($state -and $state.PSObject.Properties.Name -contains 'profileApplied' -and [bool]$state.profileApplied -and $applied) {
    $advanced3dOk = $true
}

if (-not $safePolicy) {
  $features.Add(@{
      title  = 'Forced driver profiles'
      detail = $(if ($advanced3dOk -and $drsLive -eq 'verified') {
          "$drsVerifiedText - base and per-game profiles locked at the driver. Trust this over Control Panel radios."
      } elseif ($advanced3dOk) {
          'Base and per-game profiles locked at the driver. Trust this over Control Panel radios.'
      } elseif ($profileOk -and $drsLive -eq 'drifted') {
          $drsDriftedText
      } else {
          'Apply imports competitive base and per-game profiles at the driver level.'
      })
      active = $advanced3dOk
      drsLive = $drsLive
      drsLiveText = $drsLiveText
    })
}

$features.Add(@{
    title  = 'Control Panel access'
    detail = $(if ($cplOk) {
        'NVIDIA Control Panel is installed. Exo still applies display (full RGB, highest refresh, GPU scaling) through NVAPI - the panel is optional UI only.'
    } else {
        'NVIDIA Control Panel is not installed. Exo applies display settings through NVAPI without it; install the panel yourself if you want NVIDIA''s UI.'
    })
    # Optional UI only - absence is not a miss. Exo does not install Control Panel.
    active = $true
    informational = $true
})

$backgroundOk = [bool]$debloat.Ok -and [bool]$overlay.Ok
$backgroundIssues = @($debloat.Issues) + @($overlay.Issues)
if (-not $safePolicy) {
  $features.Add(@{
      title  = 'Privacy & background quiet'
      detail = $(if ($backgroundOk) {
          'NVIDIA telemetry, FrameView, and noisy background helpers stay off so they do not steal frames.'
      } else {
          "Background noise still active: $($backgroundIssues -join '; ')"
      })
      active = $backgroundOk
  })
  $features.Add(@{
      title  = 'Overlay & capture quiet'
      detail = $(if ([bool]$overlay.Ok) {
          'ShadowPlay / Share / Overlay stay out of the frame-time path while you play.'
      } else {
          "Overlay still active: $((@($overlay.Issues) -join '; '))"
      })
      active = [bool]$overlay.Ok
  })
  $features.Add(@{
      title  = 'Low-latency interrupts'
      detail = $(if ([bool]$tweaks.MsiOk) {
          if ([int]$tweaks.MsiSeen -gt 0) {
              "High-priority MSI interrupts on $($tweaks.MsiSeen) NVIDIA display device(s)."
          } else {
              'Low-latency interrupt mode applied (a reboot can finish stickiness).'
          }
      } else {
          "Interrupt tuning incomplete: $((@($tweaks.Issues | Where-Object { $_ -match 'MSI' }) -join '; '))"
      })
      active = [bool]$tweaks.MsiOk
  })
  $features.Add(@{
      title  = 'HDCP off for gaming'
      detail = $(if ([bool]$tweaks.HdcpOk) {
          'HDCP protection path disabled for lower overhead. Some protected video may need it back on.'
      } else {
          'Apply turns off HDCP on the display driver for a cleaner gaming path.'
      })
      active = [bool]$tweaks.HdcpOk
  })
  # Polarity is deliberately inverted from what it used to be. This row read "active" when
  # DisableDynamicPstate was SET, so the row was a reward for carrying an undocumented
  # override of the driver's power-state machine. It crashed the NVIDIA usermode driver on a
  # real machine; Apply removes it now, so a clean node is the good state and a node that
  # still has it is what needs Apply.
  $features.Add(@{
      title  = 'GPU power state'
      detail = $(if ([bool]$tweaks.PstateDisabled) {
          'DisableDynamicPstate is still set. It overrides the driver power-state machine and crashed the NVIDIA driver; Apply removes it, then reboot.'
      } else {
          'Driver manages its own P-states. PowerMizer already asks for prefer-maximum through a key NVIDIA documents.'
      })
      active = (-not [bool]$tweaks.PstateDisabled)
  })
  # Host Game Mode / HAGS / Game Bar live on the Windows card only.
}

# Driver stage for isApplied: notebooks only need a readable driver; desktop needs tweaks/update gate.
$driverStageOk = if ($safePolicy -or $isNotebookGpu) { [bool]$currentNv } else { (-not $needsDriverAction) -and [bool]$currentNv }

# isApplied used to be DRS-only, explicitly ignoring display prefs on the grounds that
# scaling and color were "manual in Control Panel". That stopped being true once
# Exo.NvDisplay could set mode, colour range, bit depth and scaling - and it meant the
# app would call a rig fully optimized while a 165 Hz panel ran at 120 and a second
# monitor sat on limited RGB. If Exo drives it, Exo has to count it.
# $displayOk is already $true on the safePolicy path, which leaves displays alone by design.
$isApplied = if ($safePolicy) {
    $gpuOk -and (-not $pendingAfterDriver) -and (-not $applyInProgress) -and
    $applied -and $gameOk -and $advanced3dOk -and $driverStageOk -and $latencyPolicyOk
} else {
    $gpuOk -and (-not $pendingAfterDriver) -and (-not $applyInProgress) -and
    $applied -and $gameOk -and $backgroundOk -and $clientOk -and $advanced3dOk -and
    $driverStageOk -and $latencyPolicyOk -and $displayOk
}

$driverChanged = $false
if ($state -and $currentNv -and ($state.PSObject.Properties.Name -contains 'profileDriverVersion') -and
    $state.profileDriverVersion -and
    [string]$state.profileDriverVersion -ne [string]$currentNv) {
    $driverChanged = $true
}

$lastErrorStage = if ($state -and ($state.PSObject.Properties.Name -contains 'lastErrorStage') -and $state.lastErrorStage) { [string]$state.lastErrorStage } else { '' }
$lastError = if ($state -and ($state.PSObject.Properties.Name -contains 'lastError') -and $state.lastError) { [string]$state.lastError } else { '' }
$applyStatus = if ($state -and ($state.PSObject.Properties.Name -contains 'applyStatus') -and $state.applyStatus) { [string]$state.applyStatus } else { '' }
$partialReasons = if ($state -and ($state.PSObject.Properties.Name -contains 'partialReasons')) { @($state.partialReasons | ForEach-Object { [string]$_ }) } else { @() }

# A completed command and a complete result are different things. Preserve an
# explicit partial marker from Apply even if the live feature set happens to
# look green on a driver branch that cannot enumerate every app profile.
if ($applyStatus -eq 'partial') {
    $isApplied = $false
} elseif (
    $applyStatus -eq 'applied' -and
    @($partialReasons | Where-Object { $_ }).Count -eq 0 -and
    $state -and
    [bool]$state.profileApplied -and
    [bool]$state.gameProfilesApplied -and
    $drsLive -ne 'drifted'
) {
    # Full Apply recorded — trust it for status rows. Soft latency map / Base-only
    # status must not paint "Off: Competitive 3D / game profiles" after a clean Apply.
    $isApplied = $true
    $applied = $true
    $gameOk = $true
    $latencyPolicyOk = $true
    $powerMgmtOk = $true
    $advanced3dOk = $true
    $pf = if ($state.profileFile) { [string]$state.profileFile } else { 'profile applied' }
    $nGames = 0
    try { $nGames = [int]$state.gameProfileCount } catch { $nGames = 0 }
    foreach ($f in $features) {
        switch -Regex ([string]$f.title) {
            '^Competitive 3D profile$' {
                $f.active = $true
                $f.detail = "$gsyncDetail - $pf (verified)"
                if ($f.ContainsKey('drsLive')) { $f.drsLive = 'verified' }
                if ($f.ContainsKey('drsLiveText')) { $f.drsLiveText = 'Verified in driver' }
            }
            '^Low-latency rendering$' { $f.active = $true }
            '^Per-title game profiles$' {
                $f.active = $true
                if ($nGames -gt 0) {
                    $f.detail = "Imported $nGames game profiles from your series pack."
                }
            }
            '^Maximum GPU power$' { $f.active = $true }
            '^Forced driver profiles$' {
                $f.active = $true
                $f.detail = 'Base and per-game profiles locked at the driver.'
                if ($f.ContainsKey('drsLive')) { $f.drsLive = 'verified' }
            }
        }
    }
}

$statusText = if (-not $gpuOk) { 'No NVIDIA GPU' }
elseif ($pendingAfterDriver) { 'Restart required' }
elseif ($applyInProgress -and $lastErrorStage) { ("Failed at {0}" -f $lastErrorStage) }
elseif (-not $currentNv) { 'Driver status unavailable' }
elseif (-not $safePolicy -and -not $isNotebookGpu -and $needsUpdate) { 'Driver update available' }
elseif (-not $isNotebookGpu -and $needsRetweak) { 'Driver tweaks available' }
elseif ($driverChanged -or (-not $profileOk -and $state -and ($state.PSObject.Properties.Name -contains 'profileApplied') -and $state.profileApplied)) { 'Driver changed - reapply' }
elseif ($profileOk -and $drsLive -eq 'drifted') { 'Profile drifted - reapply' }
elseif (-not $profileOk) { '3D profile incomplete' }
elseif (-not $gameOk) { 'Game profiles incomplete' }
elseif (-not $safePolicy -and -not $clientOk) { 'NVIDIA App still present' }
elseif (-not $advanced3dOk) { '3D profile incomplete' }
elseif (-not $safePolicy -and -not $backgroundOk) { 'Background re-armed - reapply' }
elseif ($isApplied) { 'All applied' }
elseif ($applyStatus -eq 'partial') { 'Partially applied' }
else { 'Not applied' }

$detail = if (-not $gpuOk) { 'Needs an NVIDIA GPU and current drivers.' }
elseif ($pendingAfterDriver) { 'Restart Windows, then Apply once more to finish Profile Inspector import.' }
elseif ($applyInProgress -and $lastError) { $lastError }
elseif (-not $currentNv) { 'Could not read the NVIDIA driver version. Repair the driver, then refresh.' }
elseif ($isNotebookGpu -and -not $isApplied) { 'Laptop GPU: desktop auto-update is skipped. Apply still imports 3D profiles via Profile Inspector.' }
elseif (-not $safePolicy -and -not $isNotebookGpu -and $needsUpdate) { 'Apply can install a clean display driver package, then continues with Profile Inspector packs.' }
elseif (-not $isNotebookGpu -and $needsRetweak) { 'Driver version is current; Apply will set MSI/privacy tweaks in place.' }
elseif ($driverChanged) { "Driver is now $currentNv but last verified $($state.profileDriverVersion). Apply again." }
elseif ($profileOk -and $drsLive -eq 'drifted') { "The driver DRS no longer matches the imported Exo pack ($($drsMismatch.Count) pin(s) drifted). Apply again to re-import." }
elseif (-not $profileOk) { $(if ($applyInProgress) { 'Previous Apply was interrupted. Apply again.' } else { '3D profile not fully verified. Apply again (Profile Inspector).' }) }
elseif (-not $gameOk) { 'Base profile is present but per-game catalog is incomplete. Apply again.' }
elseif (-not $safePolicy -and -not $clientOk) { 'NVIDIA App is still installed. Apply removes it; Exo uses the driver directly.' }
elseif (-not $advanced3dOk) { '3D profiles not fully verified. Apply imports them via Profile Inspector.' }
elseif (-not $safePolicy -and -not $displayOk) { "Your displays are not at their best values yet - $($displayLive.Detail). Apply sets full RGB, highest refresh, and GPU no-scaling." }
elseif (-not $safePolicy -and -not $backgroundOk) { "Background settings need another pass ($($backgroundIssues -join '; '))." }
elseif ($isApplied -and $safePolicy) { 'Profile Inspector Base + per-game DRS verified. Displays left alone by the Control-Panel-only policy.' }
elseif ($isApplied) { 'Driver policy, 3D packs, and every display at full RGB / highest refresh / GPU no-scaling.' }
elseif ($applyStatus -eq 'partial' -and $partialReasons.Count -gt 0) { 'Apply completed with gaps: ' + ($partialReasons -join ' ') }
else { 'Apply imports 3D profiles and sets every display to full RGB, its highest refresh, and GPU no-scaling.' }

[ordered]@{
    isApplied          = $isApplied
    statusText         = $statusText
    detail             = $detail
    features           = @($features)
    gpuName            = $(if ($primary) { $primary.Name } else { $null })
    series             = $series
    gsync              = [bool]($state -and $state.gsync)
    hardwareSummary    = $hardwareSummary
    policySource       = $(if ($hardwarePolicy -and $hardwarePolicy.selectionSource) { [string]$hardwarePolicy.selectionSource } else { '' })
    primaryRefreshHz   = $(if ($hardwarePolicy) { [int]$hardwarePolicy.primaryCurrentHz } else { 0 })
    primaryMaxRefreshHz = $(if ($hardwarePolicy) { [int]$hardwarePolicy.primaryMaxHz } else { 0 })
    currentDriver      = $currentNv
    latestDriver       = $latestNv
    notebookGpu        = $isNotebookGpu
    needsDriverUpdate  = $needsUpdate
    needsDriverRetweak = $needsRetweak
    safePolicy         = $safePolicy
    driverTweaksOk     = [bool]$tweaks.Ok
    applyStatus        = $applyStatus
    partialReasons     = @($partialReasons)
    drsLive            = $drsLive
    drsLiveText        = $drsLiveText
    drsMismatch        = @($drsMismatch)
    drsComparedCount   = $drsComparedCount
} | ConvertTo-Json -Compress -Depth 5
