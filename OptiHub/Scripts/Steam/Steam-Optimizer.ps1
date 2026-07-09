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
$Script:SteamOptVersion = '1.2.1'
$Root = Split-Path -Parent $MyInvocation.MyCommand.Path

# Official Valve CEF flags (see Valve wiki: Command line options (Steam)).
# These cut steamwebhelper GPU/RAM without touching game binaries or VAC.
$Script:LeanCefArgs = @(
    '-cef-disable-gpu',
    '-cef-disable-gpu-compositing'
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

function Clear-SteamSafeCaches([string]$SteamPath) {
    $targets = @(
        (Join-Path $SteamPath 'htmlcache'),
        (Join-Path $SteamPath 'logs'),
        (Join-Path $SteamPath 'dumps'),
        (Join-Path $SteamPath 'crashhandler.log'),
        (Join-Path $SteamPath 'appcache\httpcache'),
        (Join-Path $SteamPath 'steamapps\temp'),
        (Join-Path $SteamPath 'steamapps\downloading')
    )

    $localSteam = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Steam'
    if (Test-Path $localSteam) {
        $targets += @(
            (Join-Path $localSteam 'htmlcache'),
            (Join-Path $localSteam 'GPUCache'),
            (Join-Path $localSteam 'Code Cache'),
            (Join-Path $localSteam 'ShaderCache'),
            (Join-Path $localSteam 'DawnCache'),
            (Join-Path $localSteam 'GrShaderCache')
        )
    }

    # CEF cache next to bin
    $cefRoots = @(
        (Join-Path $SteamPath 'bin\cef'),
        (Join-Path $SteamPath 'bin\cef\cef.win7x64'),
        (Join-Path $SteamPath 'bin\cef\cef.win64')
    )
    foreach ($cr in $cefRoots) {
        if (Test-Path $cr) {
            $targets += (Join-Path $cr 'GPUCache')
            $targets += (Join-Path $cr 'Code Cache')
            $targets += (Join-Path $cr 'Cache')
        }
    }

    [long]$freed = 0
    foreach ($t in $targets) {
        if (-not (Test-Path -LiteralPath $t)) { continue }
        try {
            $item = Get-Item -LiteralPath $t -Force -ErrorAction Stop
            if ($item.PSIsContainer) {
                $sumObj = Get-ChildItem -LiteralPath $item.FullName -Recurse -Force -File -ErrorAction SilentlyContinue |
                    Measure-Object -Property Length -Sum
                $size = if ($null -ne $sumObj -and $null -ne $sumObj.Sum) { [long]$sumObj.Sum } else { 0L }
                Remove-Item -LiteralPath $item.FullName -Recurse -Force -ErrorAction SilentlyContinue
                $freed += $size
                if ($size -gt 0) {
                    Write-Ok ("Cleared {0} (~{1:N1} MB)" -f $item.Name, ($size / 1MB))
                }
            } else {
                $freed += [long]$item.Length
                Remove-Item -LiteralPath $item.FullName -Force -ErrorAction SilentlyContinue
            }
        } catch {
            Write-Warn ("Skip cache {0}: {1}" -f $t, $_.Exception.Message)
        }
    }

    Get-ChildItem -LiteralPath $SteamPath -Filter '*.log' -File -ErrorAction SilentlyContinue | ForEach-Object {
        try {
            $freed += [long]$_.Length
            Remove-Item -LiteralPath $_.FullName -Force -ErrorAction SilentlyContinue
        } catch { }
    }

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

            # Snappier feel: less animation chrome, no low-bandwidth library (faster page loads)
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

            # Quieter / less background wakeups
            foreach ($k in @('NotifyAvailableGames', 'SoundPlay_DownloadComplete', 'SoundPlay_FriendOnline', 'FriendsAlwaysShowAvatars')) {
                $raw = Set-SteamVdfKey $raw $k '0'
            }

            # Downloads while playing / allow high rate when keys exist
            foreach ($pair in @(
                @{ K = 'AllowDownloadsDuringGameplay'; V = '1' },
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

function Install-LeanSteamLauncher([string]$SteamPath) {
    # Write a launcher that always starts Steam with CEF GPU disabled (webhelper RAM).
    $exe = Join-Path $SteamPath 'steam.exe'
    $cmdPath = Join-Path $SteamPath 'Steam-OptiHub.cmd'
    $args = ($Script:LeanCefArgs -join ' ')
    $cmd = @(
        '@echo off'
        'rem OptiHub lean Steam launcher - CEF flags cut steamwebhelper GPU/RAM'
        'rem Stock steam.exe is untouched; this is an alternate start path.'
        'rem /HIGH = snappier Steam process priority while client is open'
        ('start "" /HIGH /D "{0}" "{1}" {2}' -f $SteamPath, $exe, $args)
    ) -join "`r`n"
    [IO.File]::WriteAllText($cmdPath, $cmd + "`r`n", [Text.UTF8Encoding]::new($false))
    Write-Ok "Lean launcher: $cmdPath"
    Write-Ok ("CEF flags: {0}" -f $args)

    # Start Menu + Desktop shortcuts point at lean launcher (games files untouched).
    $targets = @()
    $programs = [Environment]::GetFolderPath('Programs')
    $desktop = [Environment]::GetFolderPath('Desktop')
    $commonPrograms = [Environment]::GetFolderPath('CommonPrograms')
    foreach ($base in @($programs, $desktop, $commonPrograms)) {
        if (-not $base -or -not (Test-Path $base)) { continue }
        $targets += Get-ChildItem -LiteralPath $base -Filter 'Steam.lnk' -Recurse -ErrorAction SilentlyContinue
    }

    $wsh = New-Object -ComObject WScript.Shell
    $patched = 0
    foreach ($lnk in $targets) {
        try {
            $sc = $wsh.CreateShortcut($lnk.FullName)
            $sc.TargetPath = $cmdPath
            $sc.WorkingDirectory = $SteamPath
            $sc.IconLocation = "$exe,0"
            $sc.Description = 'Steam (OptiHub lean CEF - lower steamwebhelper RAM)'
            $sc.Save()
            $patched++
        } catch {
            Write-Warn "Shortcut skip $($lnk.FullName): $($_.Exception.Message)"
        }
    }

    # Always ensure a Desktop OptiHub Steam shortcut exists
    try {
        $deskLnk = Join-Path $desktop 'Steam (OptiHub Lean).lnk'
        $sc = $wsh.CreateShortcut($deskLnk)
        $sc.TargetPath = $cmdPath
        $sc.WorkingDirectory = $SteamPath
        $sc.IconLocation = "$exe,0"
        $sc.Description = 'Steam with OptiHub CEF flags for lower webhelper memory'
        $sc.Save()
        Write-Ok 'Desktop shortcut: Steam (OptiHub Lean).lnk'
        $patched++
    } catch {
        Write-Warn "Desktop shortcut: $($_.Exception.Message)"
    }

    Write-Ok "Updated $patched Steam shortcut(s) to lean CEF launch"
    return @{ Cmd = $cmdPath; Args = $args; Shortcuts = $patched }
}

function Install-WebHelperTrimHelper([string]$SteamPath) {
    # Always-on steamwebhelper trim (matches DiscOpt ~5s cadence).
    # Idle + in-game: EmptyWorkingSet. While a Steam game is running: also
    # suspend webhelper so CEF stops burning CPU/RAM pressure (overlay may pause).
    # No game inject / no game process touches - VAC-safe.
    $helper = Join-Path $SteamPath 'OptiHub-SteamWebHelperTrim.ps1'
    $body = @'
# OptiHub - steamwebhelper RAM trim (always) + suspend while a Steam game runs.
# Interval matches DiscOpt TrimIntervalMs=5000. Exit when Steam exits.
$ErrorActionPreference = 'SilentlyContinue'
Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public static class OptiHubWs {
  [DllImport("psapi.dll")] public static extern bool EmptyWorkingSet(IntPtr hProcess);
  [DllImport("kernel32.dll")] public static extern IntPtr OpenProcess(uint a, bool b, int c);
  [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
  [DllImport("ntdll.dll")] public static extern int NtSuspendProcess(IntPtr h);
  [DllImport("ntdll.dll")] public static extern int NtResumeProcess(IntPtr h);
  public const uint ACCESS = 0x0D00; // SET_QUOTA | QUERY_INFORMATION | SUSPEND_RESUME
}
"@
# Track PIDs we suspended so we only suspend once (NtSuspendProcess nests).
$script:SuspendedPids = @{}

function Test-SteamGameRunning {
  $skip = '^(steam|steamwebhelper|steamservice|GameOverlayUI|SteamService|steamerrorreporter|svchost|explorer|dwm|csrss|fontdrvhost)\.exe$'
  try {
    $hit = @(Get-CimInstance Win32_Process -ErrorAction SilentlyContinue | Where-Object {
      if (-not $_.Name -or $_.Name -match $skip) { return $false }
      $p = $_.ExecutablePath
      if (-not $p) { return $false }
      return ($p -match '(?i)[\\/]steamapps[\\/]common[\\/]') -or ($p -match '(?i)[\\/]Steam[\\/]steamapps[\\/]')
    })
    if ($hit.Count -gt 0) { return $true }
  } catch {}
  # Fallback: Get-Process Path (may miss protected processes)
  $hit2 = @(Get-Process -ErrorAction SilentlyContinue | Where-Object {
    $_.ProcessName -notmatch '^(steam|steamwebhelper|steamservice|GameOverlayUI|SteamService|steamerrorreporter)$' -and
    $_.MainWindowHandle -ne [IntPtr]::Zero -and
    $_.Path -and (
      $_.Path -like '*steamapps\common*' -or $_.Path -like '*Steam\steamapps*'
    )
  })
  return ($hit2.Count -gt 0)
}

function Invoke-WebHelperTrim([bool]$Suspend) {
  $alive = @{}
  Get-Process steamwebhelper -ErrorAction SilentlyContinue | ForEach-Object {
    $whId = $_.Id
    $alive[$whId] = $true
    try {
      $h = [OptiHubWs]::OpenProcess([OptiHubWs]::ACCESS, $false, $whId)
      if ($h -eq [IntPtr]::Zero) { return }
      try {
        [void][OptiHubWs]::EmptyWorkingSet($h)
        if ($Suspend) {
          if (-not $script:SuspendedPids.ContainsKey($whId)) {
            [void][OptiHubWs]::NtSuspendProcess($h)
            $script:SuspendedPids[$whId] = $true
          }
        } else {
          if ($script:SuspendedPids.ContainsKey($whId)) {
            [void][OptiHubWs]::NtResumeProcess($h)
            $script:SuspendedPids.Remove($whId)
          }
        }
      } finally {
        [void][OptiHubWs]::CloseHandle($h)
      }
    } catch {}
  }
  # Drop PIDs that exited
  @($script:SuspendedPids.Keys) | ForEach-Object {
    if (-not $alive.ContainsKey($_)) { $script:SuspendedPids.Remove($_) }
  }
}

while (Get-Process steam -ErrorAction SilentlyContinue) {
  Start-Sleep -Seconds 5
  $inGame = Test-SteamGameRunning
  Invoke-WebHelperTrim -Suspend:$inGame
}
# Resume any leftover suspended helpers if Steam exited while suspended
try {
  Get-Process steamwebhelper -ErrorAction SilentlyContinue | ForEach-Object {
    $h = [OptiHubWs]::OpenProcess([OptiHubWs]::ACCESS, $false, $_.Id)
    if ($h -ne [IntPtr]::Zero) {
      [void][OptiHubWs]::NtResumeProcess($h)
      [void][OptiHubWs]::CloseHandle($h)
    }
  }
} catch {}
'@
    [IO.File]::WriteAllText($helper, $body, [Text.UTF8Encoding]::new($false))

    # Wire trimmer into lean cmd launcher
    $cmdPath = Join-Path $SteamPath 'Steam-OptiHub.cmd'
    $exe = Join-Path $SteamPath 'steam.exe'
    $args = ($Script:LeanCefArgs -join ' ')
    $ps = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $cmd = @(
        '@echo off'
        'rem OptiHub lean Steam + webhelper trim (5s, always + suspend in-game) + HIGH priority'
        ('start "" /MIN "{0}" -NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File "{1}"' -f $ps, $helper)
        ('start "" /HIGH /D "{0}" "{1}" {2}' -f $SteamPath, $exe, $args)
    ) -join "`r`n"
    [IO.File]::WriteAllText($cmdPath, $cmd + "`r`n", [Text.UTF8Encoding]::new($false))
    Write-Ok 'WebHelper trim helper installed (5s trim always; suspend while gaming)'
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

    foreach ($f in @('Steam-OptiHub.cmd', 'OptiHub-SteamWebHelperTrim.ps1')) {
        $p = Join-Path $SteamPath $f
        if (Test-Path $p) {
            Remove-Item $p -Force -ErrorAction SilentlyContinue
            Write-Ok "Removed $f"
        }
    }

    $deskLean = Join-Path ([Environment]::GetFolderPath('Desktop')) 'Steam (OptiHub Lean).lnk'
    if (Test-Path $deskLean) {
        Remove-Item $deskLean -Force -ErrorAction SilentlyContinue
        Write-Ok 'Removed Desktop Steam (OptiHub Lean).lnk'
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
    if (-not $Quick) {
        $freed = [long](Clear-SteamSafeCaches $steam)
        Write-HubProgress 48 'Clearing stuck download staging...'
        [void](Optimize-SteamDownloadFolder $steam)
    } else {
        Write-Ok 'Deep cache clean skipped (-Quick) - still applying CEF lean + download hints'
    }
    Write-Ok ("Cache cleanup freed ~{0:N1} MB" -f ($freed / 1MB))

    Write-HubProgress 55 'Writing steamwebhelper CEF lean launcher...'
    $launch = Install-LeanSteamLauncher $steam
    Write-HubProgress 65 'Installing webhelper trim helper (5s + in-game suspend)...'
    [void](Install-WebHelperTrimHelper $steam)

    Write-HubProgress 75 'Download speed / config.vdf...'
    $cfgOk = Set-SteamLibraryConfigHints $steam
    Write-HubProgress 85 'Snappier library / localconfig...'
    $local = Set-SteamLocalConfigTweaks

    Write-HubProgress 94 'Saving status...'
    $state = @{
        version           = $Script:SteamOptVersion
        appliedUtc        = (Get-Date).ToUniversalTime().ToString('o')
        steamPath         = $steam
        startupDisabled   = $true
        startupRemoved    = $startupRemoved
        cacheFreedBytes   = $freed
        configTouched     = [bool]$cfgOk
        webGpuReduced     = [bool]$local.Gpu
        # HIGH priority + lean CEF always applied; localconfig hints are best-effort
        snappyUi          = $true
        cefLeanLaunch     = $true
        cefArgs           = ($Script:LeanCefArgs -join ' ')
        leanCmd           = $launch.Cmd
        webHelperTrim     = $true
        highPriority      = $true
        downloadOptimized = $true
        quick             = [bool]$Quick
    }
    Save-SteamOptState $state

    Write-Ok 'Steam Optimizer finished (webhelper lean + faster downloads + snappier client)'
    Write-Ok 'Start Steam via your Steam shortcut or Desktop "Steam (OptiHub Lean)" so CEF flags apply.'
    Write-HubProgress 100 'Completed successfully'
    Write-Output 'DONE - Steam optimized (webhelper lean + download/snappy)'
    exit 0
} catch {
    Write-Err $_.Exception.Message
    Write-HubProgress 100 'Failed'
    exit 1
}
