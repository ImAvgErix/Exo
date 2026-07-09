# Steam Optimizer - multi-PC safe tweaks focused on steamwebhelper RAM/CPU.
# Steam is CEF/Chromium (not Electron) so Discord-style asar/kernel inject does
# not apply. Instead we use Valve CEF flags, interface settings, cache cleanup,
# startup quieting, and optional working-set trim for webhelper.
#
#   Steam-Optimizer.ps1
#   Steam-Optimizer.ps1 -Quick
#   Steam-Optimizer.ps1 -Repair

param(
    [switch]$Quick,
    [switch]$Repair,
    [switch]$NonInteractive,
    [switch]$NoLaunch
)

$ErrorActionPreference = 'Stop'
$Script:SteamOptVersion = '1.3.2'
$Root = Split-Path -Parent $MyInvocation.MyCommand.Path

# Default Steam launch flags (formerly "aggressive" - this is the only tier).
# Avoid sandbox/single-process flags - those crash on some PCs.
$Script:DefaultCefArgs = @(
    '-cef-disable-gpu',
    '-cef-disable-gpu-compositing',
    '-nofriendsui',
    '-nointro',
    '-nobigpicture',
    '-vrdisable',
    '-no-dwrite',
    '-cef-disable-breakpad'
)

function Write-HubProgress([int]$Percent, [string]$Status) {
    $p = [Math]::Max(0, [Math]::Min(100, $Percent))
    $line = "OPTIHUB_PROGRESS:$p|$Status"
    Write-Output $line
    if ($env:OPTIHUB_LOG) {
        try { Add-Content -LiteralPath $env:OPTIHUB_LOG -Value $line -Encoding UTF8 -ErrorAction SilentlyContinue } catch { }
    }
}
function Write-SteamLog([string]$Prefix, [string]$Msg) {
    $line = "$Prefix $Msg"
    Write-Host $line
    if ($env:OPTIHUB_LOG) {
        try { Add-Content -LiteralPath $env:OPTIHUB_LOG -Value $line -Encoding UTF8 -ErrorAction SilentlyContinue } catch { }
    }
}
function Write-Step([string]$Msg) { Write-SteamLog '[*]' $Msg }
function Write-Ok([string]$Msg)   { Write-SteamLog '[+]' $Msg }
function Write-Warn([string]$Msg) { Write-SteamLog '[!]' $Msg }
function Write-Err([string]$Msg)  { Write-SteamLog '[-]' $Msg }

function Get-SteamInstallPath {
    $candidates = @()
    try {
        $hkcu = Get-ItemProperty -Path 'HKCU:\Software\Valve\Steam' -ErrorAction SilentlyContinue
        if ($hkcu -and $hkcu.SteamPath) { $candidates += $hkcu.SteamPath }
        if ($hkcu -and $hkcu.SteamExe) {
            $dir = Split-Path -Parent $hkcu.SteamExe
            if ($dir) { $candidates += $dir }
        }
    } catch { }
    try {
        $hklm = Get-ItemProperty -Path 'HKLM:\SOFTWARE\WOW6432Node\Valve\Steam' -ErrorAction SilentlyContinue
        if ($hklm -and $hklm.InstallPath) { $candidates += $hklm.InstallPath }
    } catch { }
    try {
        $hklm64 = Get-ItemProperty -Path 'HKLM:\SOFTWARE\Valve\Steam' -ErrorAction SilentlyContinue
        if ($hklm64 -and $hklm64.InstallPath) { $candidates += $hklm64.InstallPath }
    } catch { }

    $pf86 = [Environment]::GetFolderPath('ProgramFilesX86')
    $pf = [Environment]::GetFolderPath('ProgramFiles')
    $local = [Environment]::GetFolderPath('LocalApplicationData')
    $candidates += @(
        (Join-Path $pf86 'Steam'),
        (Join-Path $pf 'Steam'),
        (Join-Path $local 'Steam')
    )

    foreach ($c in $candidates) {
        if ([string]::IsNullOrWhiteSpace($c)) { continue }
        $norm = $c.TrimEnd('\', '/')
        $exe = Join-Path $norm 'steam.exe'
        if (Test-Path -LiteralPath $exe) {
            try { return (Resolve-Path -LiteralPath $norm).Path }
            catch { return $norm }
        }
    }
    return $null
}

function Stop-Steam {
    Write-Step 'Closing Steam / steamwebhelper...'
    $names = @('steam', 'steamwebhelper', 'steamservice', 'GameOverlayUI', 'steamerrorreporter')
    for ($i = 1; $i -le 6; $i++) {
        $procs = @(Get-Process -Name $names -ErrorAction SilentlyContinue)
        if ($procs.Count -eq 0) { break }
        foreach ($p in $procs) {
            try { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue } catch { }
        }
        Start-Sleep -Milliseconds (350 * $i)
    }
    try { & taskkill.exe /F /IM steam.exe /T 2>$null | Out-Null } catch { }
    try { & taskkill.exe /F /IM steamwebhelper.exe /T 2>$null | Out-Null } catch { }
    Start-Sleep -Milliseconds 600
    Write-Ok 'Steam closed'
}

function Get-OptiHubSteamStatePath {
    $dir = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'OptiHub'
    if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    return (Join-Path $dir 'steam-optimizer.json')
}

function Save-SteamOptState([hashtable]$State) {
    $path = Get-OptiHubSteamStatePath
    $json = $State | ConvertTo-Json -Depth 8
    [IO.File]::WriteAllText($path, $json, [Text.UTF8Encoding]::new($false))
}

function Disable-SteamWindowsStartup {
    $removed = 0
    $runKeys = @(
        'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run',
        'HKLM:\Software\Microsoft\Windows\CurrentVersion\Run',
        'HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run'
    )
    foreach ($key in $runKeys) {
        if (-not (Test-Path $key)) { continue }
        try {
            $props = Get-ItemProperty -Path $key -ErrorAction SilentlyContinue
            if (-not $props) { continue }
            foreach ($name in @($props.PSObject.Properties.Name)) {
                if ($name -match '^PS') { continue }
                $val = [string]$props.$name
                if ($val -match '(?i)steam\.exe' -or $name -match '(?i)^steam') {
                    Remove-ItemProperty -Path $key -Name $name -Force -ErrorAction SilentlyContinue
                    $removed++
                    Write-Ok "Removed startup entry: $name"
                }
            }
        } catch { }
    }

    try {
        $steamKey = 'HKCU:\Software\Valve\Steam'
        if (Test-Path $steamKey) {
            New-ItemProperty -Path $steamKey -Name 'StartupMode' -PropertyType DWord -Value 0 -Force | Out-Null
            Write-Ok 'Steam StartupMode = 0 (do not auto-start)'
        }
    } catch {
        Write-Warn "Could not set StartupMode: $($_.Exception.Message)"
    }

    return $removed
}

function Get-SteamLibraryRoots([string]$SteamPath) {
    $roots = New-Object System.Collections.Generic.List[string]
    [void]$roots.Add($SteamPath)
    $vdf = Join-Path $SteamPath 'steamapps\libraryfolders.vdf'
    if (Test-Path -LiteralPath $vdf) {
        try {
            $text = [IO.File]::ReadAllText($vdf)
            foreach ($m in [regex]::Matches($text, '"path"\s+"([^"]+)"')) {
                $p = $m.Groups[1].Value -replace '\\\\', '\'
                if ($p -and (Test-Path -LiteralPath $p) -and -not $roots.Contains($p)) {
                    [void]$roots.Add($p)
                }
            }
        } catch { }
    }
    return @($roots)
}

function Clear-PathTree([string]$Path) {
    [long]$freed = 0
    if (-not (Test-Path -LiteralPath $Path)) { return 0L }
    try {
        $item = Get-Item -LiteralPath $Path -Force -ErrorAction Stop
        if ($item.PSIsContainer) {
            $sumObj = Get-ChildItem -LiteralPath $item.FullName -Recurse -Force -File -ErrorAction SilentlyContinue |
                Measure-Object -Property Length -Sum
            $size = if ($null -ne $sumObj -and $null -ne $sumObj.Sum) { [long]$sumObj.Sum } else { 0L }
            Remove-Item -LiteralPath $item.FullName -Recurse -Force -ErrorAction SilentlyContinue
            $freed = $size
            if ($size -gt 0) {
                Write-Ok ("Cleared {0} (~{1:N1} MB)" -f $item.Name, ($size / 1MB))
            }
        } else {
            $freed = [long]$item.Length
            Remove-Item -LiteralPath $item.FullName -Force -ErrorAction SilentlyContinue
        }
    } catch {
        Write-Warn ("Skip cache {0}: {1}" -f $Path, $_.Exception.Message)
    }
    return $freed
}

function Clear-SteamSafeCaches([string]$SteamPath) {
    $targets = New-Object System.Collections.Generic.List[string]
    foreach ($lib in (Get-SteamLibraryRoots $SteamPath)) {
        foreach ($rel in @(
            'htmlcache', 'logs', 'dumps', 'crashhandler.log',
            'appcache\httpcache', 'appcache\shader',
            'steamapps\temp', 'steamapps\downloading'
        )) {
            [void]$targets.Add((Join-Path $lib $rel))
        }
    }

    $localSteam = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Steam'
    if (Test-Path $localSteam) {
        foreach ($rel in @('htmlcache', 'GPUCache', 'Code Cache', 'ShaderCache', 'DawnCache', 'GrShaderCache')) {
            [void]$targets.Add((Join-Path $localSteam $rel))
        }
    }

    $cefRoots = @(
        (Join-Path $SteamPath 'bin\cef'),
        (Join-Path $SteamPath 'bin\cef\cef.win7x64'),
        (Join-Path $SteamPath 'bin\cef\cef.win64')
    )
    foreach ($cr in $cefRoots) {
        if (Test-Path $cr) {
            [void]$targets.Add((Join-Path $cr 'GPUCache'))
            [void]$targets.Add((Join-Path $cr 'Code Cache'))
            [void]$targets.Add((Join-Path $cr 'Cache'))
        }
    }

    [long]$freed = 0
    foreach ($t in $targets) { $freed += (Clear-PathTree $t) }

    Get-ChildItem -LiteralPath $SteamPath -Filter '*.log' -File -ErrorAction SilentlyContinue | ForEach-Object {
        try {
            $freed += [long]$_.Length
            Remove-Item -LiteralPath $_.FullName -Force -ErrorAction SilentlyContinue
        } catch { }
    }

    return $freed
}

function Clear-SteamShaderCaches([string]$SteamPath) {
    # Game shader pre-cache rebuilds on next launch - free disk / stale GPU state.
    Write-Step 'Clearing Steam shader pre-caches (rebuild on next game launch)...'
    [long]$freed = 0
    foreach ($lib in (Get-SteamLibraryRoots $SteamPath)) {
        $freed += (Clear-PathTree (Join-Path $lib 'steamapps\shadercache'))
        $freed += (Clear-PathTree (Join-Path $lib 'appcache\shader'))
    }
    $local = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Steam'
    if (Test-Path $local) {
        $freed += (Clear-PathTree (Join-Path $local 'ShaderCache'))
        $freed += (Clear-PathTree (Join-Path $local 'shadercache'))
    }
    Write-Ok ("Shader cache cleanup freed ~{0:N1} MB" -f ($freed / 1MB))
    return $freed
}

function Set-SteamVdfKey([string]$Raw, [string]$Key, [string]$Value) {
    $pattern = '"' + [regex]::Escape($Key) + '"\s+"[^"]*"'
    $replacement = '"' + $Key + '"		"' + $Value + '"'
    if ($Raw -match $pattern) {
        return [regex]::Replace($Raw, $pattern, $replacement)
    }
    # Insert near top of first big block if possible - append before final closing braces is fragile.
    # Prefer inject after first "{" in file for unknown keys under a synthetic OptiHub block.
    return $Raw
}

function Set-SteamLocalConfigTweaks {
    $steamPath = Get-SteamInstallPath
    if (-not $steamPath) { return @{ Gpu = $false; Patched = $false; Path = $null; Snappy = $false } }

    $userdata = Join-Path $steamPath 'userdata'
    if (-not (Test-Path $userdata)) {
        Write-Warn 'No userdata yet - open Steam once, then Reapply for deeper client tweaks'
        return @{ Gpu = $false; Patched = $false; Path = $null; Snappy = $false }
    }

    $files = @(Get-ChildItem -LiteralPath $userdata -Directory -ErrorAction SilentlyContinue |
        ForEach-Object {
            $p = Join-Path $_.FullName 'config\localconfig.vdf'
            if (Test-Path $p) { Get-Item $p }
        } | Sort-Object LastWriteTime -Descending)

    if ($files.Count -eq 0) {
        Write-Warn 'No localconfig.vdf yet - open Steam once, then Reapply'
        return @{ Gpu = $false; Patched = $false; Path = $null; Snappy = $false }
    }

    # Patch all accounts on this PC (universal multi-user machine)
    $anyGpu = $false
    $anySnappy = $false
    $anyPatched = $false
    $lastPath = $null
    foreach ($file in $files) {
        try {
            attrib -R $file.FullName 2>$null
            $raw = [IO.File]::ReadAllText($file.FullName)
            $orig = $raw

            # Webhelper / CEF load (GPU decode of web UI is a common RAM+GPU hog)
            foreach ($k in @('H264HWAccel', 'GPUAccelWebViews', 'GPUAccelWebViews2', 'GPUAccelWebViewsD3D11')) {
                $before = $raw
                $raw = Set-SteamVdfKey $raw $k '0'
                if ($raw -ne $before) { $anyGpu = $true }
            }

            # Snappier feel: less animation chrome, keep library responsive
            foreach ($pair in @(
                @{ K = 'SmoothScrollWebViews'; V = '0' },
                @{ K = 'LibraryLowBandwidthMode'; V = '0' },
                @{ K = 'LibraryLowPerfMode'; V = '0' },
                @{ K = 'StartupMovieMode'; V = '0' },
                @{ K = 'LibraryDisableCommunityContent'; V = '1' },
                @{ K = 'LibraryDisplayIconInGameList'; V = '0' }
            )) {
                $before = $raw
                $raw = Set-SteamVdfKey $raw $pair.K $pair.V
                if ($raw -ne $before) { $anySnappy = $true }
            }

            # Overlay extras: keep overlay on, cut browser/noise hitch sources when keys exist
            foreach ($pair in @(
                @{ K = 'EnableGameOverlay'; V = '1' },
                @{ K = 'InGameOverlayScreenshotNotification'; V = '0' },
                @{ K = 'InGameOverlayShowFPSCounterHotKey'; V = '0' },
                @{ K = 'SteamInputConfigEnabled'; V = '1' },
                @{ K = 'Controller_EnableChrome'; V = '0' },
                @{ K = 'BigPictureInForeground'; V = '0' }
            )) {
                $before = $raw
                $raw = Set-SteamVdfKey $raw $pair.K $pair.V
                if ($raw -ne $before) { $anySnappy = $true }
            }

            # Quieter / less background wakeups
            foreach ($k in @('NotifyAvailableGames', 'SoundPlay_DownloadComplete', 'SoundPlay_FriendOnline', 'FriendsAlwaysShowAvatars')) {
                $raw = Set-SteamVdfKey $raw $k '0'
            }

            # Prefer not downloading while playing (smoother 1% lows); cloud stays on
            foreach ($pair in @(
                @{ K = 'AllowDownloadsDuringGameplay'; V = '0' },
                @{ K = 'CloudEnabled'; V = '1' }
            )) {
                $raw = Set-SteamVdfKey $raw $pair.K $pair.V
            }

            if ($raw -ne $orig) {
                $bak = $file.FullName + '.optihub-bak'
                if (-not (Test-Path $bak)) { Copy-Item $file.FullName $bak -Force }
                [IO.File]::WriteAllText($file.FullName, $raw, [Text.UTF8Encoding]::new($false))
                $anyPatched = $true
                $lastPath = $file.FullName
                Write-Ok ("localconfig.vdf patched (user {0})" -f $file.Directory.Parent.Name)
            }
        } catch {
            Write-Warn "localconfig.vdf: $($_.Exception.Message)"
        }
    }

    if (-not $anyPatched) {
        Write-Ok 'localconfig.vdf: no matching keys - CEF launch flags + download config still apply'
    }

    return @{ Gpu = $anyGpu; Patched = $anyPatched; Path = $lastPath; Snappy = $anySnappy }
}

function Set-SteamLibraryConfigHints([string]$SteamPath) {
    $config = Join-Path $SteamPath 'config\config.vdf'
    if (-not (Test-Path -LiteralPath $config)) {
        Write-Warn 'config.vdf not found'
        return $false
    }
    try {
        attrib -R $config 2>$null
        $raw = [IO.File]::ReadAllText($config)
        $orig = $raw

        # Unlimited / max download throughput when keys exist
        foreach ($pair in @(
            @{ K = 'DownloadThrottleKbps'; V = '0' },
            @{ K = 'ThrottleKbps'; V = '0' },
            @{ K = 'RateLimitBps'; V = '0' },
            @{ K = 'MaxSimDownloads'; V = '8' },
            @{ K = 'AutoUpdateWindowEnabled'; V = '0' }
        )) {
            $raw = Set-SteamVdfKey $raw $pair.K $pair.V
        }

        if ($raw -ne $orig) {
            $bak = $config + '.optihub-bak'
            if (-not (Test-Path $bak)) { Copy-Item $config $bak -Force }
            [IO.File]::WriteAllText($config, $raw, [Text.UTF8Encoding]::new($false))
            Write-Ok 'config.vdf: download throttle off / snappier download settings'
            return $true
        }
        Write-Ok 'config.vdf: no download keys to patch (Steam UI rate limit still available in Settings)'
        return $true
    } catch {
        Write-Warn "config.vdf: $($_.Exception.Message)"
        return $false
    }
}

function Optimize-SteamDownloadFolder([string]$SteamPath) {
    # Clear stuck/partial download staging so the next download starts clean and fast.
    $dirs = @(
        (Join-Path $SteamPath 'steamapps\downloading'),
        (Join-Path $SteamPath 'steamapps\temp'),
        (Join-Path $SteamPath 'steamapps\workshop\downloads')
    )
    $n = 0
    foreach ($d in $dirs) {
        if (-not (Test-Path $d)) { continue }
        Get-ChildItem -LiteralPath $d -Force -ErrorAction SilentlyContinue | ForEach-Object {
            try {
                Remove-Item -LiteralPath $_.FullName -Recurse -Force -ErrorAction SilentlyContinue
                $n++
            } catch { }
        }
    }
    if ($n -gt 0) { Write-Ok "Cleared $n stuck download/temp item(s) for cleaner next download" }
    else { Write-Ok 'Download staging folders already clean' }
    return $n
}

function Write-SteamLaunchCmd([string]$CmdPath, [string]$SteamPath, [string]$HelperPath, [string[]]$CefArgs, [string]$Label) {
    $exe = Join-Path $SteamPath 'steam.exe'
    $args = ($CefArgs -join ' ')
    $ps = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $cmd = @(
        '@echo off'
        ("rem OptiHub {0} - 5s webhelper trim + in-game BELOW_NORMAL for steam/webhelper" -f $Label)
        ('start "" /MIN "{0}" -NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File "{1}"' -f $ps, $HelperPath)
        ('start "" /HIGH /D "{0}" "{1}" {2}' -f $SteamPath, $exe, $args)
    ) -join "`r`n"
    [IO.File]::WriteAllText($CmdPath, $cmd + "`r`n", [Text.UTF8Encoding]::new($false))
}

function Get-SteamShortcutSearchRoots {
    $roots = New-Object System.Collections.Generic.List[string]
    $candidates = @(
        [Environment]::GetFolderPath('Programs'),              # Start Menu (user)
        [Environment]::GetFolderPath('CommonPrograms'),        # Start Menu (all users)
        [Environment]::GetFolderPath('Desktop'),
        [Environment]::GetFolderPath('CommonDesktopDirectory'),
        (Join-Path $env:APPDATA 'Microsoft\Internet Explorer\Quick Launch'),
        (Join-Path $env:APPDATA 'Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar'),
        (Join-Path $env:APPDATA 'Microsoft\Internet Explorer\Quick Launch\User Pinned\StartMenu'),
        (Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu'),
        (Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu')
    )
    foreach ($c in $candidates) {
        if ($c -and (Test-Path -LiteralPath $c) -and -not $roots.Contains($c)) {
            [void]$roots.Add($c)
        }
    }
    return @($roots)
}

function Test-LnkIsSteamClient([string]$LnkPath, [string]$SteamExe, $Wsh) {
    try {
        $sc = $Wsh.CreateShortcut($LnkPath)
        $target = [string]$sc.TargetPath
        if ([string]::IsNullOrWhiteSpace($target)) { return $false }
        # Stock steam.exe or our launchers
        if ($target -match '(?i)Steam-OptiHub(\.cmd|-Aggressive\.cmd)$') { return $true }
        if ($target -match '(?i)[\\/]steam\.exe$') { return $true }
        # Same install dir steam.exe (path normalize)
        try {
            $fullT = [IO.Path]::GetFullPath($target)
            $fullE = [IO.Path]::GetFullPath($SteamExe)
            if ($fullT -eq $fullE) { return $true }
        } catch { }
        # Name-based Start Menu entries often "Steam.lnk" / "Steam Client.lnk"
        $base = [IO.Path]::GetFileNameWithoutExtension($LnkPath)
        if ($base -match '^(?i)steam(\s+client)?$' -and $target -match '(?i)steam') { return $true }
        return $false
    } catch {
        return $false
    }
}

function Set-SteamShortcutTarget([string]$LnkPath, [string]$TargetCmd, [string]$SteamPath, [string]$SteamExe, [string]$Description, $Wsh) {
    $sc = $Wsh.CreateShortcut($LnkPath)
    $sc.TargetPath = $TargetCmd
    $sc.Arguments = ''
    $sc.WorkingDirectory = $SteamPath
    $sc.IconLocation = "$SteamExe,0"
    $sc.WindowStyle = 1
    $sc.Description = $Description
    $sc.Save()
}

function Install-LeanSteamLauncher([string]$SteamPath, [string]$HelperPath) {
    # Single default launcher (full CEF quiet flags). Patch Start Menu / taskbar only.
    # Never create Desktop shortcuts.
    $exe = Join-Path $SteamPath 'steam.exe'
    $cmdPath = Join-Path $SteamPath 'Steam-OptiHub.cmd'
    Write-SteamLaunchCmd $cmdPath $SteamPath $HelperPath $Script:DefaultCefArgs 'default CEF'
    Write-Ok "Steam launcher: $cmdPath"
    Write-Ok ("CEF flags: {0}" -f ($Script:DefaultCefArgs -join ' '))

    # Remove old optional aggressive launcher if present
    $oldAgg = Join-Path $SteamPath 'Steam-OptiHub-Aggressive.cmd'
    if (Test-Path -LiteralPath $oldAgg) {
        Remove-Item -LiteralPath $oldAgg -Force -ErrorAction SilentlyContinue
        Write-Ok 'Removed old Steam-OptiHub-Aggressive.cmd'
    }

    $wsh = New-Object -ComObject WScript.Shell
    $patched = 0
    $seen = @{}
    $desc = 'Steam (OptiHub - CEF quiet + 5s webhelper trim)'

    foreach ($root in (Get-SteamShortcutSearchRoots)) {
        $lnks = @(Get-ChildItem -LiteralPath $root -Filter '*.lnk' -Recurse -Force -ErrorAction SilentlyContinue)
        foreach ($lnk in $lnks) {
            $key = $lnk.FullName.ToLowerInvariant()
            if ($seen.ContainsKey($key)) { continue }
            if (-not (Test-LnkIsSteamClient $lnk.FullName $exe $wsh)) { continue }
            # Never create/keep OptiHub-branded desktop entries - remove if found
            $onDesktop = $lnk.FullName -match '(?i)[\\/]Desktop[\\/]'
            if ($onDesktop -and $lnk.Name -match '(?i)OptiHub') {
                try {
                    Remove-Item -LiteralPath $lnk.FullName -Force -ErrorAction SilentlyContinue
                    Write-Ok ("Removed OptiHub desktop shortcut: {0}" -f $lnk.Name)
                } catch { }
                $seen[$key] = $true
                continue
            }
            try {
                Set-SteamShortcutTarget $lnk.FullName $cmdPath $SteamPath $exe $desc $wsh
                $seen[$key] = $true
                $patched++
                Write-Ok ("Shortcut -> OptiHub launcher: {0}" -f $lnk.FullName.Replace($env:USERPROFILE, '~').Replace($env:ProgramData, '%ProgramData%'))
            } catch {
                Write-Warn "Shortcut skip $($lnk.FullName): $($_.Exception.Message)"
            }
        }
    }

    # Ensure Start Menu Steam.lnk only (no Desktop, no Aggressive clone)
    $startSteamDirs = @(
        (Join-Path ([Environment]::GetFolderPath('Programs')) 'Steam'),
        (Join-Path ([Environment]::GetFolderPath('CommonPrograms')) 'Steam')
    )
    foreach ($dir in $startSteamDirs) {
        if (-not $dir) { continue }
        try {
            if (-not (Test-Path -LiteralPath $dir)) {
                New-Item -ItemType Directory -Path $dir -Force | Out-Null
            }
            $mainLnk = Join-Path $dir 'Steam.lnk'
            Set-SteamShortcutTarget $mainLnk $cmdPath $SteamPath $exe $desc $wsh
            $patched++
            Write-Ok "Start Menu Steam.lnk: $mainLnk"

            $aggLnk = Join-Path $dir 'Steam (OptiHub Aggressive).lnk'
            if (Test-Path -LiteralPath $aggLnk) {
                Remove-Item -LiteralPath $aggLnk -Force -ErrorAction SilentlyContinue
                Write-Ok 'Removed Start Menu Aggressive shortcut (now default launcher)'
            }
        } catch {
            Write-Warn "Start Menu Steam folder: $($_.Exception.Message)"
        }
    }

    # Cleanup any leftover OptiHub desktop shortcuts we may have created earlier
    $desktop = [Environment]::GetFolderPath('Desktop')
    foreach ($name in @('Steam (OptiHub Lean).lnk', 'Steam (OptiHub Aggressive).lnk', 'Steam (OptiHub).lnk')) {
        $p = Join-Path $desktop $name
        if (Test-Path -LiteralPath $p) {
            Remove-Item -LiteralPath $p -Force -ErrorAction SilentlyContinue
            Write-Ok "Removed desktop shortcut: $name"
        }
    }

    try {
        $appPaths = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\App Paths\steam.exe'
        if (-not (Test-Path $appPaths)) { New-Item -Path $appPaths -Force | Out-Null }
        Set-ItemProperty -Path $appPaths -Name '(default)' -Value $exe -Force
        Set-ItemProperty -Path $appPaths -Name 'Path' -Value $SteamPath -Force
    } catch { }

    Write-Ok "Updated $patched Steam shortcut(s) (Start Menu / taskbar; no desktop icons created)"
    return @{
        Cmd       = $cmdPath
        Args      = ($Script:DefaultCefArgs -join ' ')
        Shortcuts = $patched
    }
}

function Install-WebHelperTrimHelper([string]$SteamPath) {
    # 5s EmptyWorkingSet always (idle + in-game). No suspend.
    # In-game: lower steam + steamwebhelper priority so the game wins CPU.
    $helper = Join-Path $SteamPath 'OptiHub-SteamWebHelperTrim.ps1'
    $body = @'
# OptiHub - steamwebhelper soft trim every 5s (idle + in-game) + in-game priority yield.
# No NtSuspendProcess (suspend caused FPS cliffs via Steam IPC).
$ErrorActionPreference = 'SilentlyContinue'
Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public static class OptiHubWs {
  [DllImport("psapi.dll")] public static extern bool EmptyWorkingSet(IntPtr hProcess);
  [DllImport("kernel32.dll")] public static extern IntPtr OpenProcess(uint a, bool b, int c);
  [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
  public const uint ACCESS = 0x0500;
}
"@

function Test-SteamGameRunning {
  try {
    $appsKey = 'HKCU:\Software\Valve\Steam\Apps'
    if (Test-Path $appsKey) {
      foreach ($app in @(Get-ChildItem $appsKey -ErrorAction SilentlyContinue)) {
        $props = Get-ItemProperty -LiteralPath $app.PSPath -ErrorAction SilentlyContinue
        if ($props -and [int]$props.Running -eq 1) { return $true }
      }
    }
  } catch {}
  if (Get-Process -Name 'gameoverlayui','gameoverlayui64','GameOverlayUI' -ErrorAction SilentlyContinue) {
    return $true
  }
  return $false
}

function Trim-WebHelpers {
  Get-Process steamwebhelper -ErrorAction SilentlyContinue | ForEach-Object {
    try {
      $h = [OptiHubWs]::OpenProcess([OptiHubWs]::ACCESS, $false, $_.Id)
      if ($h -eq [IntPtr]::Zero) { return }
      try { [void][OptiHubWs]::EmptyWorkingSet($h) }
      finally { [void][OptiHubWs]::CloseHandle($h) }
    } catch {}
  }
}

function Set-SteamClientPriority([bool]$InGame) {
  $cls = if ($InGame) {
    [System.Diagnostics.ProcessPriorityClass]::BelowNormal
  } else {
    [System.Diagnostics.ProcessPriorityClass]::High
  }
  foreach ($name in @('steam', 'steamwebhelper')) {
    Get-Process -Name $name -ErrorAction SilentlyContinue | ForEach-Object {
      try {
        if ($_.PriorityClass -ne $cls) { $_.PriorityClass = $cls }
      } catch {}
    }
  }
}

while (Get-Process steam -ErrorAction SilentlyContinue) {
  $inGame = Test-SteamGameRunning
  Set-SteamClientPriority -InGame:$inGame
  Trim-WebHelpers
  Start-Sleep -Seconds 5
}
'@
    [IO.File]::WriteAllText($helper, $body, [Text.UTF8Encoding]::new($false))
    Write-Ok 'WebHelper helper installed (5s trim always; BELOW_NORMAL steam/webhelper while gaming; no suspend)'
    return $helper
}


function Invoke-SteamRepair([string]$SteamPath) {
    Write-Step 'Repair: restoring backups and stock Steam shortcuts...'
    $restored = 0

    $bak = Join-Path $SteamPath 'config\config.vdf.optihub-bak'
    $cfg = Join-Path $SteamPath 'config\config.vdf'
    if ((Test-Path $bak) -and (Test-Path $cfg)) {
        Copy-Item $bak $cfg -Force
        $restored++
        Write-Ok 'Restored config.vdf'
    }

    Get-ChildItem -LiteralPath (Join-Path $SteamPath 'userdata') -Directory -ErrorAction SilentlyContinue | ForEach-Object {
        $lb = Join-Path $_.FullName 'config\localconfig.vdf.optihub-bak'
        $lf = Join-Path $_.FullName 'config\localconfig.vdf'
        if ((Test-Path $lb) -and (Test-Path $lf)) {
            Copy-Item $lb $lf -Force
            $restored++
            Write-Ok "Restored localconfig user $($_.Name)"
        }
    }

    # Point Steam shortcuts back at steam.exe
    $exe = Join-Path $SteamPath 'steam.exe'
    $wsh = New-Object -ComObject WScript.Shell
    $programs = [Environment]::GetFolderPath('Programs')
    $desktop = [Environment]::GetFolderPath('Desktop')
    $commonPrograms = [Environment]::GetFolderPath('CommonPrograms')
    foreach ($base in @($programs, $desktop, $commonPrograms)) {
        if (-not $base -or -not (Test-Path $base)) { continue }
        Get-ChildItem -LiteralPath $base -Filter 'Steam*.lnk' -Recurse -ErrorAction SilentlyContinue | ForEach-Object {
            try {
                $sc = $wsh.CreateShortcut($_.FullName)
                if ($sc.TargetPath -match 'Steam-OptiHub|OptiHub') {
                    $sc.TargetPath = $exe
                    $sc.Arguments = ''
                    $sc.WorkingDirectory = $SteamPath
                    $sc.Save()
                    $restored++
                    Write-Ok "Shortcut restored: $($_.Name)"
                }
            } catch { }
        }
    }

    foreach ($f in @('Steam-OptiHub.cmd', 'Steam-OptiHub-Aggressive.cmd', 'OptiHub-SteamWebHelperTrim.ps1')) {
        $p = Join-Path $SteamPath $f
        if (Test-Path $p) {
            Remove-Item $p -Force -ErrorAction SilentlyContinue
            Write-Ok "Removed $f"
        }
    }

    $desktop = [Environment]::GetFolderPath('Desktop')
    foreach ($name in @('Steam (OptiHub Lean).lnk', 'Steam (OptiHub Aggressive).lnk', 'Steam (OptiHub).lnk')) {
        $deskLnk = Join-Path $desktop $name
        if (Test-Path $deskLnk) {
            Remove-Item $deskLnk -Force -ErrorAction SilentlyContinue
            Write-Ok "Removed Desktop $name"
        }
    }
    foreach ($dir in @(
        (Join-Path ([Environment]::GetFolderPath('Programs')) 'Steam'),
        (Join-Path ([Environment]::GetFolderPath('CommonPrograms')) 'Steam')
    )) {
        $agg = Join-Path $dir 'Steam (OptiHub Aggressive).lnk'
        if (Test-Path $agg) {
            Remove-Item $agg -Force -ErrorAction SilentlyContinue
            Write-Ok "Removed $agg"
        }
    }

    $statePath = Get-OptiHubSteamStatePath
    if (Test-Path $statePath) {
        Remove-Item $statePath -Force -ErrorAction SilentlyContinue
        Write-Ok 'Cleared OptiHub Steam marker'
    }

    Write-Ok "Repair finished ($restored restore action(s))."
    return $restored
}

# --- main ---
try {
    Write-HubProgress 5 'Starting Steam Optimizer...'
    $steam = Get-SteamInstallPath
    if (-not $steam) {
        throw 'Steam not found. Install Steam, open it once, then rerun OptiHub.'
    }
    Write-Ok "Steam: $steam"
    Write-HubProgress 10 "Steam: $steam"

    Stop-Steam
    Write-HubProgress 20 'Steam closed'

    if ($Repair) {
        Write-HubProgress 40 'Repairing...'
        [void](Invoke-SteamRepair $steam)
        Write-HubProgress 100 'Repair complete'
        exit 0
    }

    Write-HubProgress 30 'Disabling Windows startup...'
    $startupRemoved = Disable-SteamWindowsStartup

    Write-HubProgress 40 'Cleaning webhelper / CEF caches...'
    $freed = 0L
    $shaderFreed = 0L
    if (-not $Quick) {
        $freed = [long](Clear-SteamSafeCaches $steam)
        Write-HubProgress 46 'Clearing shader pre-caches...'
        $shaderFreed = [long](Clear-SteamShaderCaches $steam)
        $freed += $shaderFreed
        Write-HubProgress 50 'Clearing stuck download staging...'
        [void](Optimize-SteamDownloadFolder $steam)
    } else {
        Write-Ok 'Deep cache/shader clean skipped (-Quick) - still applying CEF lean + helpers'
    }
    Write-Ok ("Cache cleanup freed ~{0:N1} MB" -f ($freed / 1MB))

    Write-HubProgress 58 'Installing webhelper trim + priority helper...'
    $helper = Install-WebHelperTrimHelper $steam
    Write-HubProgress 68 'Writing lean + aggressive CEF launchers...'
    $launch = Install-LeanSteamLauncher $steam $helper

    Write-HubProgress 78 'Download speed / config.vdf...'
    $cfgOk = Set-SteamLibraryConfigHints $steam
    Write-HubProgress 88 'Overlay / library / localconfig...'
    $local = Set-SteamLocalConfigTweaks

    Write-HubProgress 94 'Saving status...'
    $state = @{
        version              = $Script:SteamOptVersion
        appliedUtc           = (Get-Date).ToUniversalTime().ToString('o')
        steamPath            = $steam
        startupDisabled      = $true
        startupRemoved       = $startupRemoved
        cacheFreedBytes      = $freed
        shaderCacheFreedBytes = $shaderFreed
        configTouched        = [bool]$cfgOk
        webGpuReduced        = [bool]$local.Gpu
        snappyUi             = $true
        overlayTweaks        = $true
        cefLeanLaunch        = $true
        cefArgs              = ($Script:DefaultCefArgs -join ' ')
        leanCmd              = $launch.Cmd
        webHelperTrim        = $true
        inGamePriorityYield  = $true
        highPriority         = $true
        downloadOptimized    = $true
        noDesktopShortcuts   = $true
        quick                = [bool]$Quick
    }
    Save-SteamOptState $state

    Write-Ok 'Steam Optimizer finished (default CEF quiet launcher, 5s trim, in-game priority yield, shader clean)'
    Write-Ok 'Start Steam from Start Menu / taskbar (no desktop shortcuts created).'
    Write-HubProgress 100 'Completed successfully'
    Write-Output 'DONE - Steam optimized (default CEF launcher + trim + priority yield)'
    exit 0
} catch {
    Write-Err $_.Exception.Message
    Write-HubProgress 100 'Failed'
    exit 1
}
