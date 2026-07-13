# OptiHub - detect Steam optimizer status (JSON for WinUI).
# Checklist mirrors Discord parity: quiet launch, RAM kernel, complete debloat,
# Windows suppression, Start Menu path, verified record.
# Classifiers: SteamDetectCore.ps1 (pure) — keep aligned with SteamPeakLogic.cs
$ErrorActionPreference = 'SilentlyContinue'

$core = Join-Path $PSScriptRoot 'SteamDetectCore.ps1'
if (-not (Test-Path -LiteralPath $core)) { throw "Missing SteamDetectCore.ps1 beside detect script" }
. $core

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

function Add-Feature([string]$Title, [string]$Detail, [bool]$Active) {
    $script:features.Add(@{
        title  = $Title
        detail = $Detail
        active = $Active
    })
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

function Test-SteamToastsOff {
    $base = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Notifications\Settings'
    $ids = @('Steam', 'Valve.Steam', 'Valve.Steam.Client', 'com.valvesoftware.Steam', 'steam.exe')
    $map = @{}
    foreach ($id in $ids) {
        $path = Join-Path $base $id
        if (-not (Test-Path -LiteralPath $path)) { $map[$id] = $null; continue }
        try {
            $entry = Get-ItemProperty -Path $path -ErrorAction Stop
            $prop = $entry.PSObject.Properties['Enabled']
            if (-not $prop) { $map[$id] = $null }
            else { $map[$id] = [int]$prop.Value }
        } catch { $map[$id] = 1 }
    }
    return (Test-SteamToastsOffFromMap -Map $map)
}

function Test-SteamTrayQuiet([string]$SteamPath) {
    $notifyKey = 'HKCU:\Control Panel\NotifyIconSettings'
    if (-not (Test-Path $notifyKey)) { return $true }
    $prefix = $null
    try { $prefix = [IO.Path]::GetFullPath($SteamPath).TrimEnd('\') + '\' } catch { }
    foreach ($key in @(Get-ChildItem -Path $notifyKey -ErrorAction SilentlyContinue)) {
        $item = Get-Item -Path $key.PSPath -ErrorAction SilentlyContinue
        if (-not $item) { continue }
        $exe = [string]$item.GetValue('ExecutablePath')
        if (-not $exe) { continue }
        $isSteam = ($exe -match '(?i)[\\/]steam\.exe$' -or $exe -match '(?i)\\Steam\\')
        if (-not $isSteam -and $prefix) {
            try {
                $full = [IO.Path]::GetFullPath($exe)
                if ($full.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { $isSteam = $true }
            } catch { }
        }
        if (-not $isSteam) { continue }
        if ($item.GetValueNames() -notcontains 'IsPromoted' -or [int]$item.GetValue('IsPromoted') -ne 0) {
            return $false
        }
    }
    return $true
}

function Test-SteamScheduledTasksQuiet {
    try {
        foreach ($task in @(Get-ScheduledTask -ErrorAction SilentlyContinue)) {
            if ($task.TaskName -notmatch '(?i)\bSteam\b' -and $task.TaskPath -notmatch '(?i)\\Steam\\') { continue }
            if ($task.TaskName -match '(?i)Steam(VR|Link|OS|Deck)' -or $task.TaskPath -match '(?i)Steam(VR|Link|OS|Deck)') { continue }
            if ([bool]$task.Settings.Enabled) { return $false }
        }
        return $true
    } catch { return $true }
}

function Test-SteamWindowsQuiet([string]$SteamPath) {
    return (Test-SteamStartupQuiet) -and
        (Test-SteamToastsOff) -and
        (Test-SteamTrayQuiet $SteamPath) -and
        (Test-SteamScheduledTasksQuiet)
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
        $anyExpectationKeyPresent = $false
        foreach ($file in $files) {
            $raw = Get-Content -LiteralPath $file.FullName -Raw -ErrorAction Stop
            if (Test-VdfExpectations $raw $expectations $true) {
                $observedAnywhere = $true
            } elseif ($expectations | Where-Object { $raw -match ('"' + [regex]::Escape([string]$_.K) + '"') }) {
                # Key exists but value is wrong - fail closed.
                $anyExpectationKeyPresent = $true
                return $false
            }
            if ($expectations | Where-Object { $raw -match ('"' + [regex]::Escape([string]$_.K) + '"') }) {
                $anyExpectationKeyPresent = $true
            }
        }
        # Soft-pass: modern Steam often has none of these keys; CEF launcher still optimizes UI.
        if (-not $anyExpectationKeyPresent) { return $true }
        return $observedAnywhere
    } catch { return $false }
}

function Test-SteamCompleteClientDebloat([string]$SteamPath) {
    if (-not $SteamPath -or -not (Test-Path -LiteralPath $SteamPath)) { return $false }

    foreach ($f in @(
        (Join-Path $SteamPath 'Steam-OptiHub-Aggressive.cmd'),
        (Join-Path $SteamPath 'Steam-OptiHub-Lean.cmd'),
        (Join-Path $SteamPath 'Steam-OptiHub-Legacy.cmd')
    )) {
        if (Test-Path -LiteralPath $f) { return $false }
    }

    foreach ($desktop in @(
        [Environment]::GetFolderPath('Desktop'),
        [Environment]::GetFolderPath('CommonDesktopDirectory')
    )) {
        if (-not $desktop -or -not (Test-Path -LiteralPath $desktop)) { continue }
        $hits = @(Get-ChildItem -LiteralPath $desktop -Filter 'Steam*.lnk' -Force -ErrorAction SilentlyContinue)
        if ($hits.Count -gt 0) { return $false }
    }

    foreach ($d in @(
        (Join-Path $env:LOCALAPPDATA 'Steam\htmlcache\Crashpad'),
        (Join-Path $env:LOCALAPPDATA 'Steam\Crashpad')
    )) {
        if (Test-Path -LiteralPath $d) {
            $kids = @(Get-ChildItem -LiteralPath $d -Force -ErrorAction SilentlyContinue)
            if ($kids.Count -gt 0) { return $false }
        }
    }

    if (-not (Test-Path -LiteralPath (Join-Path $SteamPath 'Steam-OptiHub.cmd'))) { return $false }
    return $true
}

function Test-SteamRuntimeIntegrity([string]$SteamPath) {
    if (-not $SteamPath) { return $false }
    if (-not (Test-Path -LiteralPath (Join-Path $SteamPath 'steam.exe'))) { return $false }
    if (-not (Test-Path -LiteralPath (Join-Path $SteamPath 'bin'))) { return $false }
    # Modern Steam ships steamwebhelper under bin\cef\cef.win*\steamwebhelper.exe
    foreach ($h in @(
        (Join-Path $SteamPath 'steamwebhelper.exe'),
        (Join-Path $SteamPath 'bin\cef\cef.win64\steamwebhelper.exe'),
        (Join-Path $SteamPath 'bin\cef\cef.win7x64\steamwebhelper.exe'),
        (Join-Path $SteamPath 'bin\cef\cef.win7\steamwebhelper.exe')
    )) {
        if (Test-Path -LiteralPath $h) { return $true }
    }
    try {
        $found = Get-ChildItem -LiteralPath (Join-Path $SteamPath 'bin') -Filter 'steamwebhelper.exe' -Recurse -ErrorAction Stop |
            Select-Object -First 1
        return [bool]$found
    } catch { return $false }
}

function Test-SteamStartMenuLaunchPath([string]$SteamPath) {
    if (-not $SteamPath) { return $false }
    $cmdPath = Join-Path $SteamPath 'Steam-OptiHub.cmd'
    if (-not (Test-Path -LiteralPath $cmdPath)) { return $false }

    $candidates = @(
        (Join-Path ([Environment]::GetFolderPath('Programs')) 'Steam\Steam.lnk'),
        (Join-Path ([Environment]::GetFolderPath('CommonPrograms')) 'Steam\Steam.lnk'),
        (Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Steam\Steam.lnk'),
        (Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\Steam\Steam.lnk')
    )

    try {
        $wsh = New-Object -ComObject WScript.Shell
        foreach ($lnk in $candidates) {
            if (-not (Test-Path -LiteralPath $lnk)) { continue }
            try {
                $sc = $wsh.CreateShortcut($lnk)
                $target = [string]$sc.TargetPath
                if ($target -and (
                        $target -ieq $cmdPath -or
                        $target -match '(?i)Steam-OptiHub\.cmd$'
                    )) {
                    return $true
                }
            } catch { }
        }
    } catch { }
    return $false
}

$features = New-Object System.Collections.Generic.List[hashtable]
$steam = Get-SteamInstallPath
$statePath = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'OptiHub\steam-optimizer.json'
$state = $null
if (Test-Path $statePath) {
    try { $state = Get-Content $statePath -Raw -Encoding UTF8 | ConvertFrom-Json } catch { }
}

$steamOk = [bool]$steam
if (-not $steamOk) {
    $statusText = 'Steam not installed'
    $detail = 'Install Steam, open it once, then return.'
    Add-Feature 'Steam install' 'Required before optimizations can apply.' $false
} else {
    Add-Feature 'Steam install' 'Client found and ready.' $true

    # Quiet CEF launcher (SteamPeakLogic / SteamDetectCore)
    $cefOk = $false
    $launcher = Join-Path $steam 'Steam-OptiHub.cmd'
    if (Test-Path -LiteralPath $launcher) {
        try {
            $launcherText = Get-Content -LiteralPath $launcher -Raw -ErrorAction Stop
            $cefOk = Test-SteamCefLauncherText -Text $launcherText
        } catch { }
    }
    Add-Feature 'Quiet CEF launcher' 'Fast quiet CEF flags + High priority Steam start (Steam launches before the trim helper).' $cefOk

    # WebHelper trim + priority (2–15s reclaim interval accepted)
    $trimOk = $false
    $helper = Join-Path $steam 'OptiHub-SteamWebHelperTrim.ps1'
    if (Test-Path -LiteralPath $helper) {
        try {
            $helperText = Get-Content -LiteralPath $helper -Raw -ErrorAction Stop
            $trimOk = Test-SteamTrimHelperText -Text $helperText
        } catch { }
    }
    Add-Feature 'RAM trim + priority' 'Webhelper reclaim loop + priority yield while gaming (2-15s interval).' $trimOk

    $debloatOk = Test-SteamCompleteClientDebloat $steam
    $dlOk = [bool]($state -and $state.configVerified -and $state.downloadOptimized) -and
        (Test-SteamDownloadConfig $steam)
    $debloatCombined = $debloatOk -and $dlOk
    Add-Feature 'Complete client debloat' 'Caches, leftovers, crashpads cleaned; games preserved.' $debloatCombined

    $snapOk = [bool]($state -and $state.clientTweaksVerified -and $state.snappyUi -and $state.overlayTweaks) -and
        (Test-SteamClientTweaks $steam)
    Add-Feature 'Library / overlay tweaks' 'Quieter overlay and lighter library web views.' $snapOk

    $windowsQuietOk = Test-SteamWindowsQuiet $steam
    Add-Feature 'Windows quiet shell' 'No autostart; toasts off; tray not promoted.' $windowsQuietOk

    $launchOk = Test-SteamStartMenuLaunchPath $steam
    Add-Feature 'Start Menu launch path' 'Shortcuts use OptiHub launcher; no desktop icons.' $launchOk

    $runtimeOk = Test-SteamRuntimeIntegrity $steam
    # Trust apply flags - do NOT pin exact kit version strings (1.7.3+ was falsely "incomplete").
    $markerOk = Test-SteamApplyRecord -State $state
    # Durable quiet re-enforce helper must exist after modern applies.
    if ($markerOk -and $helper -and (Test-Path -LiteralPath $helper)) {
        try {
            $helperText = Get-Content -LiteralPath $helper -Raw -ErrorAction Stop
            if (-not (Test-SteamTrimHelperText -Text $helperText) -and
                $helperText -notmatch 'Reinstate-SteamQuiet') {
                $markerOk = $false
            }
        } catch { $markerOk = $false }
    } elseif ($markerOk -and -not (Test-Path -LiteralPath $helper)) {
        $markerOk = $false
    }
    Add-Feature 'Verified apply' 'Full apply recorded with durable quiet + runtime intact.' ($markerOk -and $runtimeOk)

    $isApplied = $steamOk -and $markerOk -and $cefOk -and $trimOk -and $debloatOk -and
        $runtimeOk -and $dlOk -and $snapOk -and $windowsQuietOk -and $launchOk

    $statusText = if ($isApplied) { 'Already optimized' }
    elseif (-not $cefOk -or -not $trimOk -or -not $launchOk) { 'Launcher needs restore' }
    elseif (-not $windowsQuietOk) { 'Windows quiet incomplete' }
    else { 'Ready to optimize' }
    $detail = if ($isApplied) {
        'Quiet CEF, debloat, Windows quiet, 5s RAM trim, and autostart re-enforce are active.'
    } elseif (-not $cefOk -or -not $trimOk) {
        'Steam launcher or trim helper is missing. Run to restore the OptiHub launch path.'
    } else {
        'Some pieces are missing. Run to finish the checklist below.'
    }
}

if (-not $steamOk) {
    $isApplied = $false
}

[ordered]@{
    isApplied  = [bool]$isApplied
    statusText = $statusText
    detail     = $detail
    features   = @($features)
} | ConvertTo-Json -Compress -Depth 5
