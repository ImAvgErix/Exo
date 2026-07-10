# OptiHub - detect Steam optimizer status (JSON for WinUI).
$ErrorActionPreference = 'SilentlyContinue'

function Get-SteamInstallPath {
    $candidates = @()
    try {
        $hkcu = Get-ItemProperty -Path 'HKCU:\Software\Valve\Steam' -ErrorAction SilentlyContinue
        if ($hkcu.SteamPath) { $candidates += $hkcu.SteamPath }
        if ($hkcu.SteamExe) {
            $parent = Split-Path -Parent ([string]$hkcu.SteamExe)
            if ($parent) { $candidates += $parent }
        }
    } catch { }
    try {
        $hklm = Get-ItemProperty -Path 'HKLM:\SOFTWARE\WOW6432Node\Valve\Steam' -ErrorAction SilentlyContinue
        if ($hklm.InstallPath) { $candidates += $hklm.InstallPath }
    } catch { }
    try {
        $hklm64 = Get-ItemProperty -Path 'HKLM:\SOFTWARE\Valve\Steam' -ErrorAction SilentlyContinue
        if ($hklm64.InstallPath) { $candidates += $hklm64.InstallPath }
    } catch { }
    $pf86 = [Environment]::GetFolderPath('ProgramFilesX86')
    $pf = [Environment]::GetFolderPath('ProgramFiles')
    if ($pf86) { $candidates += (Join-Path $pf86 'Steam') }
    if ($pf) { $candidates += (Join-Path $pf 'Steam') }
    foreach ($c in $candidates) {
        if ($c -and (Test-Path -LiteralPath (Join-Path $c 'steam.exe'))) { return $c.TrimEnd('\', '/') }
    }
    return $null
}

function Test-SteamStartupQuiet {
    foreach ($key in @(
        'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run',
        'HKLM:\Software\Microsoft\Windows\CurrentVersion\Run',
        'HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run'
    )) {
        if (-not (Test-Path $key)) { continue }
        try {
            $item = Get-Item -Path $key -ErrorAction Stop
            foreach ($name in @($item.GetValueNames())) {
                if ([string]$item.GetValue($name) -match '(?i)steam\.exe' -or $name -match '(?i)^steam') {
                    return $false
                }
            }
        } catch { return $false }
    }
    try {
        return [int](Get-ItemPropertyValue -Path 'HKCU:\Software\Valve\Steam' -Name 'StartupMode' -ErrorAction Stop) -eq 0
    } catch { return $false }
}

function Test-VdfExpectations([string]$Raw, [object[]]$Expectations, [bool]$RequireObserved) {
    $observed = 0
    foreach ($pair in $Expectations) {
        $matches = [regex]::Matches($Raw, '"' + [regex]::Escape([string]$pair.K) + '"\s+"([^"]*)"')
        $observed += $matches.Count
        foreach ($match in $matches) {
            if ($match.Groups[1].Value -ne [string]$pair.V) { return $false }
        }
    }
    return (-not $RequireObserved) -or $observed -gt 0
}

function Test-SteamDownloadConfig([string]$SteamPath) {
    $path = Join-Path $SteamPath 'config\config.vdf'
    if (-not (Test-Path -LiteralPath $path)) { return $false }
    try {
        $raw = Get-Content -LiteralPath $path -Raw -ErrorAction Stop
        return Test-VdfExpectations $raw @(
            @{ K = 'DownloadThrottleKbps'; V = '0' },
            @{ K = 'ThrottleKbps'; V = '0' },
            @{ K = 'RateLimitBps'; V = '0' },
            @{ K = 'MaxSimDownloads'; V = '8' },
            @{ K = 'AutoUpdateWindowEnabled'; V = '0' }
        ) $false
    } catch { return $false }
}

function Test-SteamClientTweaks([string]$SteamPath) {
    $userdata = Join-Path $SteamPath 'userdata'
    if (-not (Test-Path -LiteralPath $userdata)) { return $false }
    try {
        $files = @(Get-ChildItem -LiteralPath $userdata -Directory -ErrorAction Stop | ForEach-Object {
            $path = Join-Path $_.FullName 'config\localconfig.vdf'
            if (Test-Path -LiteralPath $path) { Get-Item -LiteralPath $path -ErrorAction Stop }
        })
        if ($files.Count -eq 0) { return $false }
        $expectations = @(
            @{ K = 'H264HWAccel'; V = '0' },
            @{ K = 'GPUAccelWebViews'; V = '0' },
            @{ K = 'GPUAccelWebViews2'; V = '0' },
            @{ K = 'GPUAccelWebViewsD3D11'; V = '0' },
            @{ K = 'SmoothScrollWebViews'; V = '0' },
            @{ K = 'LibraryDisableCommunityContent'; V = '1' },
            @{ K = 'InGameOverlayScreenshotNotification'; V = '0' },
            @{ K = 'Controller_EnableChrome'; V = '0' },
            @{ K = 'AllowDownloadsDuringGameplay'; V = '0' }
        )
        $observedAnywhere = $false
        foreach ($file in $files) {
            $raw = Get-Content -LiteralPath $file.FullName -Raw -ErrorAction Stop
            if (Test-VdfExpectations $raw $expectations $true) {
                $observedAnywhere = $true
            } elseif ($expectations | Where-Object { $raw -match ('"' + [regex]::Escape([string]$_.K) + '"') }) {
                return $false
            }
        }
        return $observedAnywhere
    } catch { return $false }
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

$startupOk = Test-SteamStartupQuiet
$features.Add(@{
    title  = 'Quieter Windows startup'
    detail = 'Steam is not forced to launch when Windows starts.'
    active = $startupOk
})

$cefOk = $false
$launcher = if ($steam) { Join-Path $steam 'Steam-OptiHub.cmd' } else { $null }
if ($launcher -and (Test-Path -LiteralPath $launcher)) {
    try {
        $launcherText = Get-Content -LiteralPath $launcher -Raw -ErrorAction Stop
        $cefOk = $launcherText -match '(?i)steam\.exe' -and
            $launcherText -match '-cef-disable-gpu' -and
            $launcherText -match '(?i)start\s+""\s+/HIGH'
    } catch { }
}
$features.Add(@{
    title  = 'Quiet CEF launcher (default)'
    detail = 'Start Menu / taskbar Steam uses Steam-OptiHub.cmd (disable-gpu, nofriendsui, nointro, etc.). No desktop icons.'
    active = $cefOk
})

$dlOk = [bool]($state -and $state.configVerified -and $state.downloadOptimized) -and
    (Test-SteamDownloadConfig $steam)
$features.Add(@{
    title  = 'Download tuning + deep client cache clean'
    detail = 'Throttle is cleared and disposable/orphaned caches are purged. Active downloads and installed-game shader caches stay intact.'
    active = $dlOk
})

$snapOk = [bool]($state -and $state.clientTweaksVerified -and $state.snappyUi -and $state.overlayTweaks) -and
    (Test-SteamClientTweaks $steam)
$features.Add(@{
    title  = 'Overlay / library client tweaks'
    detail = 'GPU web views off, quieter overlay noise, no downloads while playing when keys exist.'
    active = $snapOk
})

$trimOk = $false
$helper = if ($steam) { Join-Path $steam 'OptiHub-SteamWebHelperTrim.ps1' } else { $null }
if ($helper -and (Test-Path -LiteralPath $helper)) {
    try {
        $helperText = Get-Content -LiteralPath $helper -Raw -ErrorAction Stop
        $trimOk = $helperText -match 'OptiHub\.SteamWebHelper' -and
            $helperText -match 'EmptyWorkingSet' -and
            $helperText -match 'Start-Sleep -Seconds 5' -and
            $helperText -match 'ProcessPriorityClass\]::High' -and
            $helperText -match 'ProcessPriorityClass\]::BelowNormal'
    } catch { }
}
$features.Add(@{
    title  = 'Aggressive 5s RAM trim + priority control'
    detail = 'Reclaims steamwebhelper working sets every 5s, runs the client High when idle, then yields CPU while gaming. No suspend.'
    active = $trimOk
})

$markerOk = [bool]($state -and
    [string]$state.version -eq '1.5.0' -and
    [string]$state.applyStatus -eq 'applied' -and
    $state.applied -eq $true -and
    $state.quick -eq $false -and
    $state.cacheCleanupCompleted -eq $true -and
    $state.shaderInventoryVerified -eq $true -and
    $state.installedShaderCachesPreserved -eq $true)
$isApplied = $steamOk -and $markerOk -and $startupOk -and $cefOk -and $trimOk -and $dlOk -and $snapOk

$statusText = if (-not $steamOk) { 'Steam not installed' }
elseif ($isApplied) { 'Already optimized' }
else { 'Ready to optimize' }

$detail = if (-not $steamOk) { 'Install Steam, open it once, then run OptiHub.' }
elseif ($isApplied) { 'Performance pack active. Open Steam from Start Menu or taskbar (no desktop shortcuts).' }
else { 'Run for the quiet CEF launcher, aggressive RAM reclamation, priority control, and deep client cleanup.' }

[ordered]@{
    isApplied  = $isApplied
    statusText = $statusText
    detail     = $detail
    features   = @($features)
} | ConvertTo-Json -Compress -Depth 5
