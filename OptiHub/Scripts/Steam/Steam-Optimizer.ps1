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
$Script:SteamOptVersion = '1.5.0'
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

function Test-SteamGameActive {
    try {
        $appsKey = 'HKCU:\Software\Valve\Steam\Apps'
        if (Test-Path $appsKey) {
            foreach ($app in @(Get-ChildItem $appsKey -ErrorAction SilentlyContinue)) {
                $props = Get-ItemProperty -LiteralPath $app.PSPath -ErrorAction SilentlyContinue
                if ($props -and [int]$props.Running -eq 1) { return $true }
            }
        }
    } catch { }
    return [bool](Get-Process -Name 'GameOverlayUI', 'gameoverlayui64' -ErrorAction SilentlyContinue)
}

function Stop-Steam([string]$SteamPath) {
    Write-Step 'Closing Steam / steamwebhelper...'
    # Never stop the system Steam Client Service and never kill a process merely
    # because it has a common name. Limit tree termination to this Steam install.
    $names = @('steam', 'steamwebhelper', 'GameOverlayUI', 'steamerrorreporter')
    for ($i = 1; $i -le 6; $i++) {
        $procs = @(Get-Process -Name $names -ErrorAction SilentlyContinue | Where-Object {
            try {
                $processPath = $_.Path
                if ($processPath) {
                    return $processPath.StartsWith(
                        $SteamPath.TrimEnd('\') + '\',
                        [StringComparison]::OrdinalIgnoreCase)
                }
            } catch { }

            # These names are Steam-specific. Keep this fallback for short-lived
            # child processes whose Path becomes unavailable while they exit.
            return $_.ProcessName -in @('steam', 'steamwebhelper', 'GameOverlayUI', 'steamerrorreporter')
        })
        if ($procs.Count -eq 0) { break }
        foreach ($p in $procs) {
            try { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue } catch { }
        }
        Start-Sleep -Milliseconds (200 * $i)
    }
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
    $temp = "$path.$PID.$([Guid]::NewGuid().ToString('N')).tmp"
    try {
        [IO.File]::WriteAllText($temp, $json, [Text.UTF8Encoding]::new($false))
        Move-Item -LiteralPath $temp -Destination $path -Force
    } finally {
        Remove-Item -LiteralPath $temp -Force -ErrorAction SilentlyContinue
    }
}

function Read-SteamOptState {
    $path = Get-OptiHubSteamStatePath
    if (-not (Test-Path -LiteralPath $path)) { return $null }
    try { return (Get-Content -LiteralPath $path -Raw -Encoding UTF8 | ConvertFrom-Json) }
    catch { return $null }
}

function Get-SteamRecoveryFromState($State) {
    if (-not $State) { return $null }
    $source = if ($State.PSObject.Properties.Name -contains 'recovery') { $State.recovery } else { $State }
    if (-not $source) { return $null }

    $hasEntries = $source.PSObject.Properties.Name -contains 'startupEntries'
    $hasMode = $source.PSObject.Properties.Name -contains 'startupModeCaptured'
    $hasLegacyMode = $source.PSObject.Properties.Name -contains 'hadStartupMode'
    if (-not $hasEntries -and -not $hasMode -and -not $hasLegacyMode) { return $null }

    return @{
        StartupEntries         = @($source.startupEntries | Where-Object { $_ })
        StartupModeCaptured    = if ($hasMode) { [bool]$source.startupModeCaptured } else { $hasLegacyMode }
        HadStartupMode         = if ($hasLegacyMode) { [bool]$source.hadStartupMode } else { $false }
        PreviousStartupMode    = $source.previousStartupMode
        PreviousStartupModeKind = if ($source.PSObject.Properties.Name -contains 'previousStartupModeKind') {
            [string]$source.previousStartupModeKind
        } else { 'DWord' }
    }
}

function Get-SteamWindowsStartupSnapshot {
    $entries = [Collections.Generic.List[hashtable]]::new()
    $runKeys = @(
        'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run',
        'HKLM:\Software\Microsoft\Windows\CurrentVersion\Run',
        'HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run'
    )
    foreach ($key in $runKeys) {
        if (-not (Test-Path $key)) { continue }
        try {
            $keyItem = Get-Item -Path $key -ErrorAction Stop
            foreach ($name in @($keyItem.GetValueNames())) {
                $value = $keyItem.GetValue(
                    $name,
                    $null,
                    [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
                if ([string]$value -match '(?i)steam\.exe' -or $name -match '(?i)^steam') {
                    $entries.Add(@{
                        Key   = $key
                        Name  = $name
                        Value = $value
                        Kind  = $keyItem.GetValueKind($name).ToString()
                    })
                }
            }
        } catch {
            throw "Could not snapshot Steam startup key ${key}: $($_.Exception.Message)"
        }
    }

    $steamKey = 'HKCU:\Software\Valve\Steam'
    $modeCaptured = $false
    $hadStartupMode = $false
    $previousStartupMode = $null
    $previousStartupModeKind = 'DWord'
    if (Test-Path $steamKey) {
        try {
            $steamKeyItem = Get-Item -Path $steamKey -ErrorAction Stop
            $modeCaptured = $true
            $hadStartupMode = $steamKeyItem.GetValueNames() -contains 'StartupMode'
            if ($hadStartupMode) {
                $previousStartupMode = $steamKeyItem.GetValue(
                    'StartupMode',
                    $null,
                    [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
                $previousStartupModeKind = $steamKeyItem.GetValueKind('StartupMode').ToString()
            }
        } catch {
            throw "Could not snapshot Steam StartupMode: $($_.Exception.Message)"
        }
    }

    return @{
        StartupEntries          = @($entries)
        StartupModeCaptured     = $modeCaptured
        HadStartupMode          = $hadStartupMode
        PreviousStartupMode     = $previousStartupMode
        PreviousStartupModeKind = $previousStartupModeKind
    }
}

function Merge-SteamStartupRecovery($Prior, [hashtable]$Current) {
    $merged = [Collections.Generic.List[hashtable]]::new()
    $seen = @{}
    foreach ($set in @($Prior, $Current)) {
        if (-not $set) { continue }
        foreach ($entry in @($set.StartupEntries | Where-Object { $_ })) {
            $id = (([string]$entry.Key).ToLowerInvariant() + "`0" + ([string]$entry.Name).ToLowerInvariant())
            if ($seen.ContainsKey($id)) { continue }
            $seen[$id] = $true
            $merged.Add(@{
                Key   = [string]$entry.Key
                Name  = [string]$entry.Name
                Value = $entry.Value
                Kind  = [string]$entry.Kind
            })
        }
    }

    $usePriorMode = $Prior -and [bool]$Prior.StartupModeCaptured
    return @{
        StartupEntries          = @($merged)
        StartupModeCaptured     = if ($usePriorMode) { $true } else { [bool]$Current.StartupModeCaptured }
        HadStartupMode          = if ($usePriorMode) { [bool]$Prior.HadStartupMode } else { [bool]$Current.HadStartupMode }
        PreviousStartupMode     = if ($usePriorMode) { $Prior.PreviousStartupMode } else { $Current.PreviousStartupMode }
        PreviousStartupModeKind = if ($usePriorMode) { [string]$Prior.PreviousStartupModeKind } else { [string]$Current.PreviousStartupModeKind }
    }
}

function Disable-SteamWindowsStartup([hashtable]$CurrentSnapshot) {
    $removed = 0
    $success = $true
    foreach ($entry in @($CurrentSnapshot.StartupEntries)) {
        try {
            if (-not (Test-Path $entry.Key)) { continue }
            Remove-ItemProperty -Path $entry.Key -Name $entry.Name -Force -ErrorAction Stop
            $keyItem = Get-Item -Path $entry.Key -ErrorAction Stop
            if ($keyItem.GetValueNames() -contains [string]$entry.Name) {
                throw 'registry value is still present'
            }
            $removed++
            Write-Ok "Removed startup entry: $($entry.Name)"
        } catch {
            $success = $false
            Write-Warn "Could not remove startup entry $($entry.Name): $($_.Exception.Message)"
        }
    }

    try {
        $steamKey = 'HKCU:\Software\Valve\Steam'
        if (-not (Test-Path $steamKey)) { New-Item -Path $steamKey -Force -ErrorAction Stop | Out-Null }
        New-ItemProperty -Path $steamKey -Name 'StartupMode' -PropertyType DWord -Value 0 -Force -ErrorAction Stop | Out-Null
        if ([int](Get-ItemPropertyValue -Path $steamKey -Name 'StartupMode' -ErrorAction Stop) -ne 0) {
            throw 'StartupMode verification failed'
        }
        Write-Ok 'Steam StartupMode = 0 (do not auto-start)'
    } catch {
        $success = $false
        Write-Warn "Could not set StartupMode: $($_.Exception.Message)"
    }

    return @{
        Count   = $removed
        Success = $success
    }
}

function Test-SteamWindowsStartupDisabled {
    foreach ($key in @(
        'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run',
        'HKLM:\Software\Microsoft\Windows\CurrentVersion\Run',
        'HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run'
    )) {
        if (-not (Test-Path $key)) { continue }
        try {
            $keyItem = Get-Item -Path $key -ErrorAction Stop
            foreach ($name in @($keyItem.GetValueNames())) {
                $value = $keyItem.GetValue($name)
                if ([string]$value -match '(?i)steam\.exe' -or $name -match '(?i)^steam') { return $false }
            }
        } catch { return $false }
    }
    try {
        return [int](Get-ItemPropertyValue -Path 'HKCU:\Software\Valve\Steam' -Name 'StartupMode' -ErrorAction Stop) -eq 0
    } catch { return $false }
}

function Get-SteamLibraryRoots([string]$SteamPath) {
    $Script:SteamLibraryInventoryVerified = $true
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
        } catch {
            $Script:SteamLibraryInventoryVerified = $false
            Write-Warn "Could not inventory Steam libraries: $($_.Exception.Message)"
        }
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
            if (Test-Path -LiteralPath $item.FullName) {
                Write-Warn ("Could not fully clear {0}" -f $item.FullName)
                return 0L
            }
            $freed = $size
            if ($size -gt 0) {
                Write-Ok ("Cleared {0} (~{1:N1} MB)" -f $item.Name, ($size / 1MB))
            }
        } else {
            $freed = [long]$item.Length
            Remove-Item -LiteralPath $item.FullName -Force -ErrorAction SilentlyContinue
            if (Test-Path -LiteralPath $item.FullName) { return 0L }
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
            'appcache\httpcache', 'appcache\shader'
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
    # Keep caches for installed games (FPS / traversal-stutter critical), but
    # reclaim orphaned shader data for app IDs no longer installed anywhere.
    Write-Step 'Cleaning orphaned Steam shader pre-caches...'
    $libraries = @(Get-SteamLibraryRoots $SteamPath)
    if (-not $Script:SteamLibraryInventoryVerified) {
        Write-Warn 'Shader cleanup skipped: Steam library inventory is not trustworthy'
        return @{ Freed = 0L; InventoryVerified = $false; Removed = 0 }
    }

    $installed = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $inventoryVerified = $true
    foreach ($lib in $libraries) {
        $steamApps = Join-Path $lib 'steamapps'
        $shaderRoot = Join-Path $steamApps 'shadercache'
        if (-not (Test-Path -LiteralPath $steamApps)) {
            if (Test-Path -LiteralPath $shaderRoot) { $inventoryVerified = $false }
            continue
        }
        try {
            $manifests = @(Get-ChildItem -LiteralPath $steamApps -Filter 'appmanifest_*.acf' -File -ErrorAction Stop)
            foreach ($manifest in $manifests) {
                if ($manifest.BaseName -match '^appmanifest_(\d+)$') { [void]$installed.Add($Matches[1]) }
            }
        } catch {
            $inventoryVerified = $false
            Write-Warn "Shader cleanup inventory failed for $steamApps`: $($_.Exception.Message)"
        }
    }

    $numericShaderCaches = @()
    foreach ($lib in $libraries) {
        $shaderRoot = Join-Path $lib 'steamapps\shadercache'
        if (-not (Test-Path -LiteralPath $shaderRoot)) { continue }
        try {
            $numericShaderCaches += @(Get-ChildItem -LiteralPath $shaderRoot -Directory -Force -ErrorAction Stop |
                Where-Object { $_.Name -match '^\d+$' })
        } catch {
            $inventoryVerified = $false
            Write-Warn "Shader cache inventory failed for $shaderRoot`: $($_.Exception.Message)"
        }
    }

    if (-not $inventoryVerified -or ($numericShaderCaches.Count -gt 0 -and $installed.Count -eq 0)) {
        Write-Warn 'Shader cleanup skipped: installed-game manifest inventory is unreadable or ambiguous'
        return @{ Freed = 0L; InventoryVerified = $false; Removed = 0 }
    }

    [long]$freed = 0
    $removed = 0
    foreach ($dir in $numericShaderCaches) {
        if ($installed.Contains($dir.Name)) { continue }
        $beforeExists = Test-Path -LiteralPath $dir.FullName
        $freed += Clear-PathTree $dir.FullName
        if ($beforeExists -and -not (Test-Path -LiteralPath $dir.FullName)) { $removed++ }
    }
    if ($removed -gt 0) {
        Write-Ok ("Removed {0} orphaned shader cache(s), freed ~{1:N1} MB" -f $removed, ($freed / 1MB))
    } else {
        Write-Ok 'Installed-game shader pre-caches preserved; no orphaned caches found'
    }
    return @{ Freed = $freed; InventoryVerified = $true; Removed = $removed }
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

function Test-SteamVdfExpectations([string]$Raw, [object[]]$Expectations) {
    $observed = 0
    $valid = $true
    foreach ($pair in $Expectations) {
        $pattern = '"' + [regex]::Escape([string]$pair.K) + '"\s+"([^"]*)"'
        $matches = [regex]::Matches($Raw, $pattern)
        $observed += $matches.Count
        foreach ($match in $matches) {
            if ($match.Groups[1].Value -ne [string]$pair.V) { $valid = $false }
        }
    }
    return @{ Valid = $valid; Observed = $observed }
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
    $verificationOk = $true
    $verificationObserved = 0
    $expectations = @(
        @{ K = 'H264HWAccel'; V = '0' },
        @{ K = 'GPUAccelWebViews'; V = '0' },
        @{ K = 'GPUAccelWebViews2'; V = '0' },
        @{ K = 'GPUAccelWebViewsD3D11'; V = '0' },
        @{ K = 'SmoothScrollWebViews'; V = '0' },
        @{ K = 'LibraryLowBandwidthMode'; V = '0' },
        @{ K = 'LibraryLowPerfMode'; V = '0' },
        @{ K = 'StartupMovieMode'; V = '0' },
        @{ K = 'LibraryDisableCommunityContent'; V = '1' },
        @{ K = 'LibraryDisplayIconInGameList'; V = '0' },
        @{ K = 'EnableGameOverlay'; V = '1' },
        @{ K = 'InGameOverlayScreenshotNotification'; V = '0' },
        @{ K = 'InGameOverlayShowFPSCounterHotKey'; V = '0' },
        @{ K = 'SteamInputConfigEnabled'; V = '1' },
        @{ K = 'Controller_EnableChrome'; V = '0' },
        @{ K = 'BigPictureInForeground'; V = '0' },
        @{ K = 'NotifyAvailableGames'; V = '0' },
        @{ K = 'SoundPlay_DownloadComplete'; V = '0' },
        @{ K = 'SoundPlay_FriendOnline'; V = '0' },
        @{ K = 'FriendsAlwaysShowAvatars'; V = '0' },
        @{ K = 'AllowDownloadsDuringGameplay'; V = '0' },
        @{ K = 'CloudEnabled'; V = '1' }
    )
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
            $verification = Test-SteamVdfExpectations $raw $expectations
            $verificationObserved += [int]$verification.Observed
            if (-not $verification.Valid) { $verificationOk = $false }
        } catch {
            $verificationOk = $false
            Write-Warn "localconfig.vdf: $($_.Exception.Message)"
        }
    }

    if (-not $anyPatched) {
        Write-Ok 'localconfig.vdf: no matching keys - CEF launch flags + download config still apply'
    }

    return @{
        Gpu     = $anyGpu
        Patched = $anyPatched
        Path    = $lastPath
        Snappy  = $anySnappy
        Verified = ($files.Count -gt 0 -and $verificationObserved -gt 0 -and $verificationOk)
    }
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

        $verification = Test-SteamVdfExpectations $raw @(
            @{ K = 'DownloadThrottleKbps'; V = '0' },
            @{ K = 'ThrottleKbps'; V = '0' },
            @{ K = 'RateLimitBps'; V = '0' },
            @{ K = 'MaxSimDownloads'; V = '8' },
            @{ K = 'AutoUpdateWindowEnabled'; V = '0' }
        )
        if (-not $verification.Valid) {
            Write-Warn 'config.vdf verification found a conflicting download value'
            return $false
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
    # Partial downloads are resumable user data, not cache. Report them but leave
    # them intact; deleting these folders can discard many gigabytes of progress.
    $dirs = @(
        (Join-Path $SteamPath 'steamapps\downloading'),
        (Join-Path $SteamPath 'steamapps\temp'),
        (Join-Path $SteamPath 'steamapps\workshop\downloads')
    )
    $n = 0
    foreach ($d in $dirs) {
        if (-not (Test-Path $d)) { continue }
        $n += @(Get-ChildItem -LiteralPath $d -Force -ErrorAction SilentlyContinue).Count
    }
    if ($n -gt 0) { Write-Ok "Preserved $n resumable download/workshop item(s)" }
    else { Write-Ok 'Download staging folders already clean' }
    return $n
}

function Write-SteamLaunchCmd([string]$CmdPath, [string]$SteamPath, [string]$HelperPath, [string[]]$CefArgs, [string]$Label) {
    $exe = Join-Path $SteamPath 'steam.exe'
    $args = ($CefArgs -join ' ')
    $ps = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    # Percent signs are expanded while a .cmd file is parsed.
    $cmdSteamPath = $SteamPath.Replace('%', '%%')
    $cmdExe = $exe.Replace('%', '%%')
    $cmdHelper = $HelperPath.Replace('%', '%%')
    $cmdPs = $ps.Replace('%', '%%')
    $cmd = @(
        '@echo off'
        ("rem OptiHub {0} - aggressive webhelper trim + in-game priority yield" -f $Label)
        ('start "" /MIN "{0}" -NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File "{1}"' -f $cmdPs, $cmdHelper)
        ('start "" /HIGH /D "{0}" "{1}" {2} %*' -f $cmdSteamPath, $cmdExe, $args)
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
        $arguments = [string]$sc.Arguments
        if ([string]::IsNullOrWhiteSpace($target)) { return $false }
        # Game shortcuts also target steam.exe, usually with -applaunch or a
        # steam:// URI. Rewriting one and clearing its arguments breaks the game.
        if ($arguments -match '(?i)(^|\s)-applaunch\b|steam://|(^|\s)-(install|uninstall|shutdown)\b') {
            return $false
        }
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
    $existingArguments = [string]$sc.Arguments
    $sc.TargetPath = $TargetCmd
    # Preserve harmless client flags such as -silent. The generated launcher
    # forwards %*, while Test-LnkIsSteamClient excludes game/action arguments.
    $sc.Arguments = $existingArguments
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
    $desc = 'Steam (OptiHub - quiet CEF + aggressive 5s webhelper trim)'

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
        (Join-Path ([Environment]::GetFolderPath('Programs')) 'Steam')
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

    # Never leave Steam / OptiHub icons on the Desktop (user or public).
    foreach ($desktop in @(
        [Environment]::GetFolderPath('Desktop'),
        [Environment]::GetFolderPath('CommonDesktopDirectory')
    )) {
        if (-not $desktop -or -not (Test-Path -LiteralPath $desktop)) { continue }
        foreach ($name in @(
            'Steam.lnk', 'Steam (OptiHub Lean).lnk', 'Steam (OptiHub Aggressive).lnk', 'Steam (OptiHub).lnk'
        )) {
            $p = Join-Path $desktop $name
            if (Test-Path -LiteralPath $p) {
                Remove-Item -LiteralPath $p -Force -ErrorAction SilentlyContinue
                Write-Ok "Removed desktop shortcut: $name"
            }
        }
        Get-ChildItem -LiteralPath $desktop -Filter 'Steam*.lnk' -Force -ErrorAction SilentlyContinue | ForEach-Object {
            try { Remove-Item -LiteralPath $_.FullName -Force -ErrorAction SilentlyContinue; Write-Ok "Removed desktop: $($_.Name)" } catch { }
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
    # Maximum-performance helper: one instance, high client priority while idle,
    # an in-game CPU yield, and a 5-second working-set trim with no suspension.
    $helper = Join-Path $SteamPath 'OptiHub-SteamWebHelperTrim.ps1'
    $body = @'
# OptiHub - aggressive 5s steamwebhelper trim + in-game priority yield.
# No process suspension (suspension can break Steam IPC and overlay behavior).
$ErrorActionPreference = 'SilentlyContinue'
$created = $false
$mutex = [Threading.Mutex]::new($true, 'Local\OptiHub.SteamWebHelper', [ref]$created)
if (-not $created) { $mutex.Dispose(); exit 0 }
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

try {
  # The helper is started immediately before steam.exe; wait for the client so
  # a scheduling race does not make the helper exit before Steam appears.
  $startupDeadline = (Get-Date).AddSeconds(30)
  while (-not (Get-Process steam -ErrorAction SilentlyContinue) -and (Get-Date) -lt $startupDeadline) {
    Start-Sleep -Milliseconds 250
  }

  while (Get-Process steam -ErrorAction SilentlyContinue) {
    $inGame = Test-SteamGameRunning
    Set-SteamClientPriority -InGame:$inGame
    Trim-WebHelpers
    Start-Sleep -Seconds 5
  }
} finally {
  try { $mutex.ReleaseMutex() } catch {}
  $mutex.Dispose()
}
'@
    [IO.File]::WriteAllText($helper, $body, [Text.UTF8Encoding]::new($false))
    Write-Ok 'WebHelper helper installed (single instance; aggressive 5s trim; in-game priority yield)'
    return $helper
}


function Invoke-SteamRepair([string]$SteamPath) {
    Write-Step 'Repair: restoring backups and stock Steam shortcuts...'
    $restored = 0
    $failures = [Collections.Generic.List[string]]::new()
    $statePath = Get-OptiHubSteamStatePath
    $state = Read-SteamOptState
    $recovery = Get-SteamRecoveryFromState $state
    if ($state) {
        Save-SteamOptState @{
            version        = $Script:SteamOptVersion
            applyStatus    = 'repairing'
            applied        = $false
            recovery       = $recovery
            repairFailures = @()
            repairStartedUtc = (Get-Date).ToUniversalTime().ToString('o')
        }
    }

    $bak = Join-Path $SteamPath 'config\config.vdf.optihub-bak'
    $cfg = Join-Path $SteamPath 'config\config.vdf'
    if (Test-Path $bak) {
        Copy-Item $bak $cfg -Force
        Remove-Item $bak -Force -ErrorAction SilentlyContinue
        $restored++
        Write-Ok 'Restored config.vdf'
    }

    Get-ChildItem -LiteralPath (Join-Path $SteamPath 'userdata') -Directory -ErrorAction SilentlyContinue | ForEach-Object {
        $lb = Join-Path $_.FullName 'config\localconfig.vdf.optihub-bak'
        $lf = Join-Path $_.FullName 'config\localconfig.vdf'
        if (Test-Path $lb) {
            Copy-Item $lb $lf -Force
            Remove-Item $lb -Force -ErrorAction SilentlyContinue
            $restored++
            Write-Ok "Restored localconfig user $($_.Name)"
        }
    }

    # Point Steam shortcuts back at steam.exe
    $exe = Join-Path $SteamPath 'steam.exe'
    $wsh = New-Object -ComObject WScript.Shell
    foreach ($base in (Get-SteamShortcutSearchRoots)) {
        Get-ChildItem -LiteralPath $base -Filter '*.lnk' -Recurse -ErrorAction SilentlyContinue | ForEach-Object {
            $linkPath = $_.FullName
            try {
                $sc = $wsh.CreateShortcut($linkPath)
                if ($sc.TargetPath -match 'Steam-OptiHub|OptiHub') {
                    $sc.TargetPath = $exe
                    $sc.WorkingDirectory = $SteamPath
                    $sc.IconLocation = "$exe,0"
                    $sc.Save()
                    $restored++
                    Write-Ok "Shortcut restored: $($_.Name)"
                }
            } catch {
                $failures.Add("Shortcut $linkPath`: $($_.Exception.Message)")
            }
        }
    }

    foreach ($f in @('Steam-OptiHub.cmd', 'Steam-OptiHub-Aggressive.cmd', 'OptiHub-SteamWebHelperTrim.ps1')) {
        $p = Join-Path $SteamPath $f
        if (Test-Path $p) {
            try {
                Remove-Item -LiteralPath $p -Force -ErrorAction Stop
                if (Test-Path -LiteralPath $p) { throw 'file is still present' }
                Write-Ok "Removed $f"
            } catch {
                $failures.Add("Remove $f`: $($_.Exception.Message)")
            }
        }
    }

    $desktop = [Environment]::GetFolderPath('Desktop')
    foreach ($name in @('Steam (OptiHub Lean).lnk', 'Steam (OptiHub Aggressive).lnk', 'Steam (OptiHub).lnk')) {
        $deskLnk = Join-Path $desktop $name
        if (Test-Path $deskLnk) {
            try {
                Remove-Item -LiteralPath $deskLnk -Force -ErrorAction Stop
                if (Test-Path -LiteralPath $deskLnk) { throw 'shortcut is still present' }
                Write-Ok "Removed Desktop $name"
            } catch { $failures.Add("Remove Desktop $name`: $($_.Exception.Message)") }
        }
    }
    foreach ($dir in @(
        (Join-Path ([Environment]::GetFolderPath('Programs')) 'Steam'),
        (Join-Path ([Environment]::GetFolderPath('CommonPrograms')) 'Steam')
    )) {
        $agg = Join-Path $dir 'Steam (OptiHub Aggressive).lnk'
        if (Test-Path $agg) {
            try {
                Remove-Item -LiteralPath $agg -Force -ErrorAction Stop
                if (Test-Path -LiteralPath $agg) { throw 'shortcut is still present' }
                Write-Ok "Removed $agg"
            } catch { $failures.Add("Remove $agg`: $($_.Exception.Message)") }
        }
    }

    if ($recovery) {
        foreach ($entry in @($recovery.StartupEntries)) {
            try {
                if (-not (Test-Path $entry.Key)) { New-Item -Path $entry.Key -Force -ErrorAction Stop | Out-Null }
                $kind = if ([string]$entry.Kind -in @('String', 'ExpandString', 'Binary', 'DWord', 'MultiString', 'QWord')) {
                    [string]$entry.Kind
                } else { 'String' }
                $value = switch ($kind) {
                    'Binary' { [byte[]]$entry.Value; break }
                    'DWord' { [int]$entry.Value; break }
                    'QWord' { [long]$entry.Value; break }
                    'MultiString' { [string[]]$entry.Value; break }
                    default { [string]$entry.Value }
                }
                New-ItemProperty -Path $entry.Key -Name ([string]$entry.Name) -Value $value -PropertyType $kind -Force -ErrorAction Stop | Out-Null
                $keyItem = Get-Item -Path $entry.Key -ErrorAction Stop
                if ($keyItem.GetValueNames() -notcontains [string]$entry.Name) { throw 'registry value is missing after restore' }
                $actualKind = $keyItem.GetValueKind([string]$entry.Name).ToString()
                $actualValue = $keyItem.GetValue(
                    [string]$entry.Name,
                    $null,
                    [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
                if ($actualKind -ne $kind -or
                    (($actualValue | ConvertTo-Json -Compress -Depth 4) -ne ($value | ConvertTo-Json -Compress -Depth 4))) {
                    throw 'registry value verification failed'
                }
                $restored++
                Write-Ok "Restored startup entry: $($entry.Name)"
            } catch {
                $failure = "Startup entry $($entry.Name): $($_.Exception.Message)"
                $failures.Add($failure)
                Write-Warn "Could not restore $failure"
            }
        }

        if ([bool]$recovery.StartupModeCaptured) {
            try {
                $steamKey = 'HKCU:\Software\Valve\Steam'
                if (-not (Test-Path $steamKey)) { New-Item -Path $steamKey -Force -ErrorAction Stop | Out-Null }
                if ([bool]$recovery.HadStartupMode) {
                    $kind = if ([string]$recovery.PreviousStartupModeKind -in @('String', 'ExpandString', 'Binary', 'DWord', 'MultiString', 'QWord')) {
                        [string]$recovery.PreviousStartupModeKind
                    } else { 'DWord' }
                    New-ItemProperty -Path $steamKey -Name 'StartupMode' -PropertyType $kind -Value $recovery.PreviousStartupMode -Force -ErrorAction Stop | Out-Null
                    $keyItem = Get-Item -Path $steamKey -ErrorAction Stop
                    $actual = $keyItem.GetValue('StartupMode', $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
                    if ($keyItem.GetValueKind('StartupMode').ToString() -ne $kind -or
                        (($actual | ConvertTo-Json -Compress) -ne ($recovery.PreviousStartupMode | ConvertTo-Json -Compress))) {
                        throw 'StartupMode verification failed'
                    }
                } else {
                    Remove-ItemProperty -Path $steamKey -Name 'StartupMode' -Force -ErrorAction SilentlyContinue
                    if ((Get-Item -Path $steamKey -ErrorAction Stop).GetValueNames() -contains 'StartupMode') {
                        throw 'StartupMode is still present'
                    }
                }
                Write-Ok 'Restored Steam StartupMode'
            } catch {
                $failure = "StartupMode: $($_.Exception.Message)"
                $failures.Add($failure)
                Write-Warn "Could not restore $failure"
            }
        }
    }

    if ($failures.Count -gt 0) {
        Save-SteamOptState @{
            version          = $Script:SteamOptVersion
            applyStatus      = 'repair-pending'
            applied          = $false
            recovery         = $recovery
            repairFailures   = @($failures)
            lastRepairUtc    = (Get-Date).ToUniversalTime().ToString('o')
        }
        throw "Steam repair incomplete ($($failures.Count) item(s)); recovery state was kept for retry"
    }

    if (Test-Path $statePath) {
        Remove-Item -LiteralPath $statePath -Force -ErrorAction Stop
        if (Test-Path -LiteralPath $statePath) { throw 'Could not clear Steam recovery marker' }
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

    if (Test-SteamGameActive) {
        throw 'A Steam game appears to be running. Close the game before optimizing or repairing Steam.'
    }

    Stop-Steam $steam
    Write-HubProgress 20 'Steam closed'

    if ($Repair) {
        Write-HubProgress 40 'Repairing...'
        [void](Invoke-SteamRepair $steam)
        Write-HubProgress 100 'Repair complete'
        exit 0
    }

    # Persist recovery before the first mutation and invalidate any prior
    # applied marker. Reapply merges newly discovered entries while preserving
    # the original pre-OptiHub value for every key already captured.
    $priorState = Read-SteamOptState
    $priorRecovery = Get-SteamRecoveryFromState $priorState
    $currentStartup = Get-SteamWindowsStartupSnapshot
    $recovery = Merge-SteamStartupRecovery $priorRecovery $currentStartup
    Save-SteamOptState @{
        version         = $Script:SteamOptVersion
        applyStatus     = 'applying'
        applied         = $false
        applyStartedUtc = (Get-Date).ToUniversalTime().ToString('o')
        steamPath       = $steam
        recovery        = $recovery
    }

    # Remove leftover OptiHub/Steam client junk that can conflict with lean launch
    Write-HubProgress 25 'Clearing conflicting Steam leftovers...'
    foreach ($f in @(
        (Join-Path $steam 'Steam-OptiHub-Aggressive.cmd'),
        (Join-Path ([Environment]::GetFolderPath('Desktop')) 'Steam (OptiHub Lean).lnk'),
        (Join-Path ([Environment]::GetFolderPath('Desktop')) 'Steam (OptiHub Aggressive).lnk'),
        (Join-Path ([Environment]::GetFolderPath('Desktop')) 'Steam (OptiHub).lnk')
    )) {
        if (Test-Path -LiteralPath $f) {
            Remove-Item -LiteralPath $f -Force -ErrorAction SilentlyContinue
            Write-Ok "Removed conflict leftover: $(Split-Path $f -Leaf)"
        }
    }
    # Stale CEF crashpads / htmlcache that fight fresh lean flags
    foreach ($d in @(
        (Join-Path $env:LOCALAPPDATA 'Steam\htmlcache\Crashpad'),
        (Join-Path $env:LOCALAPPDATA 'Steam\Crashpad')
    )) {
        if (Test-Path $d) {
            try { Remove-Item $d -Recurse -Force -ErrorAction SilentlyContinue; Write-Ok "Cleared $d" } catch { }
        }
    }

    Write-HubProgress 30 'Disabling Windows startup...'
    $startupResult = Disable-SteamWindowsStartup $currentStartup
    if (-not $startupResult.Success -or -not (Test-SteamWindowsStartupDisabled)) {
        throw 'Steam startup suppression could not be fully verified; recovery state was kept'
    }

    Write-HubProgress 40 'Cleaning webhelper / CEF caches...'
    $freed = 0L
    $shaderFreed = 0L
    $shaderInventoryVerified = $false
    if (-not $Quick) {
        $freed = [long](Clear-SteamSafeCaches $steam)
        Write-HubProgress 46 'Cleaning orphaned shader pre-caches...'
        $shaderResult = Clear-SteamShaderCaches $steam
        $shaderFreed = [long]$shaderResult.Freed
        $shaderInventoryVerified = [bool]$shaderResult.InventoryVerified
        if (-not $shaderInventoryVerified) {
            throw 'Shader cleanup stopped because the installed-game manifest inventory was unreadable or ambiguous'
        }
        $freed += $shaderFreed
        Write-HubProgress 50 'Checking resumable downloads...'
        [void](Optimize-SteamDownloadFolder $steam)
    } else {
        Write-Ok 'Deep cache/shader clean skipped (-Quick) - still applying CEF lean + helpers'
    }
    Write-Ok ("Cache cleanup freed ~{0:N1} MB" -f ($freed / 1MB))

    Write-HubProgress 58 'Installing aggressive webhelper helper...'
    $helper = Install-WebHelperTrimHelper $steam
    Write-HubProgress 68 'Writing quiet CEF launcher...'
    $launch = Install-LeanSteamLauncher $steam $helper

    Write-HubProgress 78 'Download speed / config.vdf...'
    $cfgOk = Set-SteamLibraryConfigHints $steam
    Write-HubProgress 88 'Overlay / library / localconfig...'
    $local = Set-SteamLocalConfigTweaks

    Write-HubProgress 94 'Saving status...'
    $startupOk = Test-SteamWindowsStartupDisabled
    $launcherOk = $false
    try {
        $launcherText = Get-Content -LiteralPath $launch.Cmd -Raw -ErrorAction Stop
        $launcherOk = $launcherText -match '(?i)steam\.exe' -and
            $launcherText -match '(?i)-cef-disable-gpu' -and
            $launcherText -match '(?i)start\s+""\s+/HIGH'
    } catch { }
    $helperOk = $false
    try {
        $helperText = Get-Content -LiteralPath $helper -Raw -ErrorAction Stop
        $helperOk = $helperText -match 'OptiHub\.SteamWebHelper' -and
            $helperText -match 'EmptyWorkingSet' -and
            $helperText -match 'Start-Sleep -Seconds 5' -and
            $helperText -match 'ProcessPriorityClass\]::High' -and
            $helperText -match 'ProcessPriorityClass\]::BelowNormal'
    } catch { }
    $clientTweaksOk = [bool]$local.Verified
    $fullPassOk = -not [bool]$Quick
    $essentialOk = $startupOk -and $launcherOk -and $helperOk -and [bool]$cfgOk -and
        $clientTweaksOk -and $fullPassOk -and $shaderInventoryVerified
    $state = @{
        version              = $Script:SteamOptVersion
        applyStatus          = if ($essentialOk) { 'applied' } else { 'incomplete' }
        applied              = $essentialOk
        appliedUtc           = (Get-Date).ToUniversalTime().ToString('o')
        steamPath            = $steam
        recovery             = $recovery
        startupDisabled      = $startupOk
        startupRemoved       = [int]$startupResult.Count
        startupEntries       = @($recovery.StartupEntries)
        startupModeCaptured  = [bool]$recovery.StartupModeCaptured
        hadStartupMode       = [bool]$recovery.HadStartupMode
        previousStartupMode  = $recovery.PreviousStartupMode
        previousStartupModeKind = $recovery.PreviousStartupModeKind
        cacheFreedBytes      = $freed
        cacheCleanupCompleted = $fullPassOk
        shaderCacheFreedBytes = $shaderFreed
        shaderInventoryVerified = $shaderInventoryVerified
        configTouched        = [bool]$cfgOk
        configVerified       = [bool]$cfgOk
        clientTweaksVerified = $clientTweaksOk
        webGpuReduced        = $clientTweaksOk
        snappyUi             = $clientTweaksOk
        overlayTweaks        = $clientTweaksOk
        cefLeanLaunch        = $launcherOk
        cefArgs              = ($Script:DefaultCefArgs -join ' ')
        leanCmd              = $launch.Cmd
        webHelperTrim        = $helperOk
        aggressiveTrim       = $helperOk
        inGamePriorityYield  = $helperOk
        highPriority         = $helperOk
        downloadOptimized    = [bool]$cfgOk
        installedShaderCachesPreserved = $shaderInventoryVerified
        noDesktopShortcuts   = $true
        quick                = [bool]$Quick
    }
    Save-SteamOptState $state

    if (-not $essentialOk) {
        if ($Quick) {
            Write-Warn 'Quick pass completed, but the full no-compromise applied state remains incomplete by design'
            Write-HubProgress 100 'Quick pass complete (full apply still required)'
            Write-Output 'DONE - Steam quick pass complete; run full Apply for verified no-compromise state'
            exit 0
        }
        throw 'Steam apply finished with an incomplete live startup/config/client verification state'
    }

    Write-Ok 'Steam Optimizer finished (quiet CEF launcher, aggressive 5s trim, in-game priority yield)'
    Write-Ok 'Start Steam from Start Menu / taskbar (no desktop shortcuts created).'
    Write-HubProgress 100 'Completed successfully'
    Write-Output 'DONE - Steam optimized (quiet CEF launcher + aggressive trim + priority yield)'
    exit 0
} catch {
    $failureRecord = $_
    if ($Repair) {
        try {
            $failedState = Read-SteamOptState
            if ($failedState) {
                $failedRecovery = Get-SteamRecoveryFromState $failedState
                $recordedFailures = @()
                if ($failedState.PSObject.Properties.Name -contains 'repairFailures') {
                    $recordedFailures = @($failedState.repairFailures)
                }
                Save-SteamOptState @{
                    version        = $Script:SteamOptVersion
                    applyStatus    = 'repair-pending'
                    applied        = $false
                    recovery       = $failedRecovery
                    repairFailures = @($recordedFailures) + @([string]$failureRecord.Exception.Message)
                    lastRepairUtc  = (Get-Date).ToUniversalTime().ToString('o')
                }
            }
        } catch { }
    }
    Write-Err $failureRecord.Exception.Message
    Write-HubProgress 100 'Failed'
    exit 1
}
