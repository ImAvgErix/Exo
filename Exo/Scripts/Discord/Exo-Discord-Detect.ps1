# Exo - detect whether Discord Optimizer is already applied.
# Prints a single JSON object to stdout for the WinUI host.
# Classifiers: DiscordDetectCore.ps1 (pure) - keep aligned with DiscordLogic.cs

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
$statePath = Join-Path $local 'Exo\discord-optimizer.json'
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

function Get-LeanPluginStatus($Settings) {
    try {
        $profiles = Join-Path $PSScriptRoot 'kit\profiles'
        $policy = Get-Content (Join-Path $profiles 'lean-plugin-policy.json') -Raw -Encoding UTF8 | ConvertFrom-Json
        $manifest = @()
        $manifest += @(Get-Content (Join-Path $profiles 'equicordplugins.json') -Raw -Encoding UTF8 | ConvertFrom-Json)
        $manifest += @(Get-Content (Join-Path $profiles 'vencordplugins.json') -Raw -Encoding UTF8 | ConvertFrom-Json)
        $byName = @{}
        foreach ($plugin in $manifest) { $byName[[string]$plugin.name] = $plugin }
        $allowed = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        $required = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($name in @($policy.enabled)) { [void]$allowed.Add([string]$name); [void]$required.Add([string]$name) }
        foreach ($plugin in $manifest) {
            if ($plugin.required -eq $true) {
                [void]$allowed.Add([string]$plugin.name)
                [void]$required.Add([string]$plugin.name)
            }
        }
        do {
            $changed = $false
            foreach ($name in @($allowed)) {
                if (-not $byName.ContainsKey($name)) { continue }
                if ($byName[$name].PSObject.Properties.Name -notcontains 'dependencies') { continue }
                foreach ($dependency in @($byName[$name].dependencies)) {
                    if ($dependency -and $allowed.Add([string]$dependency)) { $changed = $true }
                }
            }
        } while ($changed)
        foreach ($name in @($allowed)) { [void]$required.Add([string]$name) }
        $enabled = @($Settings.plugins.PSObject.Properties | Where-Object { $_.Value.enabled -eq $true } | ForEach-Object Name)
        $ok = Test-DiscOptLeanPluginNames -EnabledNames $enabled -AllowedNames @($allowed) -RequiredNames @($required) -MaximumEnabled ([int]$policy.maximumEnabled)
        return [pscustomobject]@{ Ok = $ok; Enabled = $enabled.Count; Maximum = [int]$policy.maximumEnabled; Error = '' }
    } catch {
        return [pscustomobject]@{ Ok = $false; Enabled = 0; Maximum = 0; Error = $_.Exception.Message }
    }
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
                if ($null -eq $action) { continue }
                # Non-exec actions (COM-handler/email/show-message) lack these properties.
                $ap = $action.PSObject.Properties.Name
                $actExe = if ($ap -contains 'Execute') { [string]$action.Execute } else { '' }
                $actArg = if ($ap -contains 'Arguments') { [string]$action.Arguments } else { '' }
                $actDir = if ($ap -contains 'WorkingDirectory') { [string]$action.WorkingDirectory } else { '' }
                if ((Test-DiscOptStablePathText $actExe $Root) -or
                    (Test-DiscOptStablePathText $actArg $Root) -or
                    (Test-DiscOptStablePathText $actDir $Root)) {
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

function Get-DiscordQosPolicyValueMap([string]$PolicyName) {
    $path = Join-Path 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\QoS' $PolicyName
    if (-not (Test-Path -LiteralPath $path)) { return $null }
    $map = @{}
    try {
        $item = Get-Item -LiteralPath $path -ErrorAction Stop
        foreach ($name in @($item.GetValueNames())) {
            $map[$name] = [string]$item.GetValue($name)
        }
    } catch { return $null }
    return $map
}

function Get-InstalledDetectVariants {
    $installed = @()
    foreach ($variant in @(Get-DiscOptVariantDefinitions)) {
        $root = Join-Path $local ([string]$variant.LocalDir)
        if (Test-Path -LiteralPath $root) {
            $apps = @(Get-ChildItem -LiteralPath $root -Directory -Filter 'app-*' -ErrorAction SilentlyContinue)
            if ($apps.Count -gt 0) { $installed += , $variant }
        }
    }
    return @($installed)
}

function Test-VariantAutostartQuiet([string]$LocalDir) {
    try {
        $variantRoot = Join-Path $local $LocalDir
        $runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
        if (-not (Test-Path $runKey)) { return $true }
        $run = Get-Item -Path $runKey -ErrorAction Stop
        foreach ($name in @($run.GetValueNames())) {
            if (Test-DiscOptStablePathText ([string]$run.GetValue($name)) $variantRoot) { return $false }
        }
        return $true
    } catch { return $false }
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

        # Exo Host (current): stock shell on _app.asar (large) + host flags in settings.json.
        # Legacy OpenAsar (small _app.asar rewrite) is NOT accepted - Apply never produces it.
        $stockShellOk = $false
        if (Test-Path -LiteralPath $openAsarTarget) {
            $stockShellOk = (Get-Item -LiteralPath $openAsarTarget).Length -gt 1000000
        }
        $quickStartOk = $false
        $settingsPathForQs = Join-Path $appData 'discord\settings.json'
        if (Test-Path -LiteralPath $settingsPathForQs) {
            try {
                $sjRaw = Get-Content $settingsPathForQs -Raw -Encoding UTF8
                $quickStartOk = Test-DiscOptQuickStartFromSettingsJson -JsonText $sjRaw
            } catch { }
        }
        # Host path: Equicord + stock _app.asar + host flags (binary, no legacy path)
        $exoHostOk = $equicordOk -and $quickStartOk -and $stockShellOk
        Add-Feature 'Exo Host (fast launch)' 'Equicord loader + stock Discord shell + SKIP_HOST_UPDATE / chromium lean (no OpenAsar).' $exoHostOk

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
            # config.ini: content valid (4000 or 5000 trim, etc.) - do not require exact kit hash
            # (kit may ship a newer interval while an applied config remains correct)
            $kernelOk = Test-DiscOptKernelApplied `
                -FfmpegProxyBytes $ffSize `
                -FfmpegRealBytes $realSize `
                -VersionDllBytes $verSize `
                -ConfigText $configText `
                -ProxyHashMatchesKit $proxyHashOk `
                -VersionHashMatchesKit $verHashOk
        }
        Add-Feature 'Background memory + input policy' 'Verified DiscOpt binaries apply a 4-second idle working-set policy, Above Normal process priority, and input-thread tuning.' $kernelOk

        $modPath = Join-Path $app.FullName 'modules'
        $optionalModules = @('discord_hook-1', 'discord_clips-1')
        # Always force arrays with @() - bare if/pipeline can unwrap to $null (Count throws under StrictMode).
        $oldApps = @(Get-ChildItem -LiteralPath $discordRoot -Directory -Filter 'app-*' -ErrorAction SilentlyContinue |
            Where-Object {
                try {
                    [IO.Path]::GetFullPath($_.FullName).TrimEnd('\') -ne
                        [IO.Path]::GetFullPath($app.FullName).TrimEnd('\')
                } catch { $_.FullName -ne $app.FullName }
            })
        # Only count optional modules when they contain payload files (empty recreated dirs != not debloated).
        $optionalPresent = @()
        foreach ($name in $optionalModules) {
            $p = Join-Path $modPath $name
            if (Test-DiscOptModuleDirHasPayload -ModuleDir $p) { $optionalPresent += $name }
        }
        $gameSdk = @()
        if (Test-Path -LiteralPath $modPath) {
            $gameSdk = @(Get-ChildItem -LiteralPath $modPath -Recurse -Filter 'discord_game_sdk_*.dll' -ErrorAction SilentlyContinue)
        }
        $localePath = Join-Path $app.FullName 'locales'
        $extraLocales = @()
        if (Test-Path -LiteralPath $localePath) {
            $extraLocales = @(Get-ChildItem -LiteralPath $localePath -Filter '*.pak' -ErrorAction SilentlyContinue |
                Where-Object { $_.Name -ne 'en-US.pak' })
        }
        $stateMatchesApp = $false
        # Sparse failure/quick states lack debloatVerified - guard (StrictMode-safe).
        if ($state -and ($state.PSObject.Properties.Name -contains 'debloatVerified') -and $state.debloatVerified -eq $true) {
            try {
                $stateApp = [IO.Path]::GetFullPath([string]$state.appDir).TrimEnd('\')
                $curApp = [IO.Path]::GetFullPath($app.FullName).TrimEnd('\')
                $stateMatchesApp = $stateApp -ieq $curApp
            } catch { }
        }
        # Pure classifier (DiscordDetectCore) - soft-drift recovery only when hard signals are clean.
        $debloatOk = Test-DiscOptClientDebloat `
            -LeftoverAppBuildCount (@($oldApps).Count) `
            -OptionalModulePayloadCount (@($optionalPresent).Count) `
            -GameSdkFileCount (@($gameSdk).Count) `
            -ExtraLocaleCount (@($extraLocales).Count) `
            -StateDebloatVerifiedSameApp:$stateMatchesApp
        Add-Feature 'Complete client debloat' 'Old builds, optional hook/clips modules, game SDK files, extra locales, and disposable caches are removed.' $debloatOk

        $missingRuntime = @(@('discord_desktop_core-1', 'discord_utils-1', 'discord_voice-1', 'discord_media-1') |
            Where-Object { -not (Test-Path -LiteralPath (Join-Path $modPath $_)) })
        $runtimeOk = $missingRuntime.Count -eq 0
        Add-Feature 'Discord runtime integrity' 'Required desktop, utility, voice, and media modules remain installed.' $runtimeOk

        $amoledOk = $false
        $leanPluginsOk = $false
        $leanPluginDetail = 'Lean plugin policy missing or unreadable.'
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
                $leanStatus = Get-LeanPluginStatus $eqSj
                $leanPluginsOk = [bool]$leanStatus.Ok
                $leanPluginDetail = if ($leanStatus.Error) {
                    "Plugin policy check unavailable: $($leanStatus.Error)"
                } else {
                    "$($leanStatus.Enabled) enabled / budget $($leanStatus.Maximum) / required dependencies gated"
                }
            } catch {
                if (Test-Path -LiteralPath $eqThemeFile) { $amoledOk = $true }
            }
        } elseif (Test-Path -LiteralPath $eqThemeFile) {
            $amoledOk = $true
        }
        Add-Feature 'Dark mode' 'True-black Equicord theme without a forced overlay.' $amoledOk
        Add-Feature 'Lean plugin budget' $leanPluginDetail $leanPluginsOk

        $notificationsOk = Test-DiscordToastsOff
        $windowsQuietOk = $startupOk -and $notificationsOk -and
            (Test-StableDiscordWindowsQuiet $discordRoot)
        Add-Feature 'Windows background suppression' 'No Discord autostart or scheduled tasks; Windows toasts off; tray icon not promoted.' $windowsQuietOk

        $launchOk = $false
        try {
            $sm = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Discord Inc\Discord.lnk'
            if (Test-Path $sm) {
                $sc = (New-Object -ComObject WScript.Shell).CreateShortcut($sm)
                $tp = [string]$sc.TargetPath
                $args = [string]$sc.Arguments
                # Preferred: official Update.exe --processStart Discord.exe
                if ($tp -match '(?i)Update\.exe$' -and $args -match '(?i)processStart') { $launchOk = $true }
                # Legacy: Discord.vbs via wscript
                elseif ($tp -match '(?i)wscript\.exe$' -and $args -match '(?i)Discord\.vbs') { $launchOk = $true }
                # Also accept direct Discord.exe in app-* (still works)
                elseif ($tp -match '(?i)Discord\.exe$') { $launchOk = $true }
            }
        } catch {}
        Add-Feature 'Start Menu / apps launch path' 'Start Menu Discord shortcut uses Update.exe (or Exo launch helper). No desktop icons.' $launchOk

        # Voice QoS (DSCP 46 / UDP) policy for every installed variant
        $installedVariants = @(Get-InstalledDetectVariants)
        $qosOk = $installedVariants.Count -gt 0
        foreach ($variant in $installedVariants) {
            $map = Get-DiscordQosPolicyValueMap ([string]$variant.QosPolicy)
            if (-not (Test-DiscOptQosPolicyMap -Map $map -ExpectedExe ([string]$variant.Exe))) {
                $qosOk = $false
                break
            }
        }
        Add-Feature 'Voice priority (QoS DSCP 46)' 'Windows QoS policy tags Discord voice UDP traffic as Expedited Forwarding for every installed variant.' $qosOk

        # PTB / Canary variants: all installed variants must be optimized
        $extraVariants = @($installedVariants | Where-Object { [string]$_.Name -ne 'stable' })
        $variantsOk = $true
        foreach ($variant in $extraVariants) {
            $variantSettings = Join-Path $appData ((([string]$variant.AppDataDir)) + '\settings.json')
            $variantFlagsOk = $false
            if (Test-Path -LiteralPath $variantSettings) {
                try {
                    $variantFlagsOk = Test-DiscOptVariantSettingsJson -JsonText (Get-Content -LiteralPath $variantSettings -Raw -Encoding UTF8)
                } catch { }
            }
            $variantAutostartOk = Test-VariantAutostartQuiet ([string]$variant.LocalDir)
            $variantQosOk = Test-DiscOptQosPolicyMap -Map (Get-DiscordQosPolicyValueMap ([string]$variant.QosPolicy)) -ExpectedExe ([string]$variant.Exe)
            if (-not (Test-DiscOptVariantOptimized -SettingsFlagsOk $variantFlagsOk -AutostartQuiet $variantAutostartOk -QosOk $variantQosOk)) {
                $variantsOk = $false
                break
            }
        }
        $variantDetail = if ($extraVariants.Count -eq 0) {
            'Only stable Discord is installed; PTB/Canary would be optimized automatically.'
        } else {
            'Every installed PTB/Canary variant has quiet flags, no autostart, and voice QoS.'
        }
        Add-Feature 'Discord variants (PTB/Canary)' $variantDetail $variantsOk

        $markerOk = Test-DiscOptApplyRecord -State $state -CurrentAppDir $app.FullName
        Add-Feature 'Verified optimizer record' 'A completed full apply is recorded for this exact Discord build.' $markerOk

        $isApplied = [bool]($markerOk -and $equicordOk -and $exoHostOk -and $kernelOk -and
            $debloatOk -and $windowsQuietOk -and $amoledOk -and $runtimeOk -and $launchOk -and
            $qosOk -and $variantsOk -and $leanPluginsOk)
        if ($isApplied) {
            $statusText = 'Already optimized'
            $detail = 'Verified Discord policy active: lean client, background policy, privacy settings, and dark mode.'
        } elseif ($state -and $state.applied -eq $true -and -not $markerOk) {
            $statusText = 'Discord updated - reapply'
            $detail = 'Discord installed a new build. Run Apply again to restore Equicord, Exo Host, kernel, and Windows quiet.'
        } elseif (-not $exoHostOk -or -not $kernelOk -or -not $equicordOk) {
            $statusText = 'Mods need restore'
            $detail = 'Equicord, Exo Host, or the DiscOpt kernel is incomplete. Run Apply to restore.'
        } else {
            $statusText = 'Ready to optimize'
            $detail = 'Some pieces are missing. Run Apply to finish setup.'
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
