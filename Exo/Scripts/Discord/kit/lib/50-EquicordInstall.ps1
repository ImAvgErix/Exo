# 50-EquicordInstall.ps1 - Launch helpers + Equicord install
# Dot-sourced by Disc-Optimizer.ps1 (load order = filename sort).
# Universal multi-PC kit - do not assume Equicord/Discord already configured.

function Invoke-DiscordLaunch {
    param(
        [string]$AppDir,
        [string[]]$ExtraArgs = @('-disable-logging', '-log-level=3')
    )

    $argStr = ($ExtraArgs | Where-Object { $_ }) -join ' '

    # Launch Discord.exe directly - it is the reliable path. Update.exe
    # -processStart depends on Squirrel state (RELEASES/installer.db) and
    # exits silently when that state is unhappy.
    if (-not $AppDir) {
        $active = Get-ActiveApp
        if ($active) { $AppDir = $active.FullName }
    }
    $exe = if ($AppDir) { Join-Path $AppDir 'Discord.exe' } else { $null }
    if ($exe -and (Test-Path $exe)) {
        $psi = New-Object System.Diagnostics.ProcessStartInfo
        $psi.FileName = $exe
        $psi.WorkingDirectory = $AppDir
        $psi.Arguments = $argStr
        $psi.UseShellExecute = $true
        return [System.Diagnostics.Process]::Start($psi)
    }

    $updateExe = Join-Path $DiscordRoot 'Update.exe'
    if (Test-Path $updateExe) {
        $psi = New-Object System.Diagnostics.ProcessStartInfo
        $psi.FileName = $updateExe
        if ($argStr) {
            $psi.Arguments = "-processStart Discord.exe -process-start-args `"$argStr`""
        } else {
            $psi.Arguments = '-processStart Discord.exe'
        }
        $psi.WorkingDirectory = $DiscordRoot
        $psi.UseShellExecute = $true
        return [System.Diagnostics.Process]::Start($psi)
    }

    throw "Discord.exe not found in $AppDir and Update.exe missing"
}

function Start-Discord([string]$AppDir) {
    # Fast path: do not rewrite settings.json or reinstall the kernel on every
    # launch - that was the hitch. Heal is Discord.vbs / -Launch when files missing.
    if (-not $AppDir) {
        $active = Get-ActiveApp
        if ($active) { $AppDir = $active.FullName }
    }
    if (-not $AppDir) { throw 'No Discord app folder to launch' }

    # Soft re-enable only if kernel was renamed to .disabled (rollback marker).
    if (-not $SkipKernel -and -not $Script:KernelRolledBack -and -not $Script:ModsRolledBack) {
        $verDisabled = Join-Path $AppDir 'version.dll.disabled'
        if (Test-Path -LiteralPath $verDisabled) {
            try { Install-DiscOptKernel $AppDir } catch {
                Write-Warn "Kernel re-enable on launch failed: $($_.Exception.Message)"
            }
        }
    }

    [void](Invoke-DiscordLaunch -AppDir $AppDir)
}

function Wait-UserThenStartDiscord([string]$AppDir) {
    # Always skip under Exo; interactive restart is not used from the app.
    Write-Ok 'Skipping interactive Discord restart prompt'
    Write-HubProgress 98 'Finishing...'
}

function Write-JsonFile([string]$Path, $Object, [int]$Depth = 20) {
    $dir = Split-Path $Path -Parent
    if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    $json = $Object | ConvertTo-Json -Depth $Depth -Compress:$false
    [System.IO.File]::WriteAllText($Path, $json, [System.Text.UTF8Encoding]::new($false))
}

function Merge-HashtableDeep([hashtable]$Base, [hashtable]$Overlay) {
    foreach ($key in @($Overlay.Keys)) {
        $val = $Overlay[$key]
        if ($val -is [hashtable] -and ($Base.Keys -contains $key) -and $Base[$key] -is [hashtable]) {
            Merge-HashtableDeep $Base[$key] $val
        } else {
            $Base[$key] = $val
        }
    }
}

function Get-EquicordSettingsHealth([string]$Path) {
    $result = @{
        Healthy = $false
        Reason  = 'missing'
        Size    = 0
        Plugins = 0
        Enabled = 0
        HasBom  = $false
    }
    if (-not (Test-Path $Path)) { return $result }

    $result.Size = (Get-Item $Path).Length
    $bytes = [System.IO.File]::ReadAllBytes($Path)
    $result.HasBom = ($bytes.Length -ge 3 -and $bytes[0] -eq 239 -and $bytes[1] -eq 187 -and $bytes[2] -eq 191)
    if ($result.HasBom) { $result.Reason = 'utf8-bom'; return $result }
    if ($result.Size -lt 8000) { $result.Reason = 'too-small'; return $result }

    try {
        $s = Get-Content $Path -Raw -Encoding UTF8 | ConvertFrom-Json
        if (-not $s.plugins) { $result.Reason = 'no-plugins'; return $result }
        $props = @($s.plugins.PSObject.Properties)
        $result.Plugins = $props.Count
        $result.Enabled = (@($props | Where-Object { $_.Value.enabled -eq $true })).Count
        if ($result.Plugins -lt 200) { $result.Reason = 'plugin-count-low'; return $result }
        if ($props.Name -notcontains 'NoTrack') { $result.Reason = 'missing-notrack'; return $result }
        $result.Healthy = $true
        $result.Reason = 'ok'
    } catch {
        $result.Reason = 'parse-error'
    }
    return $result
}

function Test-EquicordSettingsHealthy([string]$Path) {
    return (Get-EquicordSettingsHealth $Path).Healthy
}

function Initialize-EquicordSettingsBase([string]$DestPath) {
    if (Test-EquicordSettingsHealthy $DestPath) {
        return ConvertTo-HashtableDeep (Get-Content $DestPath -Raw -Encoding UTF8 | ConvertFrom-Json)
    }

    # Quick / non-interactive: never launch Discord just to seed settings - use bundled manifests.
    if ($Quick -or $env:DISCOPT_NONINTERACTIVE -eq '1' -or $env:EXO_SKIP_BOOT_FLASH -eq '1') {
        Write-Step 'Building Equicord settings from bundled manifests (no Discord launch)...'
        Write-Warn 'Skipping Discord bootstrap launch in Quick/non-interactive mode'
        return Build-FullEquicordSettings
    }

    Write-Step 'Bootstrapping Equicord plugin registry (one quick launch)...'
    [void](Invoke-DiscordLaunch -AppDir (Get-ActiveApp).FullName)
    Start-Sleep -Seconds 12
    Stop-Discord

    if (Test-EquicordSettingsHealthy $DestPath) {
        return ConvertTo-HashtableDeep (Get-Content $DestPath -Raw -Encoding UTF8 | ConvertFrom-Json)
    }

    Write-Warn 'Using bundled manifests for settings base'
    return Build-FullEquicordSettings
}
function Apply-EquicordProfile {
    param([string]$AppDir = '')

    # Prefer preserving a working lean Equicord profile. Full rebuilds only when
    # settings are missing/corrupt. eagerPatches=true blanks Discord 1.0.9245.
    Write-Step 'Applying Equicord profile (lean overrides; preserve healthy settings)...'
    Write-HubProgress 62 'Applying Equicord profile...'
    Write-HubProgress 64 'Writing plugin settings...'

    $settingsDir = Join-Path $EquicordData 'settings'
    $themesDir = Join-Path $EquicordData 'themes'
    $destPath = Join-Path $settingsDir 'settings.json'
    if (-not (Test-Path $settingsDir)) { New-Item -ItemType Directory -Path $settingsDir -Force | Out-Null }
    if (-not (Test-Path $themesDir)) { New-Item -ItemType Directory -Path $themesDir -Force | Out-Null }

    # Refresh manifests when allowed so new Equicord plugins still get registered.
    Sync-PluginManifests

    $settings = $null
    if (Test-Path $destPath) {
        try {
            $existing = ConvertTo-HashtableDeep (Get-Content $destPath -Raw -Encoding UTF8 | ConvertFrom-Json)
            $bytes = (Get-Item $destPath).Length
            $pluginCount = if ($existing.plugins) { @($existing.plugins.Keys).Count } else { 0 }
            if ($bytes -gt 200 -and $pluginCount -gt 0) {
                $settings = $existing
                Write-Ok "Preserving existing Equicord settings ($pluginCount plugins, $([math]::Round($bytes/1KB,1)) KB)"
            }
        } catch {
            Write-Warn "Could not read existing Equicord settings - rebuilding"
        }
    }
    if (-not $settings) {
        $settings = Build-FullEquicordSettings
    }

    # Hard safety locks (always, every machine).
    $settings.autoUpdateNotification = $false
    $settings.eagerPatches = $false
    $settings.enableOnlineThemes = $false
    $settings.useQuickCss = $true
    $settings.enableReactDevtools = $false
    $settings.mainWindowFrameless = $false
    $settings.frameless = $false
    $settings.transparent = $false
    $settings.windowsMaterial = 'none'
    $settings.winNativeTitleBar = $false
    if (-not $settings.cloud) { $settings.cloud = @{} }
    $settings.cloud.settingsSync = $false
    $settings.cloud.authenticated = $false
    $settings.cloud.url = 'https://cloud.equicord.org/'
    $settings.enabledThemes = @($EnabledTheme)

    # Enforce a measured plugin budget instead of preserving an arbitrarily heavy
    # old profile. Required plugins and transitive dependencies are added from the
    # bundled manifests; everything else is disabled but its options are retained.
    $leanPolicy = Get-EquicordLeanPolicy
    $leanAllowed = Get-EquicordLeanAllowedNames -Policy $leanPolicy
    foreach ($name in @($settings.plugins.Keys)) {
        if (-not $leanAllowed.Contains([string]$name)) { $settings.plugins[$name].enabled = $false }
    }
    $overridesPath = Join-Path $Profiles 'equicord-overrides.json'
    $overridePlugins = @{}
    if (Test-Path -LiteralPath $overridesPath) {
        $overrideRoot = ConvertTo-HashtableDeep (Get-Content -LiteralPath $overridesPath -Raw -Encoding UTF8 | ConvertFrom-Json)
        if ($overrideRoot.plugins) { $overridePlugins = $overrideRoot.plugins }
    }
    foreach ($name in @($leanAllowed)) {
        if (-not ($settings.plugins.Keys -contains $name)) { $settings.plugins[$name] = @{} }
        if ($overridePlugins.ContainsKey($name)) {
            foreach ($option in @($overridePlugins[$name].Keys)) {
                $settings.plugins[$name][$option] = ConvertTo-HashtableDeep $overridePlugins[$name][$option]
            }
        }
        $settings.plugins[$name].enabled = $true
    }

    foreach ($name in $ForceDisabledPlugins) {
        if ($leanAllowed.Contains([string]$name)) { continue }
        if (-not ($settings.plugins.Keys -contains $name)) { $settings.plugins[$name] = @{} }
        $settings.plugins[$name].enabled = $false
    }

    if (-not ($settings.plugins.Keys -contains 'StreamerModeOn')) { $settings.plugins['StreamerModeOn'] = @{} }
    $settings.plugins['StreamerModeOn'].enabled = $false

    # Always restore member-list role headers (even when preserving an older profile that enabled NoRoleHeaders).
    if (-not ($settings.plugins.Keys -contains 'NoRoleHeaders')) { $settings.plugins['NoRoleHeaders'] = @{} }
    $settings.plugins['NoRoleHeaders'].enabled = $false

    if (-not ($settings.plugins.Keys -contains 'NotificationVolume')) { $settings.plugins['NotificationVolume'] = @{} }
    $settings.plugins['NotificationVolume'].enabled = $true
    $settings.plugins['NotificationVolume'].notificationVolume = 25

    if (-not ($settings.plugins.Keys -contains 'NoTrack')) { $settings.plugins['NoTrack'] = @{} }
    $settings.plugins['NoTrack'].enabled = $true
    $settings.plugins['NoTrack'].disableAnalytics = $true

    Write-JsonFile $destPath $settings 30

    # Broken custom themes from older kits.
    Get-ChildItem $themesDir -Filter 'discopt-amoled*.theme.css' -ErrorAction SilentlyContinue |
        ForEach-Object {
            Remove-Item $_.FullName -Force -ErrorAction SilentlyContinue
            Write-Ok "Removed broken theme: $($_.Name)"
        }

    $themeSrc = Join-Path $Themes $EnabledTheme
    if (Test-Path $themeSrc) {
        Copy-Item $themeSrc (Join-Path $themesDir $EnabledTheme) -Force
    } else {
        Write-Warn "Dark theme missing from kit: $EnabledTheme"
    }

    $enabled = @($settings.plugins.Values | Where-Object { $_.enabled -eq $true }).Count
    $total = @($settings.plugins.Keys).Count
    if ($enabled -gt [int]$leanPolicy.maximumEnabled) {
        throw "Lean plugin budget exceeded ($enabled > $($leanPolicy.maximumEnabled))"
    }
    Write-Ok "Universal profile written: $enabled / $total plugins enabled, dark mode on"
    Write-Ok "Themes: $($settings.enabledThemes -join ', ')"
    Write-Ok "Settings: $destPath"
}

function New-EquicordLoaderAsar([string]$EquicordAsarPath) {
    # Exo Host bootstrap: lean env, then Equicord (actively maintained client).
    # Replaces OpenAsar - we do not ship a fragile full desktop-shell rewrite.
    $escaped = $EquicordAsarPath.Replace('\', '\\')
    # Keep the stub minimal - do NOT set DISCORD_USER_DATA_DIR (breaks %AppData%\discord).
    $indexJs = @(
        '// Exo Host - Equicord bootstrap (no OpenAsar)'
        'try { process.env.ELECTRON_NO_ATTACH_CONSOLE = "1"; } catch (e) {}'
        "require(`"$escaped`");"
        ''
    ) -join "`n"
    $packageJson = "{`n`t`"name`": `"discord`",`n`t`"main`": `"index.js`"`n}"
    $indexBytes = [Text.Encoding]::UTF8.GetBytes($indexJs)
    $pkgBytes = [Text.Encoding]::UTF8.GetBytes($packageJson)
    $json = '{"files":{"index.js":{"size":' + $indexBytes.Length + ',"offset":"0"},"package.json":{"size":' + $pkgBytes.Length + ',"offset":"' + $indexBytes.Length + '"}}}'
    $jsonBytes = [Text.Encoding]::UTF8.GetBytes($json)
    $jsonPad = (4 - ($jsonBytes.Length % 4)) % 4
    $ms = [IO.MemoryStream]::new()
    $bw = [IO.BinaryWriter]::new($ms)
    $bw.Write([uint32]4)
    $bw.Write([uint32](8 + $jsonBytes.Length))
    $bw.Write([uint32]($jsonBytes.Length + 4))
    $bw.Write([uint32]$jsonBytes.Length)
    $bw.Write($jsonBytes)
    for ($i = 0; $i -lt $jsonPad; $i++) { $bw.Write([byte]0) }
    $bw.Write($indexBytes)
    $bw.Write($pkgBytes)
    $bw.Close()
    return $ms.ToArray()
}

function Test-EquicordReady([string]$AppDir) {
    # Applied = Equicord loader + Exo Host flags path (OpenAsar no longer required).
    return (Test-EquicordLoaderPatched $AppDir)
}

function Install-EquicordDirect([string]$AppDir) {
    # Fast path: Equicord asar + Exo Host loader + profile/theme/plugins.
    # Never calls Equilot (interactive CLI hangs Exo). Never installs OpenAsar.
    $equicordAsar = Join-Path $EquicordData 'equicord.asar'
    if (-not (Test-Path $EquicordData)) { New-Item -ItemType Directory -Path $EquicordData -Force | Out-Null }

    Write-HubProgress 56 'Installing Equicord (fast)...'
    Write-Step 'Installing Equicord + Exo Host (direct, no OpenAsar)...'
    Stop-Discord

    $dl = Resolve-EquicordDesktopAsar $equicordAsar
    if ($dl.Size -lt 1000000) { throw 'Equicord desktop.asar looks invalid (too small)' }
    $tagLabel = switch ($dl.Source) {
        'tools'   { 'bundled (tools/)' }
        'cache'   { 'cached' }
        'direct'  { 'latest (direct)' }
        'api'     { $dl.Tag }
        default   { $dl.Tag }
    }
    Write-Ok "Equicord $tagLabel ($([math]::Round($dl.Size / 1MB, 1)) MB)"

    $resources = Join-Path $AppDir 'resources'
    $appAsar = Join-Path $resources 'app.asar'
    if (-not (Test-Path $resources) -or -not (Test-Path $appAsar)) {
        Write-Warn "Discord resources missing under $AppDir - repairing Discord install..."
        Write-HubProgress 30 'Repairing Discord resources...'
        Remove-DiscordInstall
        Invoke-DiscordSetupSilent
        $repaired = Get-ActiveApp
        if (-not $repaired) { throw 'Discord reinstall failed - no app-* folder' }
        Invoke-SquirrelFirstRun $repaired.FullName
        if (-not (Test-DiscordModulesReady $repaired.FullName)) {
            Initialize-DiscordModules $repaired.FullName
        }
        Stop-Discord
        $AppDir = $repaired.FullName
        $resources = Join-Path $AppDir 'resources'
        $appAsar = Join-Path $resources 'app.asar'
        if (-not (Test-Path $resources) -or -not (Test-Path $appAsar)) {
            throw "Discord still missing resources after reinstall: $resources"
        }
        Write-Ok "Discord resources restored ($($repaired.Name))"
    }

    Ensure-AsarStockBackup $AppDir
    Remove-LegacyOpenAsar $AppDir

    # Equilotl / Equicord require stock Discord desktop as _app.asar (large).
    # app.asar becomes the tiny require("equicord.asar") stub. Missing _app.asar
    # shows a bare "Error" window on modern Discord hosts.
    $bootstrap = Join-Path $resources '_app.asar'
    $stock = Join-Path $resources '_app.asar.stock'
    $loaderLen = if (Test-Path $appAsar) { (Get-Item $appAsar).Length } else { 0 }
    if ($loaderLen -gt 1000000) {
        Copy-Item -LiteralPath $appAsar -Destination $bootstrap -Force
        if (-not (Test-Path -LiteralPath $stock)) {
            Copy-Item -LiteralPath $appAsar -Destination $stock -Force
        }
        Write-Ok 'Stock Discord shell moved to _app.asar (Equicord layout)'
    } elseif (-not (Test-Path -LiteralPath $bootstrap) -or ((Get-Item $bootstrap).Length -lt 1000000)) {
        if (Test-Path -LiteralPath $stock) {
            Copy-Item -LiteralPath $stock -Destination $bootstrap -Force
            Write-Ok 'Restored stock shell to _app.asar from backup'
        } else {
            throw 'Missing stock Discord app.asar for Equicord (_app.asar). Reinstall Discord, then re-run Exo Discord Apply.'
        }
    }

    Write-DiscordResourceBytes -Path $appAsar -Bytes (New-EquicordLoaderAsar $equicordAsar)
    Write-Ok 'Installed Exo Host Equicord loader (app.asar stub)'

    Write-HubProgress 66 'Exo Host flags...'
    Install-ExoHost $AppDir

    Write-HubProgress 62 'Applying Equicord profile (theme + plugins)...'
    Apply-EquicordProfile -AppDir $AppDir

    if (-not (Test-EquicordReady $AppDir)) {
        throw 'Direct Equicord install did not verify (loader check failed)'
    }
    Write-Ok 'Equicord + Exo Host ready (theme/plugins applied; no OpenAsar)'
}

function Install-EquicordViaEquilotl([string]$DiscordRoot) {
    # Official Equicord installer (non-interactive). Produces the correct
    # app.asar stub + large stock _app.asar layout modern Discord needs.
    $cli = Join-Path $ToolsDir 'EquilotlCli.exe'
    if (-not (Test-Path -LiteralPath $cli)) {
        $url = 'https://github.com/Equicord/Equilotl/releases/latest/download/EquilotlCli.exe'
        Write-Step 'Downloading Equilotl CLI (official Equicord installer)...'
        try {
            if (-not (Test-Path -LiteralPath $ToolsDir)) {
                New-Item -ItemType Directory -Path $ToolsDir -Force | Out-Null
            }
            Invoke-WebRequest -Uri $url -OutFile $cli -UseBasicParsing -TimeoutSec 120
        } catch {
            Write-Warn "Equilotl download failed: $($_.Exception.Message)"
            return $false
        }
    }
    if (-not (Test-Path -LiteralPath $cli) -or (Get-Item $cli).Length -lt 1MB) { return $false }
    Stop-Discord
    Write-Step 'Installing Equicord via Equilotl (no OpenAsar)...'
    $p = Start-Process -FilePath $cli -ArgumentList @('-install', '-location', $DiscordRoot) -Wait -PassThru -NoNewWindow
    if ($p.ExitCode -ne 0) {
        Write-Warn "Equilotl exit $($p.ExitCode) - will try direct path"
        return $false
    }
    return $true
}

function Install-Equicord([string]$AppDir) {
    Write-Step 'Verifying Equicord + Exo Host...'
    Write-HubProgress 55 'Checking Equicord...'
    $loaderOk = Test-EquicordLoaderPatched $AppDir
    if ($loaderOk) {
        Write-Ok 'Equicord loader present - refreshing host + profile'
        Remove-LegacyOpenAsar $AppDir
        # Keep large stock on _app.asar (Equicord needs it)
        $resources = Join-Path $AppDir 'resources'
        $bootstrap = Join-Path $resources '_app.asar'
        $stock = Join-Path $resources '_app.asar.stock'
        if ((-not (Test-Path $bootstrap) -or (Get-Item $bootstrap).Length -lt 1000000) -and (Test-Path $stock)) {
            Copy-Item $stock $bootstrap -Force
            Write-Ok 'Restored stock shell on _app.asar for Equicord'
        }
        Install-ExoHost $AppDir
        Apply-EquicordProfile -AppDir $AppDir
        return
    }
    Write-Warn 'Equicord loader missing - trying Equilotl then direct path'
    # AppDir is ...\Discord\app-1.0.xxxx - Equilotl wants the Discord root (Update.exe parent), NOT LocalAppData.
    $root = Split-Path -Parent $AppDir
    if (-not (Test-Path -LiteralPath (Join-Path $root 'Update.exe'))) {
        # Fallback: walk up one more only if this still looks like a Discord tree.
        $maybe = Split-Path -Parent $root
        if ($maybe -and (Test-Path -LiteralPath (Join-Path $maybe 'Update.exe'))) { $root = $maybe }
    }
    if ($root -and (Test-Path -LiteralPath (Join-Path $root 'Update.exe')) -and (Install-EquicordViaEquilotl $root)) {
        if (Test-EquicordLoaderPatched $AppDir) {
            Remove-LegacyOpenAsar $AppDir
            Install-ExoHost $AppDir
            Apply-EquicordProfile -AppDir $AppDir
            Write-Ok 'Equicord installed via Equilotl + Exo Host profile'
            return
        }
        # App folder may have been recreated
        $active = Get-ActiveApp
        if ($active -and (Test-EquicordLoaderPatched $active.FullName)) {
            Install-ExoHost $active.FullName
            Apply-EquicordProfile -AppDir $active.FullName
            Write-Ok 'Equicord installed via Equilotl + Exo Host profile'
            return
        }
    }
    Install-EquicordDirect $AppDir
}
