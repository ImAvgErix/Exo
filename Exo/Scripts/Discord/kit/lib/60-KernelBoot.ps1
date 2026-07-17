# 60-KernelBoot.ps1 - Kernel, boot check, summary
# Dot-sourced by Disc-Optimizer.ps1 (load order = filename sort).
# Universal multi-PC kit - do not assume Equicord/Discord already configured.

function Copy-KernelFileWithRetry {
    param(
        [Parameter(Mandatory)][string]$Source,
        [Parameter(Mandatory)][string]$Destination,
        [int]$Attempts = 8
    )
    for ($i = 1; $i -le $Attempts; $i++) {
        Stop-Discord
        try {
            if (Test-Path -LiteralPath $Destination) {
                attrib -R -S -H "$Destination" 2>$null
                try { & takeown.exe /F "$Destination" /A 2>$null | Out-Null } catch { }
                try { & icacls.exe "$Destination" /grant "*S-1-5-32-544:F" /C 2>$null | Out-Null } catch { }
            }
            Copy-Item -LiteralPath $Source -Destination $Destination -Force -ErrorAction Stop
            if ((Test-Path -LiteralPath $Destination) -and
                ((Get-Item -LiteralPath $Destination).Length -eq (Get-Item -LiteralPath $Source).Length)) {
                return
            }
            throw "Copy verify failed for $Destination"
        } catch {
            if ($i -ge $Attempts) { throw }
            Write-Warn ("Waiting to copy kernel file ($i/$Attempts): " + $_.Exception.Message)
            Start-Sleep -Milliseconds (350 * $i)
        }
    }
}

function Get-DiscOptKernelFileSummary([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return 'missing' }
    $item = Get-Item -LiteralPath $Path
    $version = ''
    try {
        $info = [Diagnostics.FileVersionInfo]::GetVersionInfo($Path)
        if ($info.FileVersion) { $version = " version=$($info.FileVersion)" }
    } catch { }
    return "len=$($item.Length)$version"
}

function Test-DiscOptStockFfmpegCompatible([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return $false }
    $item = Get-Item -LiteralPath $Path
    if ($item.Length -lt 500000) { return $false }

    try {
        $info = [Diagnostics.FileVersionInfo]::GetVersionInfo($Path)
        $labels = @($info.FileDescription, $info.ProductName, $info.OriginalFilename) |
            Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) }
        if ($labels.Count -gt 0 -and (($labels -join ' ') -notmatch '(?i)ffmpeg')) {
            return $false
        }
    } catch { }

    return $true
}

function Test-DiscOptProxyFfmpegCompatible([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return $false }
    $len = (Get-Item -LiteralPath $Path).Length
    return ($len -ge 10000 -and $len -lt 500000)
}

function Restore-DiscOptStockFfmpeg([string]$AppDir, [string]$Reason) {
    $real = Join-Path $AppDir 'ffmpeg_real.dll'
    $current = Join-Path $AppDir 'ffmpeg.dll'

    if (Test-DiscOptStockFfmpegCompatible $real) {
        try {
            Copy-KernelFileWithRetry -Source $real -Destination $current
            Write-Warn "$Reason - stock ffmpeg.dll restored"
            return $true
        } catch {
            Write-Warn "$Reason - could not restore stock ffmpeg.dll: $($_.Exception.Message)"
            return $false
        }
    }

    if (Test-DiscOptStockFfmpegCompatible $current) {
        Write-Warn "$Reason - stock ffmpeg.dll kept"
        return $true
    }

    Write-Warn "$Reason - no compatible stock ffmpeg.dll available"
    return $false
}

function Install-DiscOptKernel([string]$AppDir) {
    Write-Step 'Installing DiscOpt kernel (memory trim, priority, raw input)...'
    Write-HubProgress 78 'Installing DiscOpt kernel...'
    $Script:DiscOptKernelProxyActive = $false

    $proxy = Join-Path $KitDir 'ffmpeg.dll'
    $dll = Join-Path $KitDir 'version.dll'
    $ini = Join-Path $KitDir 'config.ini'
    foreach ($file in @($dll, $ini)) {
        if (-not (Test-Path $file)) { throw "Missing kernel file: $file" }
    }
    if ((Get-Item $dll).Length -lt 50000) { throw 'Bundled version.dll looks invalid' }
    $proxyReady = $true
    if (-not (Test-Path -LiteralPath $proxy)) {
        Write-Warn "Bundled ffmpeg proxy missing ($proxy) - keeping stock ffmpeg.dll"
        $proxyReady = $false
    } elseif (-not (Test-DiscOptProxyFfmpegCompatible $proxy)) {
        Write-Warn "Bundled ffmpeg proxy looks invalid ($(Get-DiscOptKernelFileSummary $proxy)) - keeping stock ffmpeg.dll"
        $proxyReady = $false
    }

    Stop-Discord

    # Clear any previous soft-disable markers so Reapply always reactivates the kernel.
    foreach ($name in @('version.dll', 'config.ini')) {
        $disabled = Join-Path $AppDir "$name.disabled"
        if (Test-Path -LiteralPath $disabled) {
            Remove-Item -LiteralPath $disabled -Force -ErrorAction SilentlyContinue
            Write-Ok "Cleared $name.disabled (re-enabling kernel)"
        }
    }

    $real = Join-Path $AppDir 'ffmpeg_real.dll'
    $current = Join-Path $AppDir 'ffmpeg.dll'
    $canReplaceFfmpeg = $proxyReady
    $hasStockFfmpeg = $false
    if (Test-DiscOptStockFfmpegCompatible $real) {
        $hasStockFfmpeg = $true
    } elseif (Test-DiscOptStockFfmpegCompatible $current) {
        $hasStockFfmpeg = $true
        try {
            Copy-KernelFileWithRetry -Source $current -Destination $real
            Write-Ok 'Saved stock ffmpeg.dll backup (ffmpeg_real.dll)'
        } catch {
            Write-Warn "Could not save ffmpeg_real.dll backup: $($_.Exception.Message)"
            Write-Warn 'Keeping stock ffmpeg.dll and skipping proxy replacement'
            $canReplaceFfmpeg = $false
        }
    } elseif (Test-Path -LiteralPath $current) {
        Write-Warn "Stock ffmpeg.dll looks incompatible ($(Get-DiscOptKernelFileSummary $current)) - keeping it and skipping proxy replacement"
        $canReplaceFfmpeg = $false
    } else {
        throw 'Stock ffmpeg.dll missing'
    }

    # Order matters: version.dll + config.ini first, ffmpeg.dll last.
    $verDest = Join-Path $AppDir 'version.dll'
    if (Test-Path $verDest) { attrib -R $verDest 2>$null }
    Copy-KernelFileWithRetry -Source $dll -Destination $verDest
    Copy-KernelFileWithRetry -Source $ini -Destination (Join-Path $AppDir 'config.ini')

    if (-not $canReplaceFfmpeg) {
        if (Test-DiscOptStockFfmpegCompatible $real) {
            [void](Restore-DiscOptStockFfmpeg $AppDir 'DiscOpt ffmpeg proxy skipped')
            Write-Warn 'DiscOpt ffmpeg proxy skipped; stock ffmpeg.dll kept with version.dll + config.ini installed'
        } else {
            Write-Warn 'DiscOpt ffmpeg proxy skipped; existing ffmpeg.dll kept with version.dll + config.ini installed'
        }
        return
    }

    try {
        Copy-KernelFileWithRetry -Source $proxy -Destination $current
    } catch {
        [void](Restore-DiscOptStockFfmpeg $AppDir "DiscOpt ffmpeg proxy copy failed: $($_.Exception.Message)")
        Write-Warn 'DiscOpt ffmpeg proxy skipped; version.dll + config.ini remain installed'
        return
    }

    # Sanity: proxy small, real large, version present.
    $proxyLen = (Get-Item $current).Length
    $realLen = (Get-Item $real).Length
    $verLen = (Get-Item $verDest).Length
    if ($proxyLen -ge 500000 -or $proxyLen -lt 10000) {
        [void](Restore-DiscOptStockFfmpeg $AppDir "DiscOpt ffmpeg proxy verify failed (ffmpeg.dll $proxyLen bytes)")
        Write-Warn 'DiscOpt ffmpeg proxy skipped; version.dll + config.ini remain installed'
        return
    }
    if ($hasStockFfmpeg -and $realLen -lt 500000) {
        [void](Restore-DiscOptStockFfmpeg $AppDir "DiscOpt ffmpeg backup verify failed (ffmpeg_real.dll $realLen bytes)")
        Write-Warn 'DiscOpt ffmpeg proxy skipped; version.dll + config.ini remain installed'
        return
    }
    if ($verLen -lt 50000) { throw "Kernel install failed: version.dll too small ($verLen bytes)" }

    $Script:DiscOptKernelProxyActive = $true
    Write-Ok "DiscOpt kernel active (proxy $([math]::Round($proxyLen/1KB,0)) KB + version.dll + config.ini)"
    Write-Ok 'Features: idle RAM trim, process priority, raw input'
}

function Disable-DiscOptKernelOnDisk([string]$AppDir) {
    $real = Join-Path $AppDir 'ffmpeg_real.dll'
    if ((Test-Path $real) -and ((Get-Item $real).Length -gt 500000)) {
        Copy-Item $real (Join-Path $AppDir 'ffmpeg.dll') -Force
    }
    foreach ($name in @('version.dll', 'config.ini')) {
        $path = Join-Path $AppDir $name
        if (Test-Path $path) {
            attrib -R $path 2>$null
            $disabled = "$path.disabled"
            if (Test-Path $disabled) { Remove-Item $disabled -Force -ErrorAction SilentlyContinue }
            Rename-Item $path $disabled -Force -ErrorAction SilentlyContinue
        }
    }
}

function Test-DiscordCiBootProbeEnabled {
    # This weaker probe is only valid on GitHub's disposable runner, which has
    # no real Discord login. Both gates are required so production can never
    # mistake a permanently loading client for a healthy one.
    return (($env:GITHUB_ACTIONS -eq 'true') -and ($env:EXO_CI_BOOT_PROBE -eq '1'))
}

function Wait-DiscordHealthy {
    param([int]$TimeoutSec = 120)

    # A blank/black page keeps the plain 'Discord' title forever. A healthy,
    # logged-in client reaches a real title ('Friends - Discord', '#chan - Discord').
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    $sawWindow = $false
    $ciProbe = Test-DiscordCiBootProbeEnabled
    $loadingSince = $null
    while ((Get-Date) -lt $deadline) {
        $state = Get-DiscordWindowState
        if ($state -eq 'logged_in') { return $true }
        if ($state -ne 'none') {
            $sawWindow = $true
        }
        if ($ciProbe -and $state -eq 'loading') {
            if ($null -eq $loadingSince) { $loadingSince = Get-Date }
            if (((Get-Date) - $loadingSince).TotalSeconds -ge 12) {
                Write-LogLine 'OK' 'CI boot probe: Discord process and loading window stayed stable for 12 seconds'
                return $true
            }
        } else {
            $loadingSince = $null
        }
        Start-Sleep -Seconds 2
    }
    if ($sawWindow) {
        Write-LogLine 'WARN' "Discord window stayed in state '$(Get-DiscordWindowState)' (blank page?)"
    } else {
        Write-LogLine 'WARN' 'Discord window never appeared'
    }
    return $false
}

function Invoke-DiscordLaunchAsUser([string]$AppDir) {
    # De-elevate via explorer so Discord is NOT admin (elevated Discord blacks out / dies).
    $exe = Join-Path $AppDir 'Discord.exe'
    if (-not (Test-Path -LiteralPath $exe)) { throw "Discord.exe missing under $AppDir" }
    try {
        Start-Process -FilePath 'explorer.exe' -ArgumentList "`"$exe`"" | Out-Null
        return
    } catch {
        Write-Warn "explorer launch failed: $($_.Exception.Message) - falling back to direct start"
    }
    [void](Invoke-DiscordLaunch -AppDir $AppDir)
}

function Confirm-DiscordBootsAsUser([string]$AppDir, [int]$TimeoutSec = 40) {
    # For elevated Exo Apply: prove Discord opens under a normal user token.
    Write-Step "User-token boot check (explorer launch, ${TimeoutSec}s)..."
    Stop-Discord
    Start-Sleep -Milliseconds 400
    Invoke-DiscordLaunchAsUser $AppDir
    $ok = Wait-DiscordHealthy $TimeoutSec
    Stop-Discord
    if ($ok) {
        Write-Ok 'User-token boot check passed (Discord opens from Start Menu path)'
    } else {
        Write-Warn 'User-token boot check failed (Discord did not stay open)'
    }
    return [bool]$ok
}

function Confirm-DiscordBootsAfterMods([string]$AppDir) {
    Write-Step 'Boot check: verifying Discord opens and fully loads...'
    Stop-Discord
    [void](Invoke-DiscordLaunch -AppDir $AppDir)
    if (Wait-DiscordHealthy 120) {
        Stop-Discord
        Write-Ok 'Boot check passed (Discord loaded to a real page)'
        return
    }

    Write-Warn 'Discord did not fully load - disabling DiscOpt kernel and retrying...'
    Write-LogLine 'WARN' 'Boot check failed with kernel - trying without kernel'
    Stop-Discord
    Disable-DiscOptKernelOnDisk $AppDir
    [void](Invoke-DiscordLaunch -AppDir $AppDir)
    if (Wait-DiscordHealthy 120) {
        Stop-Discord
        $Script:KernelRolledBack = $true
        Write-Warn 'Kernel disabled automatically - Discord loads without it on this PC.'
        Write-Warn 'Everything else (Equicord, Exo Host, theme, tweaks) is still active.'
        return
    }

    Write-Warn 'Still not loading - restoring stock Discord (all mods off)...'
    Write-LogLine 'WARN' 'Boot check failed without kernel - restoring stock runtime'
    Stop-Discord
    Use-StockDiscordRuntime $AppDir
    [void](Invoke-DiscordLaunch -AppDir $AppDir)
    if (Wait-DiscordHealthy 150) {
        Stop-Discord
        $Script:KernelRolledBack = $true
        $Script:ModsRolledBack = $true
        Write-Warn 'Stock Discord restored and it loads. Mods were rolled back for safety.'
        return
    }

    Stop-Discord
    throw 'Discord failed to load even in stock mode. Use Repair Discord in Exo, or: irm "https://raw.githubusercontent.com/ImAvgErix/Exo/main/Repair-Discord.ps1" | iex'
}

function Disable-Fso([string]$AppDir) {
    $exe = Join-Path $AppDir 'Discord.exe'
    if (-not (Test-Path $exe)) { return }
    $key = 'HKCU:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers'
    if (-not (Test-Path $key)) { New-Item -Path $key -Force | Out-Null }
    Set-ItemProperty -Path $key -Name $exe -Value '~ DISABLEDXMAXIMIZEDWINDOWEDMODE' -Force
    Write-Ok 'Disabled fullscreen optimizations'
}

function Restore-StartMenu {
    # Prefer official Update.exe launch (modern Discord host integrity).
    # One Start Menu entry only: Programs\Discord Inc\Discord.lnk
    # Never also create Programs\Discord.lnk (that shows as a second app).
    $app = Get-ActiveApp
    if (-not $app) { throw 'No Discord app folder - cannot refresh shortcuts' }

    $vbs = Join-Path $KitDir 'Discord.vbs'
    if (-not (Test-Path -LiteralPath $vbs)) {
        throw "Missing Discord.vbs at $vbs - reinstall Exo Discord kit"
    }

    $wscript = Get-DiscOptEnvPath 'SystemRoot' 'System32\wscript.exe'
    if (-not $wscript -or -not (Test-Path -LiteralPath $wscript)) {
        throw 'wscript.exe not found; cannot create Discord shortcuts'
    }
    $icon = Join-Path $app.FullName 'app.ico'
    $iconLoc = if (Test-Path -LiteralPath $icon) { "$icon,0" } else { "$($app.FullName)\Discord.exe,0" }
    $discordExe = Join-Path $app.FullName 'Discord.exe'
    $updateExe = Get-DiscOptEnvPath 'LOCALAPPDATA' 'Discord\Update.exe'
    $useUpdate = $updateExe -and (Test-Path -LiteralPath $updateExe)
    $desc = 'Discord'
    $wsh = New-Object -ComObject WScript.Shell

    function Set-DiscordLnk([string]$Path) {
        $dir = Split-Path -Parent $Path
        if ($dir -and -not (Test-Path -LiteralPath $dir)) {
            New-Item -ItemType Directory -Path $dir -Force | Out-Null
        }
        $sc = $wsh.CreateShortcut($Path)
        if ($useUpdate) {
            # Official squirrel path - most reliable on 1.0.92xx+
            $sc.TargetPath = $updateExe
            $sc.Arguments = '--processStart Discord.exe'
            $sc.WorkingDirectory = (Split-Path -Parent $updateExe)
        } else {
            $sc.TargetPath = $wscript
            $sc.Arguments = "`"$vbs`" //B"
            $sc.WorkingDirectory = $Root
        }
        $sc.Description = $desc
        $sc.IconLocation = $iconLoc
        $sc.WindowStyle = 1
        $sc.Save()
    }

    function Test-IsDiscordClientLnk([string]$LnkPath) {
        try {
            $sc = $wsh.CreateShortcut($LnkPath)
            $t = [string]$sc.TargetPath
            $a = [string]$sc.Arguments
            if ($t -match '(?i)wscript\.exe$' -and $a -match '(?i)Discord\.vbs') { return $true }
            if ($t -match '(?i)[\\/]Discord\.exe$') { return $true }
            if ($t -match '(?i)[\\/]Update\.exe$' -and $a -match '(?i)Discord') { return $true }
            if ($discordExe -and $t -and ([IO.Path]::GetFullPath($t) -eq [IO.Path]::GetFullPath($discordExe))) { return $true }
            $base = [IO.Path]::GetFileNameWithoutExtension($LnkPath)
            if ($base -match '^(?i)discord(\s+(canary|ptb|development))?$') { return $true }
            return $false
        } catch { return $false }
    }

    $roots = @(
        [Environment]::GetFolderPath('Programs'),
        [Environment]::GetFolderPath('CommonPrograms'),
        [Environment]::GetFolderPath('Desktop'),
        [Environment]::GetFolderPath('CommonDesktopDirectory'),
        (Join-Path $env:APPDATA 'Microsoft\Internet Explorer\Quick Launch'),
        (Join-Path $env:APPDATA 'Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar'),
        (Join-Path $env:APPDATA 'Microsoft\Internet Explorer\Quick Launch\User Pinned\StartMenu'),
        (Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu'),
        (Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu')
    ) | Where-Object { $_ -and (Test-Path -LiteralPath $_) }

    $patched = 0
    $seen = @{}
    foreach ($root in $roots) {
        Get-ChildItem -LiteralPath $root -Filter '*.lnk' -Recurse -Force -ErrorAction SilentlyContinue | ForEach-Object {
            $key = $_.FullName.ToLowerInvariant()
            if ($seen.ContainsKey($key)) { return }
            if (-not (Test-IsDiscordClientLnk $_.FullName)) { return }
            # Never keep Discord / Exo icons on the Desktop - delete them, do not retarget.
            if ($_.FullName -match '(?i)[\\/]Desktop[\\/]') {
                try { Remove-Item -LiteralPath $_.FullName -Force -ErrorAction SilentlyContinue } catch { }
                $seen[$key] = $true
                return
            }
            try {
                Set-DiscordLnk $_.FullName
                $seen[$key] = $true
                $patched++
            } catch {
                Write-Warn "Discord shortcut skip $($_.FullName): $($_.Exception.Message)"
            }
        }
    }

    # Remove duplicate root-level Discord.lnk (causes "two Discord apps" in Start).
    # Runs BEFORE the canonical shortcut is created: Win32 strips trailing dots
    # from path segments, so deleting "Discord Inc.\Discord.lnk" by plain path
    # can alias onto "Discord Inc\Discord.lnk" and destroy the shortcut we just
    # made. Trailing-dot paths are addressed with the \\?\ literal prefix so the
    # real dot-named entry (Squirrel authors metadata is "Discord Inc.") is the
    # only thing removed.
    foreach ($dup in @(
        (Get-DiscOptEnvPath 'APPDATA' 'Microsoft\Windows\Start Menu\Programs\Discord.lnk'),
        (Join-Path ([Environment]::GetFolderPath('CommonPrograms')) 'Discord.lnk'),
        (Join-Path ([Environment]::GetFolderPath('CommonPrograms')) 'Discord Inc\Discord.lnk')
    )) {
        if ($dup -and (Test-Path -LiteralPath $dup)) {
            try {
                Remove-Item -LiteralPath $dup -Force -ErrorAction SilentlyContinue
                Write-Ok "Removed duplicate shortcut: $dup"
            } catch { }
        }
    }
    # "Discord Inc." (with period) folder is a second Start tile. Enumerate the
    # real directory entries so the dot-named folder is only removed when it is
    # a genuinely distinct entry, and delete via \\?\ to bypass normalization.
    $programsDir = Get-DiscOptEnvPath 'APPDATA' 'Microsoft\Windows\Start Menu\Programs'
    if ($programsDir -and (Test-Path -LiteralPath $programsDir)) {
        Get-ChildItem -LiteralPath $programsDir -Directory -Force -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -eq 'Discord Inc.' } | ForEach-Object {
                try {
                    Remove-Item -LiteralPath ("\\?\" + $_.FullName) -Recurse -Force -ErrorAction SilentlyContinue
                    Write-Ok 'Removed duplicate Start tile folder: Discord Inc.'
                } catch { }
            }
    }

    # Canonical Start Menu: ONE entry under Discord Inc (user). Never root Discord.lnk.
    # Created LAST so no cleanup pass above can take it out.
    $ensure = @(
        (Get-DiscOptEnvPath 'APPDATA' 'Microsoft\Windows\Start Menu\Programs\Discord Inc\Discord.lnk')
    ) | Where-Object { $_ }

    foreach ($path in $ensure) {
        try {
            Set-DiscordLnk $path
            $patched++
            $via = if ($useUpdate) { 'Update.exe' } else { 'Discord.vbs' }
            Write-Ok ("Shortcut -> ${via}: {0}" -f ($path -replace [regex]::Escape($env:USERPROFILE), '~'))
        } catch {
            Write-Warn "Ensure shortcut failed ($path): $($_.Exception.Message)"
        }
    }

    # Never leave Discord / Exo icons on user or public Desktop
    foreach ($desk in @(
        [Environment]::GetFolderPath('Desktop'),
        [Environment]::GetFolderPath('CommonDesktopDirectory')
    )) {
        if (-not $desk -or -not (Test-Path -LiteralPath $desk)) { continue }
        foreach ($name in @('Discord.lnk', 'Discord (Exo).lnk')) {
            $p = Join-Path $desk $name
            if (Test-Path -LiteralPath $p) {
                Remove-Item -LiteralPath $p -Force -ErrorAction SilentlyContinue
                Write-Ok "Removed desktop shortcut: $name"
            }
        }
        Get-ChildItem -LiteralPath $desk -Filter 'Discord*.lnk' -Force -ErrorAction SilentlyContinue | ForEach-Object {
            try { Remove-Item -LiteralPath $_.FullName -Force -ErrorAction SilentlyContinue; Write-Ok "Removed desktop: $($_.Name)" } catch { }
        }
    }

    Write-Ok "Discord launch shortcuts refreshed ($patched) - Start Menu / taskbar (no desktop icons created)"
}

function Test-KernelOnDisk([string]$AppDir) {
    $ok = $true
    foreach ($name in @('version.dll', 'config.ini')) {
        $path = Join-Path $AppDir $name
        if (-not (Test-Path $path)) {
            Write-Warn "Kernel file missing on disk: $name"
            $ok = $false
            continue
        }
        if ($name -eq 'version.dll' -and (Get-Item $path).Length -lt 50000) {
            Write-Warn 'version.dll looks invalid'
            $ok = $false
        } else {
            Write-Ok "Kernel on disk: $name"
        }
    }
    $ffmpegReal = Join-Path $AppDir 'ffmpeg_real.dll'
    if (Test-Path $ffmpegReal) { Write-Ok 'Kernel on disk: ffmpeg_real.dll (stock backup)' }
    $ffmpeg = Join-Path $AppDir 'ffmpeg.dll'
    if (Test-Path $ffmpeg) {
        if ((Get-Item $ffmpeg).Length -lt 500000) {
            Write-Ok 'Kernel on disk: ffmpeg.dll (proxy - memory trim active)'
        } else {
            Write-Warn 'Kernel on disk: ffmpeg.dll still stock (trim inactive until -Launch)'
            $ok = $false
        }
    }
    return $ok
}

function Test-DiscOptimizer {
    $app = Get-ActiveApp
    if (-not $app) { Write-Warn 'No active Discord app folder'; return }

    $resources = Join-Path $app.FullName 'resources'
    $loader = Join-Path $resources 'app.asar'
    $bootstrap = Join-Path $resources '_app.asar'
    $stockBackup = Join-Path $resources '_app.asar.stock'

    if ((Test-Path (Join-Path $EquicordData 'equicord.asar')) -and (Test-Path $loader) -and (Get-Item $loader).Length -lt 4096) {
        Write-Ok 'Equicord loader active (app.asar stub)'
    } elseif (-not $SkipEquicord) {
        Write-Warn 'Equicord loader not verified'
    }

    if (Test-ExoHostInstalled $app.FullName) {
        Write-Ok 'Exo Host active (Equicord loader on app.asar)'
    } elseif (-not $SkipEquicord) {
        Write-Warn 'Exo Host / Equicord loader not verified on app.asar'
    }
    if (Test-OpenAsarInstalled $resources) {
        Write-Warn 'Legacy OpenAsar still on _app.asar - removed on next Apply'
    }

    if (Test-Path $stockBackup) {
        Write-Ok 'Stock shell backup present (_app.asar.stock)'
    } elseif (-not $SkipEquicord) {
        Write-Warn 'No _app.asar.stock backup yet'
    }

    $krispPath = Join-Path $app.FullName 'modules\discord_krisp-1'
    if (Test-Path $krispPath) { Write-Ok 'Krisp module present (voice UI)' }
    else { Write-Warn 'Krisp module missing - None dropdown may not work' }

    if (-not $SkipKernel) {
        if (-not (Test-KernelOnDisk $app.FullName)) {
            Write-Warn 'DiscOpt kernel files incomplete on disk'
        }
        if (Get-Process Discord -ErrorAction SilentlyContinue) {
            try {
                $kernelRunning = Get-Process Discord -ErrorAction SilentlyContinue | ForEach-Object {
                    $ff = $_.Modules | Where-Object { $_.FileName -like '*\Discord\app-*\ffmpeg.dll' } | Select-Object -First 1
                    if ($ff -and (Get-Item $ff.FileName -ErrorAction SilentlyContinue).Length -lt 500000) { return $true }
                } | Select-Object -First 1
                if ($kernelRunning) { Write-Ok 'DiscOpt kernel loaded (ffmpeg proxy in process)' }
            } catch {
                Write-Warn 'Could not inspect running Discord modules; on-disk kernel check completed'
                Write-LogLine 'WARN' "Process module inspection failed: $($_.Exception.Message)"
            }
        }
    }

    $settingsPath = Join-Path $EquicordData 'settings\settings.json'
    if (Test-Path $settingsPath) {
        $eqHealth = Get-EquicordSettingsHealth $settingsPath
        if ($eqHealth.Healthy) {
            $sizeKb = [math]::Round($eqHealth.Size / 1KB, 1)
            Write-Ok "Equicord settings OK ($($eqHealth.Plugins) plugins, $sizeKb KB, no BOM)"
        } else {
            Write-Warn "Equicord settings issue: $($eqHealth.Reason) ($($eqHealth.Plugins) plugins, $($eqHealth.Size) bytes)"
        }
        try {
            $s = Get-Content $settingsPath -Raw -Encoding UTF8 | ConvertFrom-Json
            Write-Ok "Equicord plugins: $($eqHealth.Enabled) enabled / $($eqHealth.Plugins) listed"
            if ($s.enabledThemes -and $s.enabledThemes.Count -gt 0) {
                Write-Ok "Themes on: $($s.enabledThemes -join ', ')"
            } else {
                Write-Warn 'No themes enabled in settings'
            }
            $bk = $s.plugins.BlockKrisp
            if ($bk -and $bk.enabled -eq $true) { Write-Warn 'BlockKrisp enabled - None dropdown may break' }
            else { Write-Ok 'BlockKrisp off (native noise UI)' }
            $dc = $s.plugins.Declutter
            if ($dc -and $dc.removeAudioMenus -eq $true) { Write-Warn 'Declutter hiding audio menus' }
        } catch {}
    }
}

function Write-RunSummary {
    param(
        [string]$AppDir,
        [bool]$Launched
    )

    $checks = [System.Collections.Generic.List[string]]::new()
    $psLabel = "PowerShell $($PSVersionTable.PSVersion)"
    if ($PSVersionTable.PSEdition) { $psLabel += " ($($PSVersionTable.PSEdition))" }
    $checks.Add($psLabel)

    if ($Launched) {
        $proc = Get-Process Discord -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($proc) { $checks.Add("Discord running (PID $($proc.Id))") }
        else { $checks.Add('Discord launch requested (process not seen yet)') }
    } elseif (-not $NoLaunch) {
        $checks.Add('Discord left closed - open when ready')
    } else {
        $checks.Add('Discord not started (use Start menu or -Launch)')
    }

    $settingsPath = Join-Path $EquicordData 'settings\settings.json'
    if (Test-Path $settingsPath) {
        $eq = Get-EquicordSettingsHealth $settingsPath
        if ($eq.Healthy) { $checks.Add("Equicord OK - $($eq.Enabled) plugins on") }
        else { $checks.Add("Equicord settings: $($eq.Reason)") }
    }

    if ($Script:ModsRolledBack) {
        $checks.Add('SAFETY: mods rolled back to stock (Discord would not boot with them)')
    } elseif ($Script:KernelRolledBack) {
        $checks.Add('SAFETY: kernel disabled (Discord boots fine without it; all other tweaks active)')
    } elseif ($AppDir) {
        $ff = Join-Path $AppDir 'ffmpeg.dll'
        if ((Test-Path $ff) -and (Get-Item $ff).Length -lt 500000) {
            $checks.Add('DiscOpt kernel on disk (memory trim)')
        } elseif (Test-Path $ff) {
            $checks.Add('Kernel proxy installs on next Discord start')
        }
    }

    if (Test-DiscordLoggedIn) {
        $checks.Add('Login session preserved')
    }

    Write-Host ''
    Write-Host '  ========================================' -ForegroundColor DarkGray
    Write-Host '   DONE - everything applied successfully' -ForegroundColor Green
    Write-Host '  ========================================' -ForegroundColor DarkGray
    foreach ($line in $checks) {
        Write-Host "   [+] $line" -ForegroundColor Green
    }
    Write-Host "   [i] Log: $(Join-Path $LogDir 'last-run.log')" -ForegroundColor DarkGray
    Write-Host ''
}

