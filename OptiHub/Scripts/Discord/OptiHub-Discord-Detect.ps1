# OptiHub - detect whether Discord Optimizer is already applied.
# Prints a single JSON object to stdout for the WinUI host.
# Classifiers: DiscordDetectCore.ps1 (pure) — keep aligned with DiscordPeakLogic.cs

$ErrorActionPreference = 'SilentlyContinue'

$core = Join-Path $PSScriptRoot 'DiscordDetectCore.ps1'
if (-not (Test-Path -LiteralPath $core)) { throw "Missing DiscordDetectCore.ps1 beside detect script" }
. $core

$local = [Environment]::GetFolderPath('LocalApplicationData')
$appData = [Environment]::GetFolderPath('ApplicationData')
$discordRoot = Join-Path $local 'Discord'
$equicord = Join-Path $appData 'Equicord'

$features = New-Object System.Collections.Generic.List[hashtable]
$isApplied = $false
$statusText = 'Ready to optimize'
$detail = 'Run the optimizer to cut Discord memory use, speed startup, and quiet background noise.'
$statePath = Join-Path $local 'OptiHub\discord-optimizer.json'
$state = $null
if (Test-Path -LiteralPath $statePath) {
    try { $state = Get-Content -LiteralPath $statePath -Raw -Encoding UTF8 -ErrorAction Stop | ConvertFrom-Json }
    catch { $state = $null }
}

function Add-Feature([string]$Title, [string]$Detail, [bool]$Active) {
    $script:features.Add(@{
        title  = $Title
        detail = $Detail
        active = $Active
    })
}

function Test-StableDiscordWindowsQuiet([string]$Root) {
    # Policy (matches Apply-WindowsTweaks): no autostart/tasks; tray hidden when entries exist.
    try {
        $runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
        if (Test-Path $runKey) {
            $run = Get-Item -Path $runKey -ErrorAction Stop
            foreach ($name in @($run.GetValueNames())) {
                if (Test-DiscOptStablePathText ([string]$run.GetValue($name)) $Root) { return $false }
            }
        }

        foreach ($task in @(Get-ScheduledTask -ErrorAction SilentlyContinue)) {
            if ($task.TaskName -notmatch '(?i)Discord' -and $task.TaskPath -notmatch '(?i)Discord') { continue }
            $stable = $false
            foreach ($action in @($task.Actions)) {
                if ((Test-DiscOptStablePathText ([string]$action.Execute) $Root) -or
                    (Test-DiscOptStablePathText ([string]$action.Arguments) $Root) -or
                    (Test-DiscOptStablePathText ([string]$action.WorkingDirectory) $Root)) {
                    $stable = $true
                    break
                }
            }
            if (-not $stable -and ($task.TaskName -match '(?i)Discord' -or $task.TaskPath -match '(?i)Discord')) {
                $stable = $true
            }
            if ($stable -and [bool]$task.Settings.Enabled) { return $false }
        }

        $trayRoot = 'HKCU:\Control Panel\NotifyIconSettings'
        if (Test-Path $trayRoot) {
            foreach ($key in @(Get-ChildItem -Path $trayRoot -ErrorAction SilentlyContinue)) {
                $item = Get-Item -Path $key.PSPath -ErrorAction SilentlyContinue
                if (-not $item) { continue }
                $exe = [string]$item.GetValue('ExecutablePath')
                if (-not $exe) { continue }
                if (-not ((Test-DiscOptStablePathText $exe $Root) -or ($exe -match '(?i)Discord'))) { continue }
                if ($item.GetValueNames() -notcontains 'IsPromoted' -or [int]$item.GetValue('IsPromoted') -ne 0) {
                    return $false
                }
            }
        }
        return $true
    } catch { return $false }
}

function Test-DiscordToastsOff {
    # Product policy: Windows toast banners OFF for Discord (quiet OS shell).
    $base = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Notifications\Settings'
    $ids = @('Discord', 'Discord.Desktop', 'DiscordInc.Discord', 'com.squirrel.Discord.Discord')
    $map = @{}
    foreach ($id in $ids) {
        $path = Join-Path $base $id
        if (-not (Test-Path -LiteralPath $path)) {
            $map[$id] = $null
            continue
        }
        try {
            $entry = Get-ItemProperty -Path $path -ErrorAction Stop
            $prop = $entry.PSObject.Properties['Enabled']
            if (-not $prop) { $map[$id] = $null }
            else { $map[$id] = [int]$prop.Value }
        } catch { $map[$id] = 1 }
    }
    return (Test-DiscOptToastsOffFromMap -Map $map)
}

function Test-FileHashMatch([string]$Source, [string]$Destination) {
    try {
        if (-not (Test-Path -LiteralPath $Source) -or -not (Test-Path -LiteralPath $Destination)) { return $false }
        return (Get-FileHash -LiteralPath $Source -Algorithm SHA256 -ErrorAction Stop).Hash -ieq
            (Get-FileHash -LiteralPath $Destination -Algorithm SHA256 -ErrorAction Stop).Hash
    } catch { return $false }
}

if (-not (Test-Path $discordRoot)) {
    $statusText = 'Discord not installed'
    $detail = 'Install Discord stable first, or let the optimizer install it for you.'
    Add-Feature 'Discord install' 'Stable Discord is required before optimizations can apply.' $false
} else {
    $app = Get-ChildItem -LiteralPath $discordRoot -Directory -Filter 'app-*' -ErrorAction SilentlyContinue |
        Sort-Object {
            $parsed = [version]'0.0.0.0'
            [void][version]::TryParse(($_.Name -replace '^app-', ''), [ref]$parsed)
            $parsed
        } -Descending |
        Select-Object -First 1

    if (-not $app) {
        $statusText = 'Discord incomplete'
        $detail = 'No active Discord build folder was found.'
        Add-Feature 'Discord build' 'No app-* folder under LocalAppData\Discord.' $false
    } else {
        $resources = Join-Path $app.FullName 'resources'
        $equicordAsar = Join-Path $equicord 'equicord.asar'
        $appAsar = Join-Path $resources 'app.asar'
        $openAsarTarget = Join-Path $resources '_app.asar'
        $versionDll = Join-Path $app.FullName 'version.dll'
        $ffmpeg = Join-Path $app.FullName 'ffmpeg.dll'
        $configIni = Join-Path $app.FullName 'config.ini'
        $kitDir = Join-Path $PSScriptRoot 'kit'

        $equicordOk = $false
        if ((Test-Path -LiteralPath $equicordAsar) -and ((Get-Item -LiteralPath $equicordAsar).Length -gt 1000000) -and
            (Test-Path -LiteralPath $appAsar)) {
            try {
                $loaderBytes = [IO.File]::ReadAllBytes($appAsar)
                $equicordOk = Test-DiscOptEquicordLoaderBytes -Bytes $loaderBytes
            } catch { $equicordOk = $false }
        }
        Add-Feature 'Client mods & privacy' 'Equicord loads privacy plugins and strips noisy telemetry.' $equicordOk

        $openAsarOk = $false
        if (Test-Path -LiteralPath $openAsarTarget) {
            $openAsarOk = Test-DiscOptOpenAsarSize -SizeBytes ((Get-Item -LiteralPath $openAsarTarget).Length)
        }
        $quickStartOk = $false
        $settingsPathForQs = Join-Path $appData 'discord\settings.json'
        if (Test-Path -LiteralPath $settingsPathForQs) {
            try {
                $sjRaw = Get-Content $settingsPathForQs -Raw -Encoding UTF8
                $quickStartOk = Test-DiscOptQuickStartFromSettingsJson -JsonText $sjRaw
            } catch { }
        }
        Add-Feature 'Faster Discord startup' 'OpenASAR + quickstart and lean Chromium switches so Discord opens quicker.' ($openAsarOk -and $quickStartOk)

        $kernelOk = $false
        $ffmpegReal = Join-Path $app.FullName 'ffmpeg_real.dll'
        if ((Test-Path -LiteralPath $versionDll) -and (Test-Path -LiteralPath $ffmpeg) -and
            (Test-Path -LiteralPath $ffmpegReal) -and (Test-Path -LiteralPath $configIni)) {
            $ffSize = (Get-Item -LiteralPath $ffmpeg).Length
            $realSize = (Get-Item -LiteralPath $ffmpegReal).Length
            $verSize = (Get-Item -LiteralPath $versionDll).Length
            $configText = Get-Content -LiteralPath $configIni -Raw -ErrorAction SilentlyContinue
            $proxyHashOk = Test-FileHashMatch (Join-Path $kitDir 'ffmpeg.dll') $ffmpeg
            $verHashOk = Test-FileHashMatch (Join-Path $kitDir 'version.dll') $versionDll
            # config.ini: content peak-valid (4000 or 5000 trim, etc.) — do not require exact kit hash
            # (kit may ship a newer interval while an applied peak config remains correct)
            $kernelOk = Test-DiscOptKernelApplied `
                -FfmpegProxyBytes $ffSize `
                -FfmpegRealBytes $realSize `
                -VersionDllBytes $verSize `
                -ConfigText $configText `
                -ProxyHashMatchesKit $proxyHashOk `
                -VersionHashMatchesKit $verHashOk
        }
        Add-Feature 'Aggressive RAM + latency kernel' 'DiscOpt idle RAM trim, Above Normal priority, thread/raw-input tuning (kit binaries + peak config).' $kernelOk

        $modPath = Join-Path $app.FullName 'modules'
        $optionalModules = @('discord_hook-1', 'discord_clips-1')
        # Soft scan: never hard-fail whole debloat on access-denied recurse (common while Discord runs).
        $oldApps = @(Get-ChildItem -LiteralPath $discordRoot -Directory -Filter 'app-*' -ErrorAction SilentlyContinue |
            Where-Object {
                try {
                    [IO.Path]::GetFullPath($_.FullName).TrimEnd('\') -ne
                        [IO.Path]::GetFullPath($app.FullName).TrimEnd('\')
                } catch { $_.FullName -ne $app.FullName }
            })
        # Only count optional modules when they actually contain payload files (empty recreated dirs ≠ not debloated).
        $optionalPresent = @($optionalModules | Where-Object {
            $p = Join-Path $modPath $_
            if (-not (Test-Path -LiteralPath $p)) { return $false }
            try {
                @(Get-ChildItem -LiteralPath $p -File -Recurse -ErrorAction SilentlyContinue).Count -gt 0
            } catch { Test-Path -LiteralPath $p }
        })
        $gameSdk = @()
        if (Test-Path -LiteralPath $modPath) {
            $gameSdk = @(Get-ChildItem -LiteralPath $modPath -Recurse -Filter 'discord_game_sdk_*.dll' -ErrorAction SilentlyContinue)
        }
        $localePath = Join-Path $app.FullName 'locales'
        $extraLocales = if (Test-Path -LiteralPath $localePath) {
            @(Get-ChildItem -LiteralPath $localePath -Filter '*.pak' -ErrorAction SilentlyContinue |
                Where-Object { $_.Name -ne 'en-US.pak' })
        } else { @() }
        # Hard signals: leftover app builds / optional hook+clips payload. Soft: SDK/locales (updater may re-add).
        $hardDebloatOk = $oldApps.Count -eq 0 -and $optionalPresent.Count -eq 0
        $softDebloatOk = $gameSdk.Count -eq 0 -and $extraLocales.Count -eq 0
        $debloatOk = $hardDebloatOk -and $softDebloatOk
        $stateMatchesApp = $false
        if ($state -and $state.debloatVerified -eq $true) {
            try {
                $stateApp = [IO.Path]::GetFullPath([string]$state.appDir).TrimEnd('\')
                $curApp = [IO.Path]::GetFullPath($app.FullName).TrimEnd('\')
                $stateMatchesApp = $stateApp -ieq $curApp
            } catch { }
        }
        # Trust verified full apply for this build when only soft items drift, or when live scan is noisy under lock.
        if (-not $debloatOk -and $stateMatchesApp -and $hardDebloatOk) { $debloatOk = $true }
        if (-not $debloatOk -and $stateMatchesApp -and $state.fullApply -eq $true) { $debloatOk = $true }
        Add-Feature 'Complete client debloat' 'Old builds, optional hook/clips modules, game SDK files, extra locales, and disposable caches are removed.' $debloatOk

        $missingRuntime = @(@('discord_desktop_core-1', 'discord_utils-1', 'discord_voice-1', 'discord_media-1') |
            Where-Object { -not (Test-Path -LiteralPath (Join-Path $modPath $_)) })
        $runtimeOk = $missingRuntime.Count -eq 0
        Add-Feature 'Discord runtime integrity' 'Required desktop, utility, voice, and media modules remain installed.' $runtimeOk

        $amoledOk = $false
        $startupOk = $false
        $settingsPath = Join-Path $appData 'discord\settings.json'
        if (Test-Path -LiteralPath $settingsPath) {
            try {
                $sjRaw = Get-Content $settingsPath -Raw -Encoding UTF8
                $startupOk = Test-DiscOptStartupOffFromSettingsJson -JsonText $sjRaw
                $sj = $sjRaw | ConvertFrom-Json
                if ($sj.BACKGROUND_COLOR -eq '#000000') { $amoledOk = $true }
            } catch {}
        }
        $eqRoot = Join-Path $appData 'Equicord'
        $eqThemeFile = Join-Path $eqRoot 'themes\amoled-cord.theme.css'
        $eqSettings = Join-Path $eqRoot 'settings\settings.json'
        if ((Test-Path -LiteralPath $eqThemeFile) -and (Test-Path -LiteralPath $eqSettings)) {
            try {
                $eqSj = Get-Content $eqSettings -Raw -Encoding UTF8 | ConvertFrom-Json
                $enabled = @($eqSj.enabledThemes)
                if ($enabled | Where-Object { "$_" -match '(?i)amoled' }) { $amoledOk = $true }
            } catch {
                if (Test-Path -LiteralPath $eqThemeFile) { $amoledOk = $true }
            }
        } elseif (Test-Path -LiteralPath $eqThemeFile) {
            $amoledOk = $true
        }
        Add-Feature 'True black AMOLED theme' 'Equicord amoled-cord theme (not forced OpenAsar CSS).' $amoledOk

        $notificationsOk = Test-DiscordToastsOff
        $windowsQuietOk = $startupOk -and $notificationsOk -and
            (Test-StableDiscordWindowsQuiet $discordRoot)
        Add-Feature 'Windows background suppression' 'No Discord autostart or scheduled tasks; Windows toasts off; tray icon not promoted.' $windowsQuietOk

        $launchOk = $false
        try {
            $sm = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Discord Inc\Discord.lnk'
            if (Test-Path $sm) {
                $sc = (New-Object -ComObject WScript.Shell).CreateShortcut($sm)
                if ([string]$sc.TargetPath -match '(?i)wscript\.exe$' -and
                    [string]$sc.Arguments -match '(?i)Discord\.vbs') { $launchOk = $true }
            }
        } catch {}
        Add-Feature 'Start Menu / apps launch path' 'Start Menu and taskbar Discord shortcuts use the OptiHub -Launch path (OpenASAR + kernel). No desktop icons created.' $launchOk

        $markerOk = Test-DiscOptApplyRecord -State $state -CurrentAppDir $app.FullName
        Add-Feature 'Verified optimizer record' 'A completed full apply is recorded for this exact Discord build.' $markerOk

        $isApplied = [bool]($markerOk -and $equicordOk -and $openAsarOk -and $kernelOk -and
            $debloatOk -and $windowsQuietOk -and $amoledOk -and $runtimeOk -and $launchOk)
        if ($isApplied) {
            $statusText = 'Already optimized'
            $detail = 'No-compromise pack active: aggressive trim, Above Normal priority, full debloat, OpenASAR, and Equicord.'
        } elseif ($state -and $state.applied -eq $true -and -not $markerOk) {
            $statusText = 'Discord updated - reapply'
            $detail = 'Discord installed a new build. Run again to restore OpenAsar, kernel, debloat, and Windows quiet. Daily Start Menu launch auto-heals OpenAsar/kernel when missing.'
        } elseif (-not $openAsarOk -or -not $kernelOk -or -not $equicordOk) {
            $statusText = 'Mods need restore'
            $detail = 'OpenAsar, Equicord, or the DiscOpt kernel is missing (often after a Discord update). Run to restore, or open Discord from Start Menu for auto-heal.'
        } else {
            $statusText = 'Ready to optimize'
            $detail = 'Some pieces are missing. Run to finish setup and unlock the savings below.'
        }
    }
}

$payload = [ordered]@{
    isApplied  = $isApplied
    statusText = $statusText
    detail     = $detail
    features   = @($features)
}

$payload | ConvertTo-Json -Compress -Depth 5
