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
    title  = 'Quiet CEF launcher (default)'
    detail = 'Start Menu / taskbar Steam uses Steam-OptiHub.cmd (disable-gpu, nofriendsui, nointro, etc.). No desktop icons.'
    active = $cefOk
})

$dlOk = [bool]($state -and $state.downloadOptimized)
$features.Add(@{
    title  = 'Faster downloads + shader clean'
    detail = 'Throttle cleared when possible; staging + shader pre-cache cleaned (rebuilds next launch).'
    active = $dlOk
})

$snapOk = [bool]($state -and ($state.snappyUi -or $state.highPriority -or $state.overlayTweaks))
$features.Add(@{
    title  = 'Overlay / library client tweaks'
    detail = 'GPU web views off, quieter overlay noise, no downloads while playing when keys exist.'
    active = $snapOk
})

$trimOk = $false
if ($steam -and (Test-Path (Join-Path $steam 'OptiHub-SteamWebHelperTrim.ps1'))) { $trimOk = $true }
if ($state -and $state.webHelperTrim) { $trimOk = $true }
$features.Add(@{
    title  = '5s webhelper trim + in-game priority yield'
    detail = 'EmptyWorkingSet every 5s (idle + in-game). BELOW_NORMAL steam/webhelper while gaming. No suspend.'
    active = $trimOk
})

$markerOk = [bool]$state
$isApplied = $steamOk -and $markerOk -and $cefOk

$statusText = if (-not $steamOk) { 'Steam not installed' }
elseif ($isApplied) { 'Already optimized' }
else { 'Ready to optimize' }

$detail = if (-not $steamOk) { 'Install Steam, open it once, then run OptiHub.' }
elseif ($isApplied) { 'Performance pack active. Open Steam from Start Menu or taskbar (no desktop shortcuts).' }
else { 'Run for quiet CEF launcher, 5s trim, priority yield, shader clean, downloads.' }

[ordered]@{
    isApplied  = $isApplied
    statusText = $statusText
    detail     = $detail
    features   = @($features)
} | ConvertTo-Json -Compress -Depth 5
