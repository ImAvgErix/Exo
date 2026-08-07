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

    # Always a full lean rebuild from policy. eagerPatches=true blanks Discord 1.0.9245.
    Write-Step 'Rebuilding Equicord lean profile (your prior plugin selection is replaced)...'
    Write-HubProgress 62 'Applying Equicord profile...'
    Write-HubProgress 64 'Writing plugin settings...'

    $settingsDir = Join-Path $EquicordData 'settings'
    $themesDir = Join-Path $EquicordData 'themes'
    $destPath = Join-Path $settingsDir 'settings.json'
    if (-not (Test-Path $settingsDir)) { New-Item -ItemType Directory -Path $settingsDir -Force | Out-Null }
    if (-not (Test-Path $themesDir)) { New-Item -ItemType Directory -Path $themesDir -Force | Out-Null }

    # Refresh manifests when allowed so new Equicord plugins still get registered.
    Sync-PluginManifests

    # Disc-Optimizer sets EXO_EXPERIMENTAL unconditionally, so the "preserve a healthy existing
    # profile" branch that used to sit here could never execute - not once, on any machine. The
    # step nevertheless announced "preserve healthy settings" while replacing them, and the
    # unreachable branch carried a comment explaining that rewriting a user's plugins "felt like
    # Apply reset my Discord settings". It does do that; it always has.
    #
    # Deleted rather than resurrected. A path that has never run in production has also never
    # been tested against the lean-policy writer below, and quietly enabling it now would change
    # what Apply does to every existing install. The behaviour is unchanged; only the claim is.
    # One pass, three owners, no overlap:
    #   Build-FullEquicordSettings - global settings and per-plugin options (safety clamps in code)
    #   lean-plugin-policy.json    - which plugins are enabled, via Get-EquicordLeanAllowedNames
    #   this loop                  - stamps that decision onto every catalogue entry
    #
    # The ~90 lines that used to follow re-set eleven globals Build-FullEquicordSettings had
    # already set, walked a 50-name ForceDisabledPlugins list whose every member the lean pass
    # below had already disabled, and hand-pinned six plugins (StreamerModeOn, NoRoleHeaders,
    # NotificationVolume, NoTrack, FakeNitro, LimitlessScreenshare) to states the policy and
    # overrides already produced. Four owners of the same decision, agreeing by coincidence.
    Write-Ok 'Rebuilding Equicord lean profile from policy (prior plugin selection not preserved)'
    $settings = Build-FullEquicordSettings
    $leanPolicy = Get-EquicordLeanPolicy
    $leanAllowed = Get-EquicordLeanAllowedNames -Policy $leanPolicy

    if (-not $settings.plugins) { $settings.plugins = @{} }
    foreach ($name in @($settings.plugins.Keys)) {
        $settings.plugins[$name].enabled = $leanAllowed.Contains([string]$name)
    }
    foreach ($name in @($leanAllowed)) {
        if (-not ($settings.plugins.Keys -contains $name)) {
            $settings.plugins[$name] = @{ enabled = $true }
        }
    }

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

    # The Exocord layer. Equicord has always been told useQuickCss=true and nothing in this
    # kit had ever written quickCss.css, so the slot sat enabled and zero bytes on every
    # machine - a whole styling surface switched on and left empty.
    #
    # Written as a managed block between markers so a user's own QuickCSS below it survives
    # every apply. Exo replaces its own block and touches nothing else.
    $quickCssPath = Join-Path $settingsDir 'quickCss.css'
    $exocordCss = Join-Path $Themes 'exocord.quickcss.css'
    if (Test-Path -LiteralPath $exocordCss) {
        $beginMarker = '/* ==== EXOCORD LAYER - MANAGED BY EXO, EDITS BELOW THE END MARKER ARE KEPT ==== */'
        $endMarker = '/* ==== END EXOCORD LAYER ==== */'
        $userCss = ''
        if (Test-Path -LiteralPath $quickCssPath) {
            $existing = [IO.File]::ReadAllText($quickCssPath)
            $endIndex = $existing.IndexOf($endMarker)
            if ($endIndex -ge 0) {
                $userCss = $existing.Substring($endIndex + $endMarker.Length).TrimStart("`r", "`n")
            } elseif ($existing.Trim()) {
                # Pre-existing hand-written QuickCSS from before Exo managed this file.
                $userCss = $existing.TrimStart("`r", "`n")
            }
        }
        $block = @(
            $beginMarker
            ([IO.File]::ReadAllText($exocordCss)).TrimEnd()
            $endMarker
        ) -join "`n"
        $final = if ($userCss.Trim()) { $block + "`n`n" + $userCss } else { $block + "`n" }
        [IO.File]::WriteAllText($quickCssPath, $final, [Text.UTF8Encoding]::new($false))
        if ($userCss.Trim()) {
            Write-Ok "Exocord layer written (your own QuickCSS below the end marker kept)"
        } else {
            Write-Ok 'Exocord layer written to quickCss.css'
        }
    } else {
        Write-Warn 'Exocord layer missing from kit: exocord.quickcss.css'
    }

    $enabled = @($settings.plugins.Values | Where-Object { $_.enabled -eq $true }).Count
    $total = @($settings.plugins.Keys).Count
    # Every rebuild is a lean rebuild now, so the budget always applies. The condition that used
    # to guard this could only ever be true.
    if ($enabled -gt [int]$leanPolicy.maximumEnabled) {
        throw "Lean plugin budget exceeded ($enabled > $($leanPolicy.maximumEnabled))"
    }
    Write-Ok "Universal profile written: $enabled / $total plugins enabled, dark mode on"
    Write-Ok "Themes: $($settings.enabledThemes -join ', ')"
    Write-Ok "Settings: $destPath"
}

function New-EquicordLoaderAsar([string]$EquicordAsarPath) {
    # Exocord Host bootstrap - byte-compatible with official Equilotl stubs.
    #
    # CRITICAL: Electron's asar reader expects the classic pickle header with NO
    # padding between the JSON header and file payloads. An earlier kit version
    # aligned the JSON to 4 bytes and left file offsets at 0/N, which made every
    # "direct" install write a 500-byte app.asar Discord refused to boot (exit 1).
    # Equilotl's own 216-byte stub (require only, no pad) boots; match that layout.
    #
    # Keep the stub minimal - do NOT set DISCORD_USER_DATA_DIR (breaks %AppData%\discord).
    # Match Equilotl package.json bytes exactly (tab-indented, trailing newline).
    $escaped = $EquicordAsarPath.Replace('\', '\\')
    $indexJs = "require(`"$escaped`")"
    $packageJson = "{`n`t`"name`": `"discord`",`n`t`"main`": `"index.js`"`n}"
    $indexBytes = [Text.Encoding]::UTF8.GetBytes($indexJs)
    $pkgBytes = [Text.Encoding]::UTF8.GetBytes($packageJson)
    $json = '{"files":{"index.js":{"size":' + $indexBytes.Length + ',"offset":"0"},"package.json":{"size":' + $pkgBytes.Length + ',"offset":"' + $indexBytes.Length + '"}}}'
    $jsonBytes = [Text.Encoding]::UTF8.GetBytes($json)
    $ms = [IO.MemoryStream]::new()
    $bw = [IO.BinaryWriter]::new($ms)
    # Electron asar pickle header (same 16-byte prelude Equilotl writes):
    #   u32 4 | u32 (8+jsonLen) | u32 (jsonLen+4) | u32 jsonLen | json | files
    $bw.Write([uint32]4)
    $bw.Write([uint32](8 + $jsonBytes.Length))
    $bw.Write([uint32]($jsonBytes.Length + 4))
    $bw.Write([uint32]$jsonBytes.Length)
    $bw.Write($jsonBytes)
    $bw.Write($indexBytes)
    $bw.Write($pkgBytes)
    $bw.Close()
    return $ms.ToArray()
}

function Test-EquicordLoaderAsarBytes([byte[]]$Bytes, [string]$EquicordAsarPath = '') {
    # Reject the broken padded stubs and empty/corrupt loaders before Discord is opened.
    if ($null -eq $Bytes -or $Bytes.Length -lt 64 -or $Bytes.Length -ge 4096) { return $false }
    if ($Bytes.Length -lt 16) { return $false }
    # Header must start with classic pickle size field = 4
    if ([BitConverter]::ToUInt32($Bytes, 0) -ne 4) { return $false }
    $jsonLen = [BitConverter]::ToUInt32($Bytes, 12)
    if ($jsonLen -lt 40 -or $jsonLen -gt 2000) { return $false }
    if ((16 + $jsonLen) -gt $Bytes.Length) { return $false }
    $json = [Text.Encoding]::UTF8.GetString($Bytes, 16, [int]$jsonLen)
    if ($json -notmatch '"index\.js"' -or $json -notmatch '"package\.json"') { return $false }
    $payload = [Text.Encoding]::UTF8.GetString($Bytes, 16 + [int]$jsonLen, $Bytes.Length - 16 - [int]$jsonLen)
    if ($payload -notmatch 'require\(') { return $false }
    if ($EquicordAsarPath) {
        $needle = $EquicordAsarPath.Replace('\', '\\')
        if ($payload.IndexOf($needle, [StringComparison]::OrdinalIgnoreCase) -lt 0 -and
            $payload.IndexOf('equicord.asar', [StringComparison]::OrdinalIgnoreCase) -lt 0) {
            return $false
        }
    }
    return $true
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
    $loaderLen = Get-DiscordFileLength $appAsar
    if ($loaderLen -gt 1000000) {
        Copy-Item -LiteralPath $appAsar -Destination $bootstrap -Force
        if (-not (Test-Path -LiteralPath $stock)) {
            Copy-Item -LiteralPath $appAsar -Destination $stock -Force
        }
        Write-Ok 'Stock Discord shell moved to _app.asar (Equicord layout)'
    } elseif ((Get-DiscordFileLength $bootstrap) -lt 1000000) {
        if (Test-Path -LiteralPath $stock) {
            Copy-Item -LiteralPath $stock -Destination $bootstrap -Force
            Write-Ok 'Restored stock shell to _app.asar from backup'
        } else {
            throw 'Missing stock Discord app.asar for Equicord (_app.asar). Reinstall Discord, then re-run Exo Discord Apply.'
        }
    }

    $loaderBytes = New-EquicordLoaderAsar $equicordAsar
    if (-not (Test-EquicordLoaderAsarBytes $loaderBytes $equicordAsar)) {
        throw 'Generated Equicord loader asar failed self-check (refusing to write)'
    }
    Write-DiscordResourceBytes -Path $appAsar -Bytes $loaderBytes
    $written = [IO.File]::ReadAllBytes($appAsar)
    if (-not (Test-EquicordLoaderAsarBytes $written $equicordAsar)) {
        throw 'Wrote Equicord loader but on-disk app.asar failed validation'
    }
    Write-Ok "Installed Exocord Host loader (app.asar stub, $($written.Length) bytes)"

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
    # Always redirect stdio: without it Equilotl can fall into the interactive
    # menu (exit 1 / hang), which is what made Exo fall through to the direct
    # path and previously write a broken stub.
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
    $outLog = Join-Path ([IO.Path]::GetTempPath()) ('equilotl-out-' + [guid]::NewGuid().ToString('N') + '.txt')
    $errLog = Join-Path ([IO.Path]::GetTempPath()) ('equilotl-err-' + [guid]::NewGuid().ToString('N') + '.txt')
    try {
        $args = @('-install', '-branch', 'stable', '-location', $DiscordRoot)
        $p = Start-Process -FilePath $cli -ArgumentList $args -Wait -PassThru -NoNewWindow `
            -RedirectStandardOutput $outLog -RedirectStandardError $errLog
        $exit = if ($null -ne $p) { $p.ExitCode } else { -1 }
        $combined = @()
        if (Test-Path -LiteralPath $outLog) { $combined += @(Get-Content -LiteralPath $outLog -ErrorAction SilentlyContinue) }
        if (Test-Path -LiteralPath $errLog) { $combined += @(Get-Content -LiteralPath $errLog -ErrorAction SilentlyContinue) }
        $text = ($combined -join "`n")
        if ($text -match '(?i)Successfully patched') {
            Write-Ok 'Equilotl reported Successfully patched'
            return $true
        }
        if ($exit -eq 0) { return $true }
        Write-Warn "Equilotl exit $exit - will try direct path"
        if ($text) {
            $snippet = $text.Substring(0, [Math]::Min(400, $text.Length)).Replace("`r", ' ').Replace("`n", ' ')
            Write-Warn "Equilotl output: $snippet"
        }
        return $false
    } catch {
        Write-Warn "Equilotl failed: $($_.Exception.Message) - will try direct path"
        return $false
    } finally {
        Remove-Item -LiteralPath $outLog, $errLog -Force -ErrorAction SilentlyContinue
    }
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
        if ((Get-DiscordFileLength $bootstrap) -lt 1000000 -and (Test-Path -LiteralPath $stock -PathType Leaf)) {
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
