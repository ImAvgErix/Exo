# OptiHub - detect NVIDIA optimizer status (JSON for WinUI).
# Feature order matches apply pipeline: GPU -> driver -> 3D profile -> App stack.
$ErrorActionPreference = 'SilentlyContinue'

function Get-NvidiaGpus {
    @(Get-CimInstance Win32_VideoController -ErrorAction SilentlyContinue | Where-Object {
        $_.Name -match '(?i)nvidia|geforce|rtx|gtx|quadro|titan'
    } | ForEach-Object { [pscustomobject]@{ Name = $_.Name; Driver = $_.DriverVersion } })
}

function Get-GpuSeriesFromName([string]$Name) {
    if ($Name -match '(?i)\b(?:RTX|GTX)\s*([1-5])0\d{2}\b') { return $Matches[1] + '0' }
    if ($Name -match '(?i)\b([1-5])0\d{2}\b') { return $Matches[1] + '0' }
    if ($Name -match '(?i)\b16\d{2}\b') { return '20' }
    return $null
}

function Test-NvidiaApp {
    foreach ($p in @(
        (Join-Path $env:ProgramFiles 'NVIDIA Corporation\NVIDIA App\CEF\NVIDIA App.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'NVIDIA Corporation\NVIDIA App\CEF\NVIDIA App.exe')
    )) { if (Test-Path $p) { return $true } }
    $a = Get-AppxPackage -Name '*NVIDIA*' -ErrorAction SilentlyContinue | Where-Object { $_.Name -match '(?i)App|GeForce' }
    return [bool]$a
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

function Test-OptiHubDriverInstallTweaks([string]$CurrentNv, $State) {
    # Same signals as Nvidia-Optimizer.ps1: stock Game Ready vs NVCleanstall-style install.
    $issues = New-Object System.Collections.Generic.List[string]

    $svc = Get-Service -Name 'NvTelemetryContainer' -ErrorAction SilentlyContinue
    if ($svc -and $svc.StartType -ne 'Disabled') {
        [void]$issues.Add('NvTelemetryContainer still enabled')
    }

    foreach ($p in @(
        (Join-Path $env:ProgramFiles 'NVIDIA Corporation\NVIDIA GeForce Experience'),
        (Join-Path $env:ProgramFiles 'NVIDIA Corporation\GeForce Experience')
    )) {
        if (Test-Path -LiteralPath $p) {
            [void]$issues.Add('GeForce Experience leftovers')
            break
        }
    }

    $msiSeen = $false
    $msiOn = $false
    try {
        $pci = 'HKLM:\SYSTEM\CurrentControlSet\Enum\PCI'
        if (Test-Path $pci) {
            Get-ChildItem $pci -ErrorAction SilentlyContinue | Where-Object {
                $_.PSChildName -match 'VEN_10DE'
            } | ForEach-Object {
                Get-ChildItem $_.PSPath -ErrorAction SilentlyContinue | ForEach-Object {
                    $msiKey = Join-Path $_.PSPath 'Device Parameters\Interrupt Management\MessageSignaledInterruptProperties'
                    if (-not (Test-Path $msiKey)) { return }
                    $msiSeen = $true
                    $v = (Get-ItemProperty -LiteralPath $msiKey -ErrorAction SilentlyContinue).MSISupported
                    if ($v -eq 1) { $msiOn = $true }
                }
            }
        }
    } catch { }
    if ($msiSeen -and -not $msiOn) {
        [void]$issues.Add('MSI not High/enabled (stock interrupt mode)')
    }

    $remembered = $false
    if ($State -and $State.driverTweaksVersion -and $CurrentNv -and
        [string]$State.driverTweaksVersion -eq [string]$CurrentNv) {
        $remembered = $true
    }

    return [pscustomobject]@{
        Ok         = [bool]($remembered -or ($issues.Count -eq 0))
        Remembered = $remembered
        Issues     = @($issues)
    }
}

$features = New-Object System.Collections.Generic.List[hashtable]
$gpus = Get-NvidiaGpus
$gpuOk = $gpus.Count -gt 0
$primary = if ($gpuOk) { $gpus[0] } else { $null }
$series = if ($primary) { Get-GpuSeriesFromName $primary.Name } else { $null }

$statePath = Join-Path $env:LOCALAPPDATA 'OptiHub\nvidia-optimizer.json'
$state = $null
if (Test-Path $statePath) {
    try { $state = Get-Content $statePath -Raw -Encoding UTF8 | ConvertFrom-Json } catch { }
}

# 1) GPU — name only (series is for profile pick; no "30 Series" suffix, no fancy dots that turn into ?)
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
    try {
        $winDrv = [string](Get-CimInstance Win32_VideoController -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -eq $primary.Name } |
            Select-Object -First 1 -ExpandProperty DriverVersion)
    } catch { }
}
if (-not $winDrv) {
    try {
        $winDrv = [string](Get-CimInstance Win32_VideoController |
            Where-Object { $_.Name -match 'nvidia|geforce' } |
            Select-Object -First 1).DriverVersion
    } catch { }
}
$currentNv = Convert-WindowsDriverToNvidia $winDrv
$latestNv = $null
$needsUpdate = $false
try {
    $url = 'https://gfwsl.geforce.com/services_toolkit/services/com/nvidia/services/AjaxDriverService.php?func=DriverManualLookup&psid=129&pfid=995&osID=57&languageCode=1033&beta=0&isWHQL=1&dltype=-1&dch=1&upCRD=0&qnf=0&ctk=null&windowsVersion=10.0&windowsArchitecture=64bit'
    $r = Invoke-RestMethod -Uri $url -Headers @{ 'User-Agent' = 'OptiHub-Nvidia/1.2' } -TimeoutSec 12
    if ($r.Success -eq '1') { $latestNv = [string]$r.IDS[0].downloadInfo.Version }
} catch { }

if ($latestNv -and $currentNv) {
    try {
        if ([version]$currentNv -lt [version]$latestNv) { $needsUpdate = $true }
    } catch {
        if ($currentNv -ne $latestNv) { $needsUpdate = $true }
    }
} elseif ($latestNv -and -not $currentNv) {
    $needsUpdate = $true
}

# Newest version alone is not enough — stock installs need NVCleanstall reinstall with tweaks.
$tweaks = Test-OptiHubDriverInstallTweaks $currentNv $state
$needsRetweak = (-not $needsUpdate) -and [bool]$latestNv -and [bool]$currentNv -and (-not $tweaks.Ok)
$needsDriverAction = $needsUpdate -or $needsRetweak

$driverNote = if ($needsUpdate) {
    $curLabel = if ($currentNv) { $currentNv } else { 'unknown' }
    "Update available: $curLabel -> $latestNv. Apply runs OptiHub Clean Driver (slim + silent + our tweaks)."
} elseif ($needsRetweak) {
    $gap = if ($tweaks.Issues.Count -gt 0) { ($tweaks.Issues -join '; ') } else { 'stock-style install signals' }
    "On newest Game Ready ($currentNv) but without OptiHub tweaks ($gap). Apply fixes MSI/privacy in-place."
} elseif ($latestNv) {
    "On newest Game Ready ($currentNv) with OptiHub clean-driver tweaks."
} else {
    'Could not reach NVIDIA; Apply still runs Clean Driver when online.'
}
$features.Add(@{
    title  = 'Driver (newest + install tweaks)'
    detail = $driverNote
    active = (-not $needsDriverAction) -and [bool]$latestNv
})

# 3) 3D profile — only "active" if we recorded a successful silent import (not just a filename)
$profileOk = $false
if ($state -and -not $state.pendingAfterDriver) {
    if ($state.PSObject.Properties.Name -contains 'profileApplied') {
        $profileOk = [bool]$state.profileApplied -and [bool]$state.profileFile
    } else {
        # Legacy marker without profileApplied = untrusted (may be false positive)
        $profileOk = $false
    }
}
$applied = $profileOk
$gsyncDetail = if ($state -and $state.gsync) { 'GSync pack' } else { 'No Gsync pack' }
$features.Add(@{
    title  = '3D Base Profile'
    detail = $(if ($applied) {
        $pf = if ($state.profileFile) { [string]$state.profileFile } else { 'profile applied' }
        "$gsyncDetail - $pf (silent import verified)"
    } else {
        'Not applied yet. Apply runs Profile Inspector -silentImport (no GUI / replace click).'
    })
    active = $applied
})

# 4+) App stack
$appOk = Test-NvidiaApp
if ($state -and $state.nvidiaApp) { $appOk = $true }
$cplOk = [bool]($state -and $state.nvidiaControlPanel)
if (-not $cplOk) {
    $cplOk = [bool](Get-AppxPackage -ErrorAction SilentlyContinue | Where-Object { $_.Name -match '(?i)^NVIDIACorp\.NVIDIAControlPanel$' })
}
$features.Add(@{
    title  = 'NVIDIA Control Panel'
    detail = 'Display UI only path: color = NVIDIA, scaling = GPU + No scaling + Override (both monitors). App not used.'
    active = $cplOk
})

$features.Add(@{
    title  = 'Privacy / telemetry / overlay off'
    detail = 'Telemetry trim + NVIDIA Overlay / ShadowPlay forced off when possible.'
    active = [bool]($state -and ($state.debloatApplied -or $state.overlayDisabled))
})

$features.Add(@{
    title  = 'Display color / scaling prefs'
    detail = 'GPU + No scaling + Override; color source NVIDIA / Full RGB when exposed.'
    active = [bool]($state -and $state.displayPrefs)
})

$isApplied = $gpuOk -and $applied -and (-not $needsDriverAction)
$statusText = if (-not $gpuOk) { 'No NVIDIA GPU' }
elseif ($needsUpdate) { 'Driver update available' }
elseif ($needsRetweak) { 'Reinstall driver with tweaks' }
elseif ($isApplied) { 'Already optimized' }
else { 'Ready to optimize' }

$detail = if (-not $gpuOk) { 'Needs an NVIDIA GPU and current drivers.' }
elseif ($needsUpdate) { 'Apply runs OptiHub Clean Driver (official package, stripped, silent), reboot, then Reapply for 3D + App.' }
elseif ($needsRetweak) { 'Version is newest; Apply will apply OptiHub MSI/privacy tweaks in-place (no re-download).' }
elseif ($isApplied) { 'Driver current with tweaks and 3D profile applied. Re-apply after big driver upgrades.' }
else { 'Toggle GSync if needed, then Apply.' }

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
    needsDriverUpdate  = $needsDriverAction
    needsDriverRetweak = $needsRetweak
    driverTweaksOk     = [bool]$tweaks.Ok
} | ConvertTo-Json -Compress -Depth 5
