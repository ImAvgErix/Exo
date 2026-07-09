# OptiHub - detect Steam optimizer status. Prints one JSON object.
$ErrorActionPreference = 'SilentlyContinue'

function Get-SteamInstallPath {
    $candidates = @()
    try {
        $hkcu = Get-ItemProperty -Path 'HKCU:\Software\Valve\Steam' -ErrorAction SilentlyContinue
        if ($hkcu.SteamPath) { $candidates += $hkcu.SteamPath }
    } catch { }
    try {
        $hklm = Get-ItemProperty -Path 'HKLM:\SOFTWARE\WOW6432Node\Valve\Steam' -ErrorAction SilentlyContinue
        if ($hklm.InstallPath) { $candidates += $hklm.InstallPath }
    } catch { }
    $pf86 = [Environment]::GetFolderPath('ProgramFilesX86')
    $candidates += (Join-Path $pf86 'Steam')
    foreach ($c in $candidates) {
        if ($c -and (Test-Path (Join-Path $c 'steam.exe'))) { return $c.TrimEnd('\') }
    }
    return $null
}

$features = New-Object System.Collections.Generic.List[hashtable]
$steam = Get-SteamInstallPath
$statePath = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'OptiHub\steam-optimizer.json'
$state = $null
if (Test-Path $statePath) {
    try { $state = Get-Content $statePath -Raw -Encoding UTF8 | ConvertFrom-Json } catch { }
}

$steamOk = [bool]$steam
$features.Add(@{ title = 'Steam install'; detail = $(if ($steamOk) { $steam } else { 'Install Steam, open it once, then return.' }); active = $steamOk })

$startupOk = $false
if ($state -and $state.startupDisabled) { $startupOk = $true }
else {
    # Heuristic: no Run key pointing at steam.exe
    $startupOk = $true
    try {
        $run = Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -ErrorAction SilentlyContinue
        foreach ($n in @($run.PSObject.Properties.Name)) {
            if ($n -match 'PS') { continue }
            if ([string]$run.$n -match '(?i)steam\.exe') { $startupOk = $false; break }
        }
    } catch { }
}
$features.Add(@{ title = 'Quieter Windows startup'; detail = 'Steam is not forced to launch when Windows starts.'; active = $startupOk })

$cacheOk = [bool]($state -and $state.cacheFreedBytes -ge 0 -and $state.appliedUtc)
$features.Add(@{ title = 'Lean client caches'; detail = 'HTML/log/temp download caches cleaned safely (games files kept).'; active = $cacheOk })

$configOk = [bool]($state -and $state.configTouched)
$features.Add(@{ title = 'Client config tuned'; detail = 'Download throttle / web-GPU hints applied when Steam keys exist.'; active = $configOk })

$markerOk = [bool]$state
$isApplied = $steamOk -and $markerOk -and $startupOk

$statusText = if (-not $steamOk) { 'Steam not installed' }
elseif ($isApplied) { 'Already optimized' }
else { 'Ready to optimize' }

$detail = if (-not $steamOk) { 'Install Steam stable, open it once, then run OptiHub.' }
elseif ($isApplied) { 'These savings are active. Reapply after big Steam updates.' }
else { 'Run to quiet startup, clear safe caches, and apply client performance hints.' }

$result = [ordered]@{
    isApplied  = $isApplied
    statusText = $statusText
    detail     = $detail
    features   = @($features)
}
$result | ConvertTo-Json -Compress -Depth 5
