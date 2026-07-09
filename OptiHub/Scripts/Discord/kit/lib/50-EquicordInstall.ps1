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
    Get-Process Discord, Discord.bin, Update -ErrorAction SilentlyContinue |
        Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 2

    Unlock-DiscordSettings
    Apply-DiscordProfile (Join-Path $AppData 'settings.json')
    # Always re-enable kernel on normal launch unless this run just rolled it back
    # or the user passed -SkipKernel. .disabled is only a soft temporary marker.
    if (-not $SkipKernel -and
        -not $Script:KernelRolledBack -and -not $Script:ModsRolledBack) {
        try {
            Install-DiscOptKernel $AppDir
        } catch {
            Write-Warn "Kernel re-enable on launch failed: $($_.Exception.Message)"
        }
    }

    [void](Invoke-DiscordLaunch -AppDir $AppDir)
}

function Wait-UserThenStartDiscord([string]$AppDir) {
    # Always skip under OptiHub; interactive restart is not used from the app.
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
    if ($Quick -or $env:DISCOPT_NONINTERACTIVE -eq '1' -or $env:OPTIHUB_SKIP_BOOT_FLASH -eq '1') {
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

    # UNIVERSAL: same profile on every machine. Stock Discord / fresh Equicord /
    # existing users all get the full manifest + OptiHub overrides written to disk.
    Write-Step 'Applying universal Equicord profile (all plugins for any PC)...'
    Write-HubProgress 62 'Applying Equicord profile...'
    Write-HubProgress 64 'Writing plugin settings...'

    $settingsDir = Join-Path $EquicordData 'settings'
    $themesDir = Join-Path $EquicordData 'themes'
    $destPath = Join-Path $settingsDir 'settings.json'
    if (-not (Test-Path $settingsDir)) { New-Item -ItemType Directory -Path $settingsDir -Force | Out-Null }
    if (-not (Test-Path $themesDir)) { New-Item -ItemType Directory -Path $themesDir -Force | Out-Null }

    # Refresh manifests when allowed so new Equicord plugins still get registered.
    Sync-PluginManifests

    # Full rebuild - do not trust an existing partial settings.json from a stock install.
    $settings = Build-FullEquicordSettings

    # Hard safety locks (always, every machine).
    $settings.autoUpdateNotification = $false
    $settings.eagerPatches = $true
    $settings.enableOnlineThemes = $false
    $settings.useQuickCss = $false
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

    foreach ($name in $ForceDisabledPlugins) {
        if (-not ($settings.plugins.Keys -contains $name)) { $settings.plugins[$name] = @{} }
        $settings.plugins[$name].enabled = $false
    }

    if (-not ($settings.plugins.Keys -contains 'StreamerModeOn')) { $settings.plugins['StreamerModeOn'] = @{} }
    $settings.plugins['StreamerModeOn'].enabled = $false

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
        Write-Warn "AMOLED theme missing from kit: $EnabledTheme"
    }

    $enabled = @($settings.plugins.Values | Where-Object { $_.enabled -eq $true }).Count
    $total = @($settings.plugins.Keys).Count
    Write-Ok "Universal profile written: $enabled / $total plugins enabled, AMOLED on"
    Write-Ok "Themes: $($settings.enabledThemes -join ', ')"
    Write-Ok "Settings: $destPath"
}

function New-EquicordLoaderAsar([string]$EquicordAsarPath) {
    $escaped = $EquicordAsarPath.Replace('\', '\\')
    $indexJs = "require(`"$escaped`")`n"
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
    $resources = Join-Path $AppDir 'resources'
    $loaderOk = Test-EquicordLoaderPatched $AppDir
    $openAsarOk = $SkipOpenAsar -or (Test-OpenAsarInstalled $resources)
    return ($loaderOk -and $openAsarOk)
}

function Install-EquicordDirect([string]$AppDir) {
    # Fast path only: bundled/cached Equicord asar + stub loader + OpenASAR.
    # Never calls Equilot (interactive CLI hangs OptiHub).
    $equicordAsar = Join-Path $EquicordData 'equicord.asar'
    if (-not (Test-Path $EquicordData)) { New-Item -ItemType Directory -Path $EquicordData -Force | Out-Null }

    Write-HubProgress 56 'Installing Equicord (fast)...'
    Write-Step 'Installing Equicord + OpenASAR (direct, no Equilot)...'
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

    $loaderLen = if (Test-Path $appAsar) { (Get-Item $appAsar).Length } else { 0 }
    if ($loaderLen -ge 64 -and $loaderLen -lt 4096) {
        Write-Ok 'Equicord loader already patched'
    } else {
        if ($loaderLen -gt 0 -and $loaderLen -lt 64) {
            Write-Warn "Equicord loader corrupt ($loaderLen bytes) - rewriting stub"
        }
        $backupAsar = Join-Path $resources '_app.asar'
        if (-not (Test-Path $backupAsar) -and (Test-Path $appAsar) -and (Get-Item $appAsar).Length -gt 1000000) {
            Copy-Item $appAsar $backupAsar -Force
        }
        Write-DiscordResourceBytes -Path $appAsar -Bytes (New-EquicordLoaderAsar $equicordAsar)
        Write-Ok 'Installed Equicord loader stub'
    }

    if (-not $SkipOpenAsar) {
        Write-HubProgress 66 'Installing OpenASAR...'
        Install-OpenAsar $AppDir
    } else {
        Write-Warn 'Skipped OpenASAR install (-SkipOpenAsar)'
    }

    Write-HubProgress 62 'Applying Equicord profile...'
    Apply-EquicordProfile -AppDir $AppDir

    if (-not (Test-EquicordReady $AppDir)) {
        throw 'Direct Equicord install did not verify (loader/OpenASAR check failed)'
    }
    Write-Ok 'Equicord + OpenASAR ready (direct path)'
}

function Install-Equicord([string]$AppDir) {
    Write-Step 'Verifying Equicord + OpenASAR...'
    Write-HubProgress 55 'Checking Equicord...'
    $resources = Join-Path $AppDir 'resources'
    $loaderOk = Test-EquicordLoaderPatched $AppDir
    $openOk = $SkipOpenAsar -or (Test-OpenAsarInstalled $resources)
    if ($loaderOk -and $openOk) {
        Write-Ok 'Equicord + OpenASAR already installed - applying tweaks only'
        Apply-EquicordProfile -AppDir $AppDir
        return
    }
    if (-not $loaderOk) {
        Write-Warn 'Equicord loader missing/corrupt - repairing via direct install'
    }

    Write-Step 'Equicord/OpenASAR missing - installing (fast path)...'
    Install-EquicordDirect $AppDir
}
