# OptiHub - detect NVIDIA optimizer status (JSON for WinUI).
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

$features.Add(@{
    title  = 'NVIDIA GPU'
    detail = $(if ($gpuOk) { $primary.Name } else { 'NVIDIA GPU + drivers required.' })
    active = $gpuOk
})

$features.Add(@{
    title  = 'GPU series mapped'
    detail = $(if ($series) { "$series Series OptiHub .nip pack" } else { 'Map to 10/20/30/40/50 series.' })
    active = [bool]$series
})

$appOk = Test-NvidiaApp
if ($state -and $state.nvidiaApp) { $appOk = $true }
$features.Add(@{
    title  = 'NVIDIA App'
    detail = 'Downloads NVIDIA App when missing (MS Store / winget) for display and 3D UI.'
    active = $appOk
})

$features.Add(@{
    title  = 'Conflict cleanup + fresh App'
    detail = 'Removes old NVIDIA App / GFE / CPL leftovers before reinstall so nothing fights the new stack.'
    active = [bool]($state -and ($state.conflictCleanup -ge 0 -or $state.nvidiaApp))
})

$features.Add(@{
    title  = 'Privacy / telemetry trim'
    detail = 'Disables NvTelemetry tasks/services and quiet auto-download hints where possible.'
    active = [bool]($state -and $state.debloatApplied)
})

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

$winDrv = if ($primary) { [string](Get-CimInstance Win32_VideoController -ErrorAction SilentlyContinue | Where-Object { $_.Name -eq $primary.Name } | Select-Object -First 1 -ExpandProperty DriverVersion) } else { '' }
if (-not $winDrv -and $primary) {
    try { $winDrv = [string](Get-CimInstance Win32_VideoController | Where-Object { $_.Name -match 'nvidia|geforce' } | Select-Object -First 1).DriverVersion } catch { }
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

$driverNote = if ($needsUpdate) {
    "Update available: installed $(if($currentNv){$currentNv}else{'?'}) -> newest $latestNv. Apply prompts NVCleanstall."
} elseif ($latestNv) {
    "On newest Game Ready ($currentNv)."
} else {
    'Could not reach NVIDIA for newest version; Apply still checks and can open NVCleanstall.'
}
$features.Add(@{
    title  = 'Newest driver check'
    detail = $driverNote
    active = (-not $needsUpdate) -and [bool]$latestNv
})

$features.Add(@{
    title  = 'Display color / scaling prefs'
    detail = 'Prefers NVIDIA color path + Full RGB / high bpc guidance; confirm in NVIDIA App once.'
    active = [bool]($state -and $state.displayPrefs)
})

$applied = [bool]$state
$gsyncDetail = if ($state -and $state.gsync) { 'G-SYNC pack' } else { 'Low-latency pack' }
$features.Add(@{
    title  = '3D Base Profile applied'
    detail = $(if ($applied) { "$($state.series) Series $gsyncDetail ($($state.profileFile))" } else { 'FPS/latency Base Profile via Profile Inspector.' })
    active = $applied
})

$isApplied = $gpuOk -and $applied
$statusText = if (-not $gpuOk) { 'No NVIDIA GPU' }
elseif ($isApplied) { 'Already optimized' }
else { 'Ready to optimize' }

$detail = if (-not $gpuOk) { 'Needs an NVIDIA GPU and current drivers.' }
elseif ($isApplied) { "Applied $($state.series) Series$(if($state.gsync){' G-SYNC'}). Re-apply after big driver upgrades." }
else { "Detected $(if($series){"$series Series"}else{'NVIDIA'}). Toggle G-SYNC if your monitor supports it, then Apply." }

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
    needsDriverUpdate  = $needsUpdate
} | ConvertTo-Json -Compress -Depth 5
