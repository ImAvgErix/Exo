# 40-DebloatWindows.ps1 - Debloat, cache, Windows tweaks, OpenASAR, profile flags
# Dot-sourced by Disc-Optimizer.ps1 (load order = filename sort).
# Universal multi-PC kit - do not assume Equicord/Discord already configured.

function Invoke-Debloat([string]$AppDir, [ref]$Freed) {
    Write-Step 'Debloating Discord...'

    # Old app-* hosts only (safe). Never wipe the active build.
    Get-ChildItem $DiscordRoot -Directory -Filter 'app-*' |
        Where-Object { $_.FullName -ne $AppDir } |
        ForEach-Object { if (Remove-Safe $_.FullName $freed) { Write-Ok "Removed $($_.Name)" } }

    # Only strip known-optional modules. Deleting unknown modules broke Discord
    # 1.0.92xx+ (stuck on Starting / hosts_req_modules_installed=false).
    $modPath = Join-Path $AppDir 'modules'
    if (Test-Path $modPath) {
        foreach ($name in $OptionalModules) {
            $folder = Join-Path $modPath $name
            if (Test-Path -LiteralPath $folder) {
                if (Remove-Safe $folder $freed) { Write-Ok "Removed optional module $name" }
            }
        }
        Get-ChildItem $modPath -Recurse -Filter 'discord_game_sdk_*.dll' -ErrorAction SilentlyContinue |
            ForEach-Object { if (Remove-Safe $_.FullName $freed) { Write-Ok 'Removed game SDK' } }
    }

    $localePath = Join-Path $AppDir 'locales'
    if (Test-Path $localePath) {
        Get-ChildItem "$localePath\*.pak" | Where-Object { $_.Name -ne 'en-US.pak' } |
            ForEach-Object { if (Remove-Safe $_.FullName $freed) { Write-Ok "Removed locale $($_.Name)" } }
    }

    # NOTE: never remove d3dcompiler_47.dll, vulkan-1.dll, vk_swiftshader*, or
    # chrome_*_percent.pak - Chromium needs them for rendering and removing them
    # causes blank/black windows on many GPUs.
    foreach ($pattern in @(
        '.first-run', 'Discord.exe.sig', 'discord_wer.*',
        'Microsoft.Gaming.XboxApp.XboxNetwork.winmd', '*.log'
    )) {
        Get-ChildItem (Join-Path $AppDir $pattern) -ErrorAction SilentlyContinue | ForEach-Object {
            if ($Protected -contains $_.Name) { return }
            if (Remove-Safe $_.FullName $freed) { Write-Ok "Removed $($_.Name)" }
        }
    }

    Write-Ok "Debloat saved ~$([math]::Round($freed.Value / 1MB, 1)) MB"
}

function Clear-DiscordConflictLeftovers {
    # Remove stale OptiHub / crash / GPU caches that can fight a fresh apply.
    Write-Step 'Clearing conflicting Discord leftovers (login preserved)...'
    $n = 0
    $desk = [Environment]::GetFolderPath('Desktop')
    foreach ($name in @('Discord (OptiHub).lnk')) {
        $p = Join-Path $desk $name
        if (Test-Path -LiteralPath $p) {
            Remove-Item -LiteralPath $p -Force -ErrorAction SilentlyContinue
            $n++; Write-Ok "Removed $name"
        }
    }
    $local = Join-Path $env:LOCALAPPDATA 'Discord'
    if (Test-Path $local) {
        foreach ($rel in @('GPUCache', 'Code Cache', 'ShaderCache', 'Crashpad', 'DawnCache', 'GrShaderCache')) {
            $p = Join-Path $local $rel
            if (Test-Path $p) {
                try {
                    Remove-Item -LiteralPath $p -Recurse -Force -ErrorAction SilentlyContinue
                    $n++
                    Write-Ok "Cleared LocalAppData\Discord\$rel"
                } catch { }
            }
        }
    }
    Write-Ok "Discord conflict cleanup items: $n"
    return $n
}

function Clear-DiscordSafeCache([ref]$Freed) {
    if ($SkipCacheClean) {
        Write-Ok 'Cache clean skipped (-SkipCacheClean)'
        return
    }

    Write-Step 'Cleaning safe Discord caches (login/session preserved)...'
    $before = $Freed.Value
    foreach ($relative in $SafeCacheTargets) {
        $path = Join-Path $AppData $relative
        if (Remove-Safe $path $Freed) {
            Write-Ok "Cleaned $relative"
        }
    }

    # Squirrel state (packages\RELEASES, installer.db) is never touched -
    # Update.exe silently refuses to launch Discord without it.
    $saved = $Freed.Value - $before
    if ($saved -gt 0) {
        Write-Ok "Safe cache clean saved ~$([math]::Round($saved / 1MB, 1)) MB"
    } else {
        Write-Ok 'Safe cache clean found nothing to remove'
    }
}

function Test-CacheCleanNeeded {
    if ($SkipCacheClean) { return $false }
    foreach ($relative in $SafeCacheTargets) {
        $path = Join-Path $AppData $relative
        if (-not (Test-Path $path)) { continue }
        # Sample first files only          enough to decide if a clean is worth it
        $sample = @(Get-ChildItem $path -Recurse -Force -File -ErrorAction SilentlyContinue | Select-Object -First 50)
        if ($sample.Count -eq 0) { continue }
        $sum = ($sample | Measure-Object -Property Length -Sum).Sum
        if ($sum -gt 1MB -or $sample.Count -ge 50) { return $true }
    }
    return $false
}

function Get-DiscordManifestCached {
    if ($Script:DiscordManifest) { return $Script:DiscordManifest }
    $Script:DiscordManifest = Invoke-RestMethod -Uri 'https://updates.discord.com/distributions/app/manifests/latest?channel=stable&platform=win&arch=x64' -Headers @{ 'User-Agent' = 'OptiHub-Discord/1.0' }
    return $Script:DiscordManifest
}

function Install-DiscordModuleFromManifest([string]$AppDir, [string]$ModuleName) {
    $folder = Join-Path $AppDir "modules\$ModuleName-1"
    if (Test-Path $folder) { return $true }

    $manifest = Get-DiscordManifestCached
    $mod = $manifest.modules.$ModuleName
    if (-not $mod -or -not $mod.full.url) { throw "$ModuleName missing from Discord manifest" }

    $work = Get-DiscOptTempPath "discopt-$ModuleName"
    if (Test-Path $work) { Remove-Item $work -Recurse -Force }
    New-Item -ItemType Directory -Path $work -Force | Out-Null

    $distro = Join-Path $work 'pkg.distro'
    $tar = Join-Path $work 'pkg.tar'
    $extract = Join-Path $work 'extract'
    try {
        Invoke-WebRequest -Uri $mod.full.url -OutFile $distro -UseBasicParsing

        $in = $out = $br = $null
        try {
            $in = [IO.File]::OpenRead($distro)
            $out = [IO.File]::Create($tar)
            $br = [System.IO.Compression.BrotliStream]::new($in, [IO.Compression.CompressionMode]::Decompress)
            $br.CopyTo($out)
        } finally {
            if ($br) { $br.Dispose() }
            if ($out) { $out.Dispose() }
            if ($in) { $in.Dispose() }
        }

        New-Item -ItemType Directory -Path $extract -Force | Out-Null
        $global:LASTEXITCODE = 0
        & tar -xf $tar -C $extract 2>$null
        if ($LASTEXITCODE -ne 0) { throw "tar failed while extracting $ModuleName" }

        $files = Join-Path $extract 'files'
        if (-not (Test-Path $files)) { throw "$ModuleName package had no files/" }

        $modRoot = Join-Path $AppDir 'modules'
        if (-not (Test-Path $modRoot)) { New-Item -ItemType Directory -Path $modRoot -Force | Out-Null }
        New-Item -ItemType Directory -Path $folder -Force | Out-Null
        Copy-Item -Path (Join-Path $files '*') -Destination $folder -Recurse -Force
        return $true
    } finally {
        Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
    }
}

function Ensure-RuntimeModules([string]$AppDir) {
    foreach ($name in $RuntimeModules) {
        $folder = Join-Path $AppDir "modules\$name-1"
        if (Test-Path $folder) {
            Write-Ok "$name module present"
            continue
        }
        Write-Step "Installing $name module (required by Discord core)..."
        Install-DiscordModuleFromManifest $AppDir $name | Out-Null
        Write-Ok "$name module installed"
    }
}

function Ensure-KrispModule([string]$AppDir) {
    $krisp = Join-Path $AppDir 'modules\discord_krisp-1'
    if (Test-Path $krisp) {
        Write-Ok 'Krisp module present (noise suppression UI)'
        return
    }

    Write-Step 'Installing Krisp module (noise suppression dropdown)...'
    Install-DiscordModuleFromManifest $AppDir 'discord_krisp' | Out-Null
    if (-not (Test-Path $krisp)) { throw 'Krisp module missing after CDN install' }
    Write-Ok 'Krisp module installed'
}

function Test-DebloatNeeded([string]$AppDir) {
    $reasons = @()

    $oldApps = @(Get-ChildItem $DiscordRoot -Directory -Filter 'app-*' -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -ne $AppDir })
    if ($oldApps.Count -gt 0) { $reasons += "$($oldApps.Count) old app-* folder(s)" }

    $modPath = Join-Path $AppDir 'modules'
    if (Test-Path $modPath) {
        $optionalPresent = @($OptionalModules | Where-Object { Test-Path (Join-Path $modPath $_) })
        if ($optionalPresent.Count -gt 0) { $reasons += "$($optionalPresent.Count) optional module(s)" }
    }

    $localePath = Join-Path $AppDir 'locales'
    if (Test-Path $localePath) {
        $extraLocales = @(Get-ChildItem "$localePath\*.pak" -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -ne 'en-US.pak' })
        if ($extraLocales.Count -gt 0) { $reasons += "$($extraLocales.Count) extra locale(s)" }
    }

    return @{
        Needed  = ($reasons.Count -gt 0)
        Reasons = $reasons
    }
}

function Disable-DiscordWindowsAutostart {
    $runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
    if (-not (Test-Path $runKey)) { return }
    $props = Get-ItemProperty $runKey -ErrorAction SilentlyContinue
    if (-not $props) { return }
    foreach ($prop in $props.PSObject.Properties) {
        if ($prop.Name -match '^PS') { continue }
        if ($prop.Value -match 'Discord') {
            Remove-ItemProperty -Path $runKey -Name $prop.Name -Force -ErrorAction SilentlyContinue
            Write-Ok "Removed startup entry: $($prop.Name)"
        }
    }

    $startupApproved = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run'
    if (Test-Path $startupApproved) {
        $approved = Get-ItemProperty $startupApproved -ErrorAction SilentlyContinue
        if ($approved) {
            foreach ($prop in $approved.PSObject.Properties) {
                if ($prop.Name -match '^PS') { continue }
                if ($prop.Name -match 'Discord') {
                    Remove-ItemProperty -Path $startupApproved -Name $prop.Name -Force -ErrorAction SilentlyContinue
                    Write-Ok "Removed startup approval: $($prop.Name)"
                }
            }
        }
    }
}

function Disable-DiscordScheduledTasks {
    try {
        # Discord only - matching plain 'Squirrel' would disable other apps'
        # updaters (Slack, GitHub Desktop, Teams classic all use Squirrel).
        $tasks = @(Get-ScheduledTask -ErrorAction SilentlyContinue |
            Where-Object { $_.TaskName -match 'Discord' -or $_.TaskPath -match 'Discord' })
        foreach ($task in $tasks) {
            Disable-ScheduledTask -TaskName $task.TaskName -TaskPath $task.TaskPath -ErrorAction SilentlyContinue | Out-Null
            Write-Ok "Disabled scheduled task: $($task.TaskPath)$($task.TaskName)"
        }
    } catch {
        Write-LogLine 'WARN' "Scheduled task cleanup skipped: $($_.Exception.Message)"
    }
}

function Set-DiscordWindowsNotificationsOff {
    # Quiet Windows: disable Discord toast banners (in-app Discord alerts still work).
    $base = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Notifications\Settings'
    if (-not (Test-Path $base)) { New-Item -Path $base -Force | Out-Null }

    $setOff = {
        param([string]$Id)
        $path = Join-Path $base $Id
        if (-not (Test-Path $path)) { New-Item -Path $path -Force | Out-Null }
        Set-ItemProperty -Path $path -Name 'Enabled' -Value 0 -Type DWord -Force
    }

    foreach ($id in @('Discord', 'Discord.Desktop', 'DiscordInc.Discord', 'com.squirrel.Discord.Discord')) {
        & $setOff $id
    }

    Get-ChildItem $base -ErrorAction SilentlyContinue |
        Where-Object { $_.PSChildName -match 'Discord' } |
        ForEach-Object {
            Set-ItemProperty -Path $_.PSPath -Name 'Enabled' -Value 0 -Type DWord -Force
            Write-Ok "Windows toasts off: $($_.PSChildName)"
        }
}

# Back-compat alias name used by older call sites
function Set-DiscordWindowsNotificationsOn {
    Set-DiscordWindowsNotificationsOff
}

function Set-DiscordTrayIconHidden([string]$AppDir) {
    $notifyKey = 'HKCU:\Control Panel\NotifyIconSettings'
    if (-not (Test-Path $notifyKey)) { return }

    $targets = @(
        (Join-Path $AppDir 'Discord.exe'),
        (Get-DiscOptEnvPath 'LOCALAPPDATA' 'Discord\Update.exe'),
        (Join-Path $AppDir 'Discord.bin.exe')
    ) | Where-Object { Test-Path $_ }

    $hidden = 0
    Get-ChildItem $notifyKey -ErrorAction SilentlyContinue | ForEach-Object {
        $props = Get-ItemProperty $_.PSPath -ErrorAction SilentlyContinue
        $path = $props.ExecutablePath
        if (-not $path) { return }
        foreach ($target in $targets) {
            if ($path -ieq $target -or $path -match [regex]::Escape('Discord')) {
                Set-ItemProperty -Path $_.PSPath -Name 'IsPromoted' -Value 0 -Type DWord -Force
                $hidden++
                break
            }
        }
    }

    if ($hidden -gt 0) { Write-Ok "Tray icon hidden ($hidden entries)" }
    else { Write-Warn 'Tray icon registry entry not found yet - launch once, then re-run' }
}

function Apply-WindowsTweaks([string]$AppDir) {
    Write-Step 'Applying Windows tweaks (notifications, tray, startup)...'
    Disable-DiscordWindowsAutostart
    Disable-DiscordScheduledTasks
    Set-DiscordWindowsNotificationsOff
    Set-DiscordTrayIconHidden $AppDir
    Write-Ok 'Windows tweaks applied (toasts OFF, tray hidden, no autostart)'
}

function Test-OpenAsarInstalled([string]$ResourcesDir) {
    $target = Join-Path $ResourcesDir '_app.asar'
    if (-not (Test-Path $target)) { return $false }
    $size = (Get-Item $target).Length
    return ($size -gt 10000 -and $size -lt 500000)
}

function Ensure-AsarStockBackup([string]$AppDir) {
    $resources = Join-Path $AppDir 'resources'
    $stockBackup = Join-Path $resources '_app.asar.stock'
    if (Test-Path $stockBackup) { return }

    $candidates = @(
        (Join-Path $resources 'app.asar.backup'),
        (Join-Path $resources '_app.asar'),
        (Join-Path $resources 'app.asar')
    )
    foreach ($src in $candidates) {
        if ((Test-Path $src) -and (Get-Item $src).Length -gt 1000000) {
            Copy-Item $src $stockBackup -Force
            Write-Ok 'Backed up stock bootstrap -> _app.asar.stock'
            return
        }
    }
    Write-Warn 'No _app.asar.stock backup yet'
}

function Install-OpenAsar([string]$AppDir) {
    Write-Step 'Installing OpenASAR (Equicord-compatible)...'
    $resources = Join-Path $AppDir 'resources'
    $target = Join-Path $resources '_app.asar'
    $stockBackup = Join-Path $resources '_app.asar.stock'

    if ($Quick -and (Test-OpenAsarInstalled $resources)) {
        Write-Ok 'OpenASAR already active on _app.asar (-Quick)'
        return
    }

    Ensure-AsarStockBackup $AppDir

    if (-not (Test-Path $target)) {
        throw 'Missing _app.asar - install Equicord loader first'
    }

    if (-not (Test-Path $stockBackup)) {
        if ((Get-Item $target).Length -gt 1000000) {
            Copy-Item $target $stockBackup -Force
            Write-Ok 'Backed up stock Discord bootstrap -> _app.asar.stock'
        }
    }

    $temp = Get-DiscOptTempPath 'discopt-openasar-app.asar'
    $bundled = Get-BundledOpenAsar
    if ($bundled) {
        Copy-Item $bundled $temp -Force
        Write-Ok "Using bundled OpenASAR from tools/ ($([math]::Round((Get-Item $bundled).Length / 1KB, 1)) KB)"
    } else {
        Invoke-WebRequest -Uri $OpenAsarUrl -OutFile $temp -UseBasicParsing -TimeoutSec 90
    }
    if ((Get-Item $temp).Length -lt 10000) {
        throw 'Downloaded OpenASAR app.asar looks invalid'
    }

    if (-not (Test-Path $ToolsDir)) { New-Item -ItemType Directory -Path $ToolsDir -Force | Out-Null }
    Copy-Item $temp (Join-Path $ToolsDir 'openasar.asar') -Force

    Write-DiscordResourceBytes -Path $target -Bytes ([IO.File]::ReadAllBytes($temp))
    Write-Ok "OpenASAR nightly installed ($([math]::Round((Get-Item $target).Length / 1KB, 1)) KB on _app.asar)"
}

function Unlock-DiscordSettings([string]$DestPath = '') {
    if (-not $DestPath) { $DestPath = Join-Path $AppData 'settings.json' }
    if (Test-Path $DestPath) { attrib -R $DestPath 2>$null }
}

function Get-DiscOptPowerShellExe {
    $found = Get-DiscOptPwsh77
    if ($found) { return $found.Exe }
    $pwsh = Get-Command pwsh -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($pwsh) { return $pwsh.Source }
    return (Get-DiscOptEnvPath 'SystemRoot' 'System32\WindowsPowerShell\v1.0\powershell.exe')
}

function Apply-DiscordProfile([string]$DestPath) {
    Write-Step 'Applying boot/optimizer flags (preserving your in-app settings)...'
    $profilePath = Join-Path $Profiles 'discord.json'
    if (-not (Test-Path $profilePath)) { throw 'Missing profiles/discord.json' }

    $kit = ConvertTo-HashtableDeep (Get-Content $profilePath -Raw -Encoding UTF8 | ConvertFrom-Json)
    $merged = @{}
    if (Test-Path $DestPath) {
        try {
            $merged = ConvertTo-HashtableDeep (Get-Content $DestPath -Raw -Encoding UTF8 | ConvertFrom-Json)
        } catch {}
    }

    # Strip noisy/debug keys Discord or older kits may leave behind.
    foreach ($drop in @(
        'DANGEROUS_ENABLE_DEVTOOLS_ONLY_ENABLE_IF_YOU_KNOW_WHAT_YOURE_DOING',
        'devTools',
        'OPENASAR_HARDCODED'
    )) {
        if ($merged.ContainsKey($drop)) { $merged.Remove($drop) }
    }

    # Kit keys we may stamp — do NOT force hardware acceleration or BACKGROUND_COLOR.
    # Equicord themes handle dark/AMOLED; OpenAsar must not inject CSS that paints pure black.
    $allowed = @(
        'SKIP_HOST_UPDATE', 'OPEN_ON_STARTUP', 'MINIMIZE_TO_TRAY', 'START_MINIMIZED',
        'IS_MAXIMIZED', 'IS_MINIMIZED', 'debugLogging', 'offloadAdmControls',
        'asyncVideoInputDeviceInit', 'DESKTOP_TTI_REMOVE_V8_CACHE_CLEAR',
        'DESKTOP_TTI_DNSTCP_WARMUP', 'DESKTOP_TTI_EARLY_UPDATE_CHECK',
        'DESKTOP_TTI_UPDATE_BACKOFF_MAX_MS',
        'audioSubsystem', 'useLegacyAudioDevice'
    )
    foreach ($key in $allowed) {
        if ($kit.Keys -contains $key) { $merged[$key] = $kit[$key] }
    }
    # Leave enableHardwareAcceleration alone (Discord default = on). Remove forced false from old kits.
    if ($merged.Keys -contains 'enableHardwareAcceleration' -and $merged['enableHardwareAcceleration'] -eq $false) {
        # Only strip OptiHub-forced false if user did not set it this session via kit profile
        if (-not ($kit.Keys -contains 'enableHardwareAcceleration')) {
            $merged.Remove('enableHardwareAcceleration')
            Write-LogLine 'OK' 'Hardware acceleration left at Discord default (not forced off)'
        }
    }

    # Conservative chromium flags only (no aggressive disable-features list).
    $merged.chromiumSwitches = @{
        'disable-breakpad'           = 1
        'disable-crash-reporter'     = 1
        'disable-domain-reliability' = 1
        'disable-logging'            = 1
    }
    if ($kit.openasar) {
        $merged.openasar = ConvertTo-HashtableDeep $kit.openasar
    } else {
        $merged.openasar = @{}
    }
    $merged.openasar.setup = $true
    # No cmdPreset=perf (blank client risk). No OpenAsar CSS — Equicord themes handle dark mode.
    if ($merged.openasar.Keys -contains 'cmdPreset') { $merged.openasar.Remove('cmdPreset') }
    if ($merged.openasar.Keys -contains 'css') { $merged.openasar.Remove('css') }
    $merged.openasar.quickstart = $false
    $merged.openasar.domOptimizer = $false
    $merged.openasar.themeSync = $false
    $merged.openasar.autoupdate = $false
    $merged.openasar.noTrack = $true
    $merged.openasar.noTyping = $true
    $merged.openasar.disableMediaKeys = $false

    # Stable boot flags (do not force BACKGROUND_COLOR — Equicord AMOLED theme owns look)
    $merged['DESKTOP_TTI_EARLY_UPDATE_CHECK'] = $false
    $merged['DESKTOP_TTI_DNSTCP_WARMUP'] = $false
    $merged['DESKTOP_TTI_REMOVE_V8_CACHE_CLEAR'] = $true
    $merged['audioSubsystem'] = 'standard'
    $merged['useLegacyAudioDevice'] = $false
    $merged['asyncVideoInputDeviceInit'] = $false
    $merged['debugLogging'] = $false
    $merged['OPEN_ON_STARTUP'] = $false
    if ($merged.Keys -contains 'BACKGROUND_COLOR') { $merged.Remove('BACKGROUND_COLOR') }

    # Never force host-update skip until modules are healthy - SKIP_HOST_UPDATE=true
    # with a broken installer.db freezes Discord on "Starting...".
    $activeForSkip = Get-ActiveApp
    if (-not $activeForSkip -or -not (Test-DiscordModulesReady $activeForSkip.FullName)) {
        $merged['SKIP_HOST_UPDATE'] = $false
        Write-LogLine 'OK' 'SKIP_HOST_UPDATE left false until modules are healthy'
    }

    $dir = Split-Path $DestPath -Parent
    if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    Unlock-DiscordSettings $DestPath

    Write-JsonFile $DestPath $merged 20
    Write-Ok 'Boot/optimizer flags applied (OpenASAR perf + chromium + standard audio)'
}

