# OptiHub - detect whether Discord Optimizer is already applied.
# Prints a single JSON object to stdout for the WinUI host.

$ErrorActionPreference = 'SilentlyContinue'

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

function Test-StableDiscordText([string]$Text, [string]$Root) {
    if ([string]::IsNullOrWhiteSpace($Text)) { return $false }
    try {
        $prefix = [IO.Path]::GetFullPath($Root).TrimEnd('\') + '\'
        $expanded = [Environment]::ExpandEnvironmentVariables($Text).Replace('/', '\')
        return $expanded.IndexOf($prefix, [StringComparison]::OrdinalIgnoreCase) -ge 0
    } catch { return $false }
}

function Test-StableDiscordWindowsQuiet([string]$Root) {
    # Policy (matches Apply-WindowsTweaks):
    # - no Discord autostart (Run key / scheduled tasks)
    # - Windows toasts ON (message alerts)
    # - tray icon visible (IsPromoted=1) when a NotifyIcon entry exists
    try {
        $runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
        if (Test-Path $runKey) {
            $run = Get-Item -Path $runKey -ErrorAction Stop
            foreach ($name in @($run.GetValueNames())) {
                if (Test-StableDiscordText ([string]$run.GetValue($name)) $Root) { return $false }
            }
        }

        foreach ($task in @(Get-ScheduledTask -ErrorAction SilentlyContinue)) {
            if ($task.TaskName -notmatch '(?i)Discord' -and $task.TaskPath -notmatch '(?i)Discord') { continue }
            $stable = $false
            foreach ($action in @($task.Actions)) {
                if ((Test-StableDiscordText ([string]$action.Execute) $Root) -or
                    (Test-StableDiscordText ([string]$action.Arguments) $Root) -or
                    (Test-StableDiscordText ([string]$action.WorkingDirectory) $Root)) {
                    $stable = $true
                    break
                }
            }
            if (-not $stable -and ($task.TaskName -match '(?i)Discord' -or $task.TaskPath -match '(?i)Discord')) {
                $stable = $true
            }
            if ($stable -and [bool]$task.Settings.Enabled) { return $false }
        }

        # Tray: when Discord notify-icon entries exist, prefer promoted/visible (1).
        # Missing NotifyIconSettings entries are OK (Windows creates them after first launch).
        $trayRoot = 'HKCU:\Control Panel\NotifyIconSettings'
        if (Test-Path $trayRoot) {
            foreach ($key in @(Get-ChildItem -Path $trayRoot -ErrorAction SilentlyContinue)) {
                $item = Get-Item -Path $key.PSPath -ErrorAction SilentlyContinue
                if (-not $item) { continue }
                $exe = [string]$item.GetValue('ExecutablePath')
                if (-not $exe) { continue }
                if (-not ((Test-StableDiscordText $exe $Root) -or ($exe -match '(?i)Discord'))) { continue }
                # Do not require IsPromoted=0 (that was "hide tray" debloat — we keep tray visible)
            }
        }
        return $true
    } catch { return $false }
}

function Test-DiscordToastsOn {
    # Product policy: Windows toast notifications stay ENABLED for Discord message alerts.
    $base = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Notifications\Settings'
    $ids = @('Discord', 'Discord.Desktop', 'DiscordInc.Discord', 'com.squirrel.Discord.Discord')
    $any = $false
    foreach ($id in $ids) {
        $path = Join-Path $base $id
        if (-not (Test-Path -LiteralPath $path)) { continue }
        $any = $true
        try {
            $entry = Get-ItemProperty -Path $path -ErrorAction Stop
            $prop = $entry.PSObject.Properties['Enabled']
            if (-not $prop -or [int]$prop.Value -ne 1) { return $false }
        } catch { return $false }
    }
    # If no keys exist yet, treat as OK (Windows may create them later); apply path stamps them on.
    return $true
}

function Test-EquicordLoader([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return $false }
    try {
        $bytes = [IO.File]::ReadAllBytes($Path)
        if ($bytes.Length -lt 64 -or $bytes.Length -ge 4096) { return $false }
        $text = [Text.Encoding]::UTF8.GetString($bytes)
        return $text -match '(?i)equicord\.asar' -and $text -match '(?i)require'
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

        $equicordOk = (Test-Path -LiteralPath $equicordAsar) -and
            ((Get-Item -LiteralPath $equicordAsar).Length -gt 1000000) -and
            (Test-EquicordLoader $appAsar)
        Add-Feature 'Client mods & privacy' 'Equicord loads privacy plugins and strips noisy telemetry.' $equicordOk

        $openAsarOk = $false
        if (Test-Path -LiteralPath $openAsarTarget) {
            $sz = (Get-Item -LiteralPath $openAsarTarget).Length
            if ($sz -gt 10000 -and $sz -lt 500000) { $openAsarOk = $true }
        }
        Add-Feature 'Faster Discord startup' 'OpenASAR replaces the heavy launcher path so Discord opens quicker.' $openAsarOk

        $kernelOk = $false
        $ffmpegReal = Join-Path $app.FullName 'ffmpeg_real.dll'
        if ((Test-Path -LiteralPath $versionDll) -and (Test-Path -LiteralPath $ffmpeg) -and
            (Test-Path -LiteralPath $ffmpegReal) -and (Test-Path -LiteralPath $configIni)) {
            $ffSize = (Get-Item -LiteralPath $ffmpeg).Length
            $realSize = (Get-Item -LiteralPath $ffmpegReal).Length
            $verSize = (Get-Item -LiteralPath $versionDll).Length
            $configText = Get-Content -LiteralPath $configIni -Raw -ErrorAction SilentlyContinue
            $hashesOk = $true
            foreach ($pair in @(
                @{ Source = Join-Path $PSScriptRoot 'kit\ffmpeg.dll'; Destination = $ffmpeg },
                @{ Source = Join-Path $PSScriptRoot 'kit\version.dll'; Destination = $versionDll },
                @{ Source = Join-Path $PSScriptRoot 'kit\config.ini'; Destination = $configIni }
            )) {
                try {
                    if (-not (Test-Path -LiteralPath $pair.Source) -or
                        (Get-FileHash -LiteralPath $pair.Source -Algorithm SHA256 -ErrorAction Stop).Hash -ine
                        (Get-FileHash -LiteralPath $pair.Destination -Algorithm SHA256 -ErrorAction Stop).Hash) {
                        $hashesOk = $false
                    }
                } catch { $hashesOk = $false }
            }
            if ($ffSize -lt 500000 -and $realSize -gt 500000 -and $verSize -gt 50000 -and
                $configText -match '(?m)^TrimIntervalMs=5000\s*$' -and
                $configText -match '(?m)^PriorityClass=3\s*$' -and $hashesOk) { $kernelOk = $true }
        }
        Add-Feature 'Aggressive RAM + latency kernel' 'DiscOpt reclaims memory every 5s, uses Above Normal process priority, and enables thread/raw-input tuning.' $kernelOk

        $modPath = Join-Path $app.FullName 'modules'
        $optionalModules = @('discord_hook-1', 'discord_clips-1')
        $debloatOk = $false
        try {
            $oldApps = @(Get-ChildItem -LiteralPath $discordRoot -Directory -Filter 'app-*' -ErrorAction Stop |
                Where-Object { $_.FullName -ne $app.FullName })
            $optionalPresent = @($optionalModules | Where-Object { Test-Path -LiteralPath (Join-Path $modPath $_) })
            $gameSdk = if (Test-Path -LiteralPath $modPath) {
                @(Get-ChildItem -LiteralPath $modPath -Recurse -Filter 'discord_game_sdk_*.dll' -ErrorAction Stop)
            } else { @() }
            $localePath = Join-Path $app.FullName 'locales'
            $extraLocales = if (Test-Path -LiteralPath $localePath) {
                @(Get-ChildItem -LiteralPath $localePath -Filter '*.pak' -ErrorAction Stop |
                    Where-Object { $_.Name -ne 'en-US.pak' })
            } else { @() }
            $debloatOk = $oldApps.Count -eq 0 -and $optionalPresent.Count -eq 0 -and
                $gameSdk.Count -eq 0 -and $extraLocales.Count -eq 0
        } catch { $debloatOk = $false }
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
                $sj = Get-Content $settingsPath -Raw -Encoding UTF8 | ConvertFrom-Json
                # Legacy OptiHub stamp (no longer forced — pure #000 can blank the client)
                if ($sj.BACKGROUND_COLOR -eq '#000000') { $amoledOk = $true }
                if ($sj.OPEN_ON_STARTUP -eq $false) { $startupOk = $true }
            } catch {}
        }
        # Preferred: Equicord AMOLED theme (amoled-cord) enabled — real dark UI without forced CSS
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

        # Toasts ON (message alerts), no autostart, no Discord scheduled tasks — matches Apply-WindowsTweaks
        $notificationsOk = Test-DiscordToastsOn
        $windowsQuietOk = $startupOk -and $notificationsOk -and
            (Test-StableDiscordWindowsQuiet $discordRoot)
        Add-Feature 'Windows background suppression' 'No Discord autostart or scheduled tasks; Windows toasts stay ON for messages; tray stays usable.' $windowsQuietOk

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

        $stateAppOk = $false
        try {
            $stateAppOk = [IO.Path]::GetFullPath([string]$state.appDir).TrimEnd('\') -ieq
                [IO.Path]::GetFullPath($app.FullName).TrimEnd('\')
        } catch { }
        $markerOk = [bool]($state -and
            [string]$state.version -eq '1.3.0' -and
            [string]$state.applyStatus -eq 'applied' -and
            $state.applied -eq $true -and
            $state.fullApply -eq $true -and
            $state.windowsVerified -eq $true -and
            $state.debloatVerified -eq $true -and
            $stateAppOk)
        Add-Feature 'Verified optimizer record' 'A completed 1.3.0 full apply is recorded for this exact Discord build.' $markerOk

        $isApplied = [bool]($markerOk -and $equicordOk -and $openAsarOk -and $kernelOk -and
            $debloatOk -and $windowsQuietOk -and $amoledOk -and $runtimeOk -and $launchOk)
        if ($isApplied) {
            $statusText = 'Already optimized'
            $detail = 'No-compromise pack active: aggressive trim, Above Normal priority, full debloat, OpenASAR, and Equicord.'
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
