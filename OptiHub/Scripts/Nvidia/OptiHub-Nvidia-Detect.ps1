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

# 1) GPU
$gpuDetail = if (-not $gpuOk) {
    'NVIDIA GPU + drivers required.'
} elseif ($series) {
    "$($primary.Name)  ·  $series Series"
} else {
    "$($primary.Name)"
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

$driverNote = if ($needsUpdate) {
    "Update available: $(if($currentNv){$currentNv}else{'?'}) -> $latestNv. Apply opens NVCleanstall first."
} elseif ($latestNv) {
    "On newest Game Ready ($currentNv)."
} else {
    'Could not reach NVIDIA; Apply still checks and can open NVCleanstall.'
}
$features.Add(@{
    title  = 'Driver (newest Game Ready)'
    detail = $driverNote
    active = (-not $needsUpdate) -and [bool]$latestNv
})

# 3) 3D profile (second pipeline step)
$applied = [bool]($state -and $state.profileFile -and -not $state.pendingAfterDriver)
if ($state -and $state.pendingAfterDriver) { $applied = $false }
$gsyncDetail = if ($state -and $state.gsync) { 'GSync pack' } else { 'No Gsync pack' }
$features.Add(@{
    title  = '3D Base Profile'
    detail = $(if ($applied) { "$($state.series) Series · $gsyncDetail · $($state.profileFile)" } else { 'FPS/latency Base Profile via Profile Inspector (after driver is current).' })
    active = $applied
})

# 4+) App stack
$appOk = Test-NvidiaApp
if ($state -and $state.nvidiaApp) { $appOk = $true }
$features.Add(@{
    title  = 'NVIDIA App'
    detail = 'Conflict cleanup, then fresh App install for display/3D UI.'
    active = $appOk
})

$features.Add(@{
    title  = 'Privacy / telemetry trim'
    detail = 'Disables NvTelemetry tasks/services and quiet auto-download hints where possible.'
    active = [bool]($state -and $state.debloatApplied)
})

$features.Add(@{
    title  = 'Display color / scaling prefs'
    detail = 'NVIDIA color path + Full RGB / high bpc guidance.'
    active = [bool]($state -and $state.displayPrefs)
})

$isApplied = $gpuOk -and $applied -and (-not $needsUpdate)
$statusText = if (-not $gpuOk) { 'No NVIDIA GPU' }
elseif ($needsUpdate) { 'Driver update available' }
elseif ($isApplied) { 'Already optimized' }
else { 'Ready to optimize' }

$detail = if (-not $gpuOk) { 'Needs an NVIDIA GPU and current drivers.' }
elseif ($needsUpdate) { 'Update the driver first (Apply), reboot, then Reapply for 3D profile + App polish.' }
elseif ($isApplied) { 'Driver current and 3D profile applied. Re-apply after big driver upgrades.' }
else { 'Toggle GSync if needed, then Apply.' }

[ordered]@{
    isApplied         = $isApplied
    statusText        = $statusText
    detail            = $detail
    features          = @($features)
    gpuName           = $(if ($primary) { $primary.Name } else { $null })
    series            = $series
    gsync             = [bool]($state -and $state.gsync)
    currentDriver     = $currentNv
    latestDriver      = $latestNv
    needsDriverUpdate = $needsUpdate
} | ConvertTo-Json -Compress -Depth 5
