# OptiHub - detect Steam optimizer status (JSON for WinUI).
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
$features.Add(@{
    title  = 'Steam install'
    detail = $(if ($steamOk) { $steam } else { 'Install Steam, open it once, then return.' })
    active = $steamOk
})

$startupOk = $false
if ($state -and $state.startupDisabled) { $startupOk = $true }
else {
    $startupOk = $true
    try {
        $run = Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -ErrorAction SilentlyContinue
        foreach ($n in @($run.PSObject.Properties.Name)) {
            if ($n -match 'PS') { continue }
            if ([string]$run.$n -match '(?i)steam\.exe') { $startupOk = $false; break }
        }
    } catch { }
}
$features.Add(@{
    title  = 'Quieter Windows startup'
    detail = 'Steam is not forced to launch when Windows starts.'
    active = $startupOk
})

$cefOk = $false
if ($steam -and (Test-Path (Join-Path $steam 'Steam-OptiHub.cmd'))) { $cefOk = $true }
if ($state -and $state.cefLeanLaunch) { $cefOk = $true }
$features.Add(@{
    title  = 'Lean steamwebhelper (CEF)'
    detail = '-cef-disable-gpu launch flags cut Chromium webhelper RAM/GPU.'
    active = $cefOk
})

$dlOk = [bool]($state -and $state.downloadOptimized)
$features.Add(@{
    title  = 'Faster downloads'
    detail = 'Throttle cleared when possible; stuck download/temp staging cleaned.'
    active = $dlOk
})

$snapOk = [bool]($state -and ($state.snappyUi -or $state.highPriority -or $state.cefLeanLaunch))
$features.Add(@{
    title  = 'Snappier client'
    detail = 'HIGH process priority, cache wipe, library/UI performance hints.'
    active = $snapOk
})

$trimOk = $false
if ($steam -and (Test-Path (Join-Path $steam 'OptiHub-SteamWebHelperTrim.ps1'))) { $trimOk = $true }
if ($state -and $state.webHelperTrim) { $trimOk = $true }
$features.Add(@{
    title  = 'Webhelper RAM trim + in-game suspend'
    detail = 'Every 5s (like DiscOpt): EmptyWorkingSet always; suspend steamwebhelper while a Steam game runs (overlay may pause).'
    active = $trimOk
})

$markerOk = [bool]$state
$isApplied = $steamOk -and $markerOk -and $cefOk

$statusText = if (-not $steamOk) { 'Steam not installed' }
elseif ($isApplied) { 'Already optimized' }
else { 'Ready to optimize' }

$detail = if (-not $steamOk) { 'Install Steam, open it once, then run OptiHub.' }
elseif ($isApplied) { 'Performance pack active. Start Steam from your shortcut or Desktop: Steam (OptiHub Lean).' }
else { 'Run for webhelper lean mode, faster downloads, and a snappier Steam client.' }

[ordered]@{
    isApplied  = $isApplied
    statusText = $statusText
    detail     = $detail
    features   = @($features)
} | ConvertTo-Json -Compress -Depth 5
