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

$driverNote = 'NVCleanstall launches when the driver is older than ~45 days (or ForceDriver).'
if ($state -and $state.driverUpdatePass) {
    try {
        $dup = $state.driverUpdatePass
        if ($dup.Ran) { $driverNote = "NVCleanstall pass ran (driver age ~$($dup.AgeDays)d)." }
        else { $driverNote = "Driver looked recent (~$($dup.AgeDays)d) - NVCleanstall skipped." }
    } catch { }
}
$features.Add(@{
    title  = 'Driver update (NVCleanstall)'
    detail = $driverNote
    active = $true
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
    isApplied  = $isApplied
    statusText = $statusText
    detail     = $detail
    features   = @($features)
    gpuName    = $(if ($primary) { $primary.Name } else { $null })
    series     = $series
    gsync      = [bool]($state -and $state.gsync)
} | ConvertTo-Json -Compress -Depth 5
