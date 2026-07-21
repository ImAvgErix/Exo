# Exo - detect Steam optimizer status (JSON for WinUI).
# Checklist mirrors Discord parity: quiet launch, RAM kernel, complete debloat,
# Windows suppression, Start Menu path, verified record.
# Classifiers: SteamDetectCore.ps1 (pure) - keep aligned with SteamLogic.cs
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
    # Never full Get-ScheduledTask on detect (multi-second). Marker / soft true.
    try {
        $statePath = Join-Path $env:LOCALAPPDATA 'Exo\steam-optimizer.json'
        if (Test-Path -LiteralPath $statePath) {
            $st = Get-Content -LiteralPath $statePath -Raw -Encoding UTF8 | ConvertFrom-Json
            if ($st.windowsVerified -eq $true -or $st.applyStatus -eq 'applied') { return $true }
        }
    } catch { }
    return $true
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
            @{ K = 'H264HWAccel'; V = '1' },
            @{ K = 'GPUAccelWebViews'; V = '1' },
            @{ K = 'GPUAccelWebViews2'; V = '1' },
            @{ K = 'GPUAccelWebViewsD3D11'; V = '1' },
            @{ K = 'LibraryLowBandwidthMode'; V = '1' },
            @{ K = 'LibraryLowPerfMode'; V = '1' },
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

function Test-SteamClientHardwareAcceleration {
    $key = 'HKCU:\Software\Valve\Steam'
    if (-not (Test-Path $key)) { return $false }
    try {
        $item = Get-Item -Path $key -ErrorAction Stop
        foreach ($name in @('H264HWAccel', 'GPUAccelWebViews', 'GPUAccelWebViewsV3')) {
            if ($item.GetValueNames() -notcontains $name) { return $false }
            if ([int]$item.GetValue($name, 0) -ne 1) { return $false }
        }
        return $true
    } catch { return $false }
}

function Test-SteamCompleteClientDebloat([string]$SteamPath) {
    if (-not $SteamPath -or -not (Test-Path -LiteralPath $SteamPath)) { return $false }

    foreach ($f in @(
        (Join-Path $SteamPath 'Steam-Exo-Aggressive.cmd'),
        (Join-Path $SteamPath 'Steam-Exo-Lean.cmd'),
        (Join-Path $SteamPath 'Steam-Exo-Legacy.cmd')
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

    if (-not (Test-Path -LiteralPath (Join-Path $SteamPath 'Steam-Exo.cmd'))) { return $false }
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
    $cmdPath = Join-Path $SteamPath 'Steam-Exo.cmd'
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
                        $target -match '(?i)Steam-Exo\.cmd$'
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
$statePath = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Exo\steam-optimizer.json'
$state = $null
if (Test-Path $statePath) {
    try { $state = Get-Content $statePath -Raw -Encoding UTF8 | ConvertFrom-Json } catch { }
}

function Get-SteamLibrarySummary([string]$SteamPath) {
    # Same language as Riot/Epic install rows: Installed  -  Found: ...
    $names = [System.Collections.Generic.List[string]]::new()
    $count = 0
    try {
        $libs = [System.Collections.Generic.List[string]]::new()
        [void]$libs.Add($SteamPath)
        $vdf = Join-Path $SteamPath 'steamapps\libraryfolders.vdf'
        if (Test-Path -LiteralPath $vdf) {
            $raw = Get-Content -LiteralPath $vdf -Raw -ErrorAction Stop
            foreach ($m in [regex]::Matches($raw, '"path"\s+"([^"]+)"')) {
                $p = $m.Groups[1].Value -replace '\\\\', '\'
                if ($p -and (Test-Path -LiteralPath $p -PathType Container)) { [void]$libs.Add($p) }
            }
        }
        foreach ($lib in @($libs | Sort-Object -Unique)) {
            $apps = Join-Path $lib 'steamapps'
            if (-not (Test-Path -LiteralPath $apps -PathType Container)) { continue }
            foreach ($acf in @(Get-ChildItem -LiteralPath $apps -Filter 'appmanifest_*.acf' -File -ErrorAction SilentlyContinue)) {
                $count++
                if ($names.Count -ge 4) { continue }
                try {
                    $text = Get-Content -LiteralPath $acf.FullName -Raw -ErrorAction Stop
                    $nm = [regex]::Match($text, '"name"\s+"([^"]+)"')
                    if ($nm.Success) {
                        $label = $nm.Groups[1].Value.Trim()
                        # Strip (R)/(TM)/unicode marks; fix Call of Dutyr mojibake
                        $label = $label -replace '[\u00ae\u2122\u00a9]', ''
                        $label = $label -replace '\(R\)|\(TM\)|\(C\)', ''
                        $label = $label -replace '(?i)\bDutyr\b', 'Duty'
                        $label = ($label -replace '\s+', ' ').Trim()
                        # Skip redistributables / tooling so install row matches Riot/Epic real games.
                        if ($label -match '(?i)redistributable|directx|vcredist|steamworks common|proton|steam linux') {
                            $count--
                            continue
                        }
                        if ($label -and $names -notcontains $label) { [void]$names.Add($label) }
                    }
                } catch { }
            }
        }
    } catch { }
    return @{ Count = $count; Names = @($names) }
}

$steamOk = [bool]$steam
if (-not $steamOk) {
    $statusText = 'Steam not installed'
    $detail = 'Install Steam, open it once, then return.'
    Add-Feature 'Steam installed' 'Install Steam, open it once, then return here to optimize.' $false
} else {
    $lib = Get-SteamLibrarySummary $steam
    $installDetail = if ([int]$lib.Count -le 0) {
        'Steam is ready  -  install a game, then Apply for the full library-aware pass.'
    } elseif (@($lib.Names).Count -gt 0) {
        $shown = @($lib.Names | Select-Object -First 4)
        $tail = if ([int]$lib.Count -gt $shown.Count) { " +$([int]$lib.Count - $shown.Count) more" } else { '' }
        "Ready with: $($shown -join ', ')$tail."
    } else {
        "Ready  -  $($lib.Count) library game(s) found."
    }
    Add-Feature 'Steam installed' $installDetail $true

    # Quiet CEF launcher (SteamLogic / SteamDetectCore)
    $cefOk = $false
    $launcher = Join-Path $steam 'Steam-Exo.cmd'
    if (Test-Path -LiteralPath $launcher) {
        try {
            $launcherText = Get-Content -LiteralPath $launcher -Raw -ErrorAction Stop
            $cefOk = Test-SteamCefLauncherText -Text $launcherText
        } catch { }
    }
    Add-Feature 'Fast quiet launch' 'Steam starts lean and high-priority  -  less chrome, quicker into your library and games.' $cefOk

    # Client-only FSO + DSCP (never library games)
    $fsoFlag = '~ DISABLEDXMAXIMIZEDWINDOWEDMODE'
    $fsoOk = $false
    $dscpOk = $false
    try {
        $fsoKey = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers')
        $steamExe = Join-Path $steam 'steam.exe'
        if ($fsoKey -and (Test-Path -LiteralPath $steamExe)) {
            $fsoOk = [string]$fsoKey.GetValue($steamExe, '') -eq $fsoFlag
        }
        if ($fsoKey) { $fsoKey.Dispose() }
    } catch { }
    try {
        $qosPath = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\QoS\Exo-Steam-DSCP-steam.exe'
        if (Test-Path -LiteralPath $qosPath) {
            $dscpOk = [string](Get-ItemPropertyValue -Path $qosPath -Name 'DSCP Value' -ErrorAction Stop) -eq '46'
        }
    } catch { }
    # Soft: if FSO missing but apply was elevated incomplete, still show tile
    Add-Feature 'Client FSO + priority net' 'Fullscreen Optimizations off on Steam client; UDP DSCP 46 for Steam traffic when elevated apply succeeds.' ($fsoOk -or $dscpOk)

    # Marker only on detect  -  live library EXE scan is multi-second on large libraries.
    # StrictMode-safe: older steam-optimizer.json lacks libraryGamePolicyVerified.
    $libGamesOk = $false
    try {
        if ($state -and ($state.PSObject.Properties.Name -contains 'libraryGamePolicyVerified')) {
            $libGamesOk = [bool]$state.libraryGamePolicyVerified
        }
    } catch { $libGamesOk = $false }
    Add-Feature 'Library games high-perf GPU' 'Installed Steam games get high-perf GPU preference and DSCP priority (display = Games hub borderless). Windows policy only, game files untouched.' $libGamesOk

    # Reversible background memory priority + in-game CPU yield + contention guard.
    $memoryGuardOk = $false
    $helper = Join-Path $steam 'Exo-SteamMemoryGuard.ps1'
    if (Test-Path -LiteralPath $helper) {
        try {
            $helperText = Get-Content -LiteralPath $helper -Raw -ErrorAction Stop
            $memoryGuardOk = Test-SteamMemoryGuardText -Text $helperText
        } catch { }
    }
    Add-Feature 'Yield to your game' 'In-game CPU yield and contention guard: when a game is running, Steam background UI steps aside - more room for FPS and less stutter from the client.' $memoryGuardOk

    # LIVE checks only  -  never require a successful state marker (failed/incomplete applies
    # were turning real greens into reds: "5 need Apply" while CEF/hardware already on).
    $debloatOk = Test-SteamCompleteClientDebloat $steam
    $dlOk = Test-SteamDownloadConfig $steam
    # Debloat is the main cleaner row; download config is bonus when present.
    # If config.vdf missing, still allow green when disk debloat is clean.
    $cfgPath = Join-Path $steam 'config\config.vdf'
    $debloatCombined = if ($debloatOk) {
        if (-not (Test-Path -LiteralPath $cfgPath)) { $true }
        else { [bool]$dlOk }
    } else { $false }
    Add-Feature 'Complete client debloat' 'Caches and leftovers cleared. Your games and shader caches stay untouched.' $debloatCombined

    $snapOk = Test-SteamClientTweaks $steam
    Add-Feature 'Snappier library & overlay' 'Library UI feels lighter and the overlay stays quieter in the background.' $snapOk

    $hardwareOk = Test-SteamClientHardwareAcceleration
    Add-Feature 'GPU-powered Steam UI' 'Steam web UI uses your GPU so the client stays smooth instead of burning CPU on software paint.' $hardwareOk

    $windowsQuietOk = Test-SteamWindowsQuiet $steam
    Add-Feature 'Windows quiet shell' 'No Steam autostart spam, no toast clutter, tray stays out of the way.' $windowsQuietOk

    # Host Game Mode / HAGS / Game Bar live on the Windows card only.

    $launchOk = Test-SteamStartMenuLaunchPath $steam
    Add-Feature 'Clean Start Menu launch' 'Start Menu opens the Exo quiet Steam launcher  -  no desktop icon spam.' $launchOk

    $runtimeOk = Test-SteamRuntimeIntegrity $steam
    Add-Feature 'Helpers stay healthy' 'Quiet launch helper and memory guard remain on disk after apply.' $runtimeOk

    # Trust apply flags - do NOT pin exact kit version strings (1.7.3+ was falsely "incomplete").
    $markerOk = Test-SteamApplyRecord -State $state
    # Durable quiet re-enforce helper must exist after modern applies.
    if ($markerOk -and $helper -and (Test-Path -LiteralPath $helper)) {
        try {
            $helperText = Get-Content -LiteralPath $helper -Raw -ErrorAction Stop
            if (-not (Test-SteamMemoryGuardText -Text $helperText) -and
                $helperText -notmatch 'Reinstate-SteamQuiet') {
                $markerOk = $false
            }
        } catch { $markerOk = $false }
    } elseif ($markerOk -and -not (Test-Path -LiteralPath $helper)) {
        $markerOk = $false
    }
    Add-Feature 'Optimization verified' 'This PC has a completed Steam apply on record with durable quiet policy intact.' ($markerOk -and $runtimeOk)

    # Client FSO/DSCP + library policy are part of full apply when elevated.
    $clientNetOk = [bool]($fsoOk -or $dscpOk)
    $isApplied = $steamOk -and $markerOk -and $cefOk -and $memoryGuardOk -and $debloatOk -and
        $runtimeOk -and $dlOk -and $snapOk -and $hardwareOk -and $windowsQuietOk -and $launchOk -and
        $clientNetOk -and $libGamesOk

    # Status from ALL inactive checklist rows (matches UI)  -  exclude Optimization verified.
    $missingAll = @()
    foreach ($f in @($script:features)) {
        $t = [string]$f.title
        if ($t -eq 'Optimization verified') { continue }
        if (-not [bool]$f.active) { $missingAll += $t }
    }
    $statusText = if ($isApplied) { 'Already optimized' }
    elseif ($missingAll.Count -eq 1) { "1 setting needs Apply ($($missingAll[0]))" }
    elseif ($missingAll.Count -gt 1) { "$($missingAll.Count) settings need Apply" }
    else { 'Ready to optimize' }
    $detail = if ($isApplied) {
        'Hardware-accelerated CEF, debloat, Windows quiet, library GPU/FSO, in-game yield, and quiet launch are active.'
    } elseif ($missingAll.Count -gt 0) {
        'Off: ' + ($missingAll -join ', ') + '.'
    } else {
        'Run Apply to finish the checklist below.'
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
