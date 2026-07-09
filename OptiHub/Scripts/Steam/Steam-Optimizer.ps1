# Steam Optimizer - universal multi-PC tweaks for a normal Steam install.
# Safe: no game file injection, no anti-cheat touch. Closes Steam, applies
# settings/cache/startup cleanup, writes OptiHub marker for status detect.
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
$Script:SteamOptVersion = '1.0.0'
$Root = Split-Path -Parent $MyInvocation.MyCommand.Path

function Write-HubProgress([int]$Percent, [string]$Status) {
    $p = [Math]::Max(0, [Math]::Min(100, $Percent))
    $line = "OPTIHUB_PROGRESS:$p|$Status"
    Write-Output $line
    if ($env:OPTIHUB_LOG) {
        try { Add-Content -LiteralPath $env:OPTIHUB_LOG -Value $line -Encoding UTF8 -ErrorAction SilentlyContinue } catch { }
    }
}
function Write-Step([string]$Msg) { Write-Output "[*] $Msg" }
function Write-Ok([string]$Msg)   { Write-Output "[+] $Msg" }
function Write-Warn([string]$Msg) { Write-Output "[!] $Msg" }
function Write-Err([string]$Msg)  { Write-Output "[-] $Msg" }

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
        if (Test-Path -LiteralPath $exe) { return (Resolve-Path -LiteralPath $norm).Path }
    }
    return $null
}

function Stop-Steam {
    Write-Step 'Closing Steam...'
    $names = @('steam', 'steamwebhelper', 'steamservice', 'GameOverlayUI', 'steamerrorreporter')
    for ($i = 1; $i -le 5; $i++) {
        $procs = @(Get-Process -Name $names -ErrorAction SilentlyContinue)
        if ($procs.Count -eq 0) { break }
        foreach ($p in $procs) {
            try { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue } catch { }
        }
        Start-Sleep -Milliseconds (300 * $i)
    }
    try { & taskkill.exe /F /IM steam.exe /T 2>$null | Out-Null } catch { }
    try { & taskkill.exe /F /IM steamwebhelper.exe /T 2>$null | Out-Null } catch { }
    Start-Sleep -Milliseconds 500
    Write-Ok 'Steam closed'
}

function Get-OptiHubSteamStatePath {
    $dir = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'OptiHub'
    if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    return (Join-Path $dir 'steam-optimizer.json')
}

function Save-SteamOptState([hashtable]$State) {
    $path = Get-OptiHubSteamStatePath
    $json = $State | ConvertTo-Json -Depth 6
    [IO.File]::WriteAllText($path, $json, [Text.UTF8Encoding]::new($false))
}

function Read-SteamOptState {
    $path = Get-OptiHubSteamStatePath
    if (-not (Test-Path $path)) { return $null }
    try {
        return Get-Content -LiteralPath $path -Raw -Encoding UTF8 | ConvertFrom-Json
    } catch { return $null }
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
                if ($name -in @('PSPath', 'PSParentPath', 'PSChildName', 'PSDrive', 'PSProvider')) { continue }
                $val = [string]$props.$name
                if ($val -match '(?i)steam\.exe' -or $name -match '(?i)^steam') {
                    Remove-ItemProperty -Path $key -Name $name -Force -ErrorAction SilentlyContinue
                    $removed++
                    Write-Ok "Removed startup entry: $name"
                }
            }
        } catch { }
    }

    # Steam's own "Run Steam when computer starts"
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
        (Join-Path $SteamPath 'appcache\librarycache\*.jpg'),
        (Join-Path $SteamPath 'steamapps\temp'),
        (Join-Path $SteamPath 'steamapps\downloading'),
        (Join-Path $SteamPath 'bin\cef\cef.win7x64\debug.log'),
        (Join-Path $SteamPath 'bin\cef\cef.win7\debug.log')
    )

    # Per-user CEF / GPU caches under LocalAppData (Steam HTML)
    $localSteam = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Steam'
    if (Test-Path $localSteam) {
        $targets += @(
            (Join-Path $localSteam 'htmlcache'),
            (Join-Path $localSteam 'GPUCache'),
            (Join-Path $localSteam 'Code Cache'),
            (Join-Path $localSteam 'ShaderCache')
        )
    }

    $freed = [long]0
    foreach ($t in $targets) {
        $items = @()
        if ($t -match '[\*\?]') {
            $parent = Split-Path $t -Parent
            $leaf = Split-Path $t -Leaf
            if (Test-Path $parent) {
                $items = @(Get-ChildItem -LiteralPath $parent -Filter $leaf -Force -ErrorAction SilentlyContinue)
            }
        } elseif (Test-Path -LiteralPath $t) {
            $items = @(Get-Item -LiteralPath $t -Force -ErrorAction SilentlyContinue)
        }
        foreach ($item in $items) {
            try {
                if ($item.PSIsContainer) {
                    $size = (Get-ChildItem -LiteralPath $item.FullName -Recurse -Force -ErrorAction SilentlyContinue |
                        Measure-Object -Property Length -Sum).Sum
                    if ($null -eq $size) { $size = 0 }
                    Remove-Item -LiteralPath $item.FullName -Recurse -Force -ErrorAction SilentlyContinue
                    $freed += [long]$size
                    Write-Ok ("Cleared {0} (~{1:N1} MB)" -f $item.Name, ($size / 1MB))
                } else {
                    $freed += [long]$item.Length
                    Remove-Item -LiteralPath $item.FullName -Force -ErrorAction SilentlyContinue
                    Write-Ok ("Removed {0}" -f $item.Name)
                }
            } catch {
                Write-Warn ("Skip {0}: {1}" -f $item.FullName, $_.Exception.Message)
            }
        }
    }

    # Old *.log next to steam.exe
    Get-ChildItem -LiteralPath $SteamPath -Filter '*.log' -File -ErrorAction SilentlyContinue | ForEach-Object {
        try {
            $freed += $_.Length
            Remove-Item -LiteralPath $_.FullName -Force -ErrorAction SilentlyContinue
        } catch { }
    }

    return $freed
}

function Set-SteamLibraryConfigHints([string]$SteamPath) {
    # libraryfolders.vdf left alone (library paths). Soft-touch config.vdf when present.
    $config = Join-Path $SteamPath 'config\config.vdf'
    if (-not (Test-Path -LiteralPath $config)) {
        Write-Warn 'config.vdf not found - skip library config hints'
        return $false
    }

    try {
        attrib -R $config 2>$null
        $raw = [IO.File]::ReadAllText($config)
        $orig = $raw

        # Prefer high download rate when a rate cap line exists (do not invent complex VDF).
        # "DownloadThrottleKbps" "0" means unlimited in many Steam builds.
        if ($raw -match '"DownloadThrottleKbps"\s+"\d+"') {
            $raw = [regex]::Replace($raw, '"DownloadThrottleKbps"\s+"\d+"', '"DownloadThrottleKbps"		"0"')
        }

        if ($raw -ne $orig) {
            $bak = $config + '.optihub-bak'
            if (-not (Test-Path $bak)) { Copy-Item $config $bak -Force }
            [IO.File]::WriteAllText($config, $raw, [Text.UTF8Encoding]::new($false))
            Write-Ok 'config.vdf: download throttle set to unlimited (0)'
            return $true
        }
        Write-Ok 'config.vdf: no throttle key to change (left as-is)'
        return $true
    } catch {
        Write-Warn "config.vdf edit skipped: $($_.Exception.Message)"
        return $false
    }
}

function Set-SteamLocalConfigTweaks {
    # Newest userdata\*\config\localconfig.vdf - disable heavy web GPU if keys exist
    $steamPath = Get-SteamInstallPath
    if (-not $steamPath) { return @{ Overlay = $false; Gpu = $false } }

    $userdata = Join-Path $steamPath 'userdata'
    if (-not (Test-Path $userdata)) { return @{ Overlay = $false; Gpu = $false } }

    $files = @(Get-ChildItem -LiteralPath $userdata -Directory -ErrorAction SilentlyContinue |
        ForEach-Object {
            $p = Join-Path $_.FullName 'config\localconfig.vdf'
            if (Test-Path $p) { Get-Item $p }
        } | Sort-Object LastWriteTime -Descending)

    if ($files.Count -eq 0) {
        Write-Warn 'No localconfig.vdf yet - open Steam once, then Reapply for client tweaks'
        return @{ Overlay = $false; Gpu = $false }
    }

    $file = $files[0]
    $overlay = $false
    $gpu = $false
    try {
        attrib -R $file.FullName 2>$null
        $raw = [IO.File]::ReadAllText($file.FullName)
        $orig = $raw

        # Do NOT force-disable overlay by default (many users want it).
        # Reduce browser/webhelper load when Valve stores these keys:
        if ($raw -match '"H264HWAccel"\s+"\d+"') {
            $raw = [regex]::Replace($raw, '"H264HWAccel"\s+"\d+"', '"H264HWAccel"		"0"')
            $gpu = $true
        }
        if ($raw -match '"GPUAccelWebViews2"\s+"\d+"') {
            $raw = [regex]::Replace($raw, '"GPUAccelWebViews2"\s+"\d+"', '"GPUAccelWebViews2"		"0"')
            $gpu = $true
        }
        if ($raw -match '"GPUAccelWebViews"\s+"\d+"') {
            $raw = [regex]::Replace($raw, '"GPUAccelWebViews"\s+"\d+"', '"GPUAccelWebViews"		"0"')
            $gpu = $true
        }

        # Quiet noisy notifications when keys exist
        if ($raw -match '"NotifyAvailableGames"\s+"\d+"') {
            $raw = [regex]::Replace($raw, '"NotifyAvailableGames"\s+"\d+"', '"NotifyAvailableGames"		"0"')
        }

        if ($raw -ne $orig) {
            $bak = $file.FullName + '.optihub-bak'
            if (-not (Test-Path $bak)) { Copy-Item $file.FullName $bak -Force }
            [IO.File]::WriteAllText($file.FullName, $raw, [Text.UTF8Encoding]::new($false))
            Write-Ok ("Updated localconfig.vdf ({0})" -f $file.Directory.Parent.Name)
        } else {
            Write-Ok 'localconfig.vdf present - no matching keys to patch (still OK)'
        }
    } catch {
        Write-Warn "localconfig.vdf: $($_.Exception.Message)"
    }

    return @{ Overlay = $overlay; Gpu = $gpu }
}

function Disable-SteamGameRecordingHints([string]$SteamPath) {
    # Best-effort: Game Recording can be heavy; only flip known registry toggles.
    $ok = $false
    try {
        $key = 'HKCU:\Software\Valve\Steam'
        if (Test-Path $key) {
            # Non-destructive: document only if key missing
            $props = Get-ItemProperty -Path $key -ErrorAction SilentlyContinue
            if ($props -and ($props.PSObject.Properties.Name -contains 'EnableGameRecording')) {
                New-ItemProperty -Path $key -Name 'EnableGameRecording' -PropertyType DWord -Value 0 -Force | Out-Null
                Write-Ok 'Game Recording disabled (registry)'
                $ok = $true
            }
        }
    } catch { }
    if (-not $ok) { Write-Ok 'Game Recording: no registry toggle found (left as-is)' }
    return $ok
}

function Invoke-SteamRepair([string]$SteamPath) {
    Write-Step 'Repair: restoring OptiHub Steam backups where present...'
    $restored = 0
    $paths = @(
        (Join-Path $SteamPath 'config\config.vdf.optihub-bak'),
        (Join-Path $SteamPath 'config\config.vdf')
    )
    $bak = Join-Path $SteamPath 'config\config.vdf.optihub-bak'
    $cfg = Join-Path $SteamPath 'config\config.vdf'
    if ((Test-Path $bak) -and (Test-Path $cfg)) {
        Copy-Item $bak $cfg -Force
        $restored++
        Write-Ok 'Restored config.vdf from backup'
    }

    Get-ChildItem -LiteralPath (Join-Path $SteamPath 'userdata') -Directory -ErrorAction SilentlyContinue | ForEach-Object {
        $lb = Join-Path $_.FullName 'config\localconfig.vdf.optihub-bak'
        $lf = Join-Path $_.FullName 'config\localconfig.vdf'
        if ((Test-Path $lb) -and (Test-Path $lf)) {
            Copy-Item $lb $lf -Force
            $restored++
            Write-Ok "Restored localconfig for user $($_.Name)"
        }
    }

    $statePath = Get-OptiHubSteamStatePath
    if (Test-Path $statePath) {
        Remove-Item $statePath -Force -ErrorAction SilentlyContinue
        Write-Ok 'Cleared OptiHub Steam optimizer marker'
    }

    Write-Ok "Repair finished ($restored file(s) restored). Start Steam normally."
    return $restored
}

# --- main ---
try {
    Write-HubProgress 5 'Starting Steam Optimizer...'
    $steam = Get-SteamInstallPath
    if (-not $steam) {
        throw 'Steam not found. Install Steam (steam.exe), open it once, then rerun OptiHub.'
    }
    Write-Ok "Steam: $steam"
    Write-HubProgress 12 "Steam: $steam"

    Stop-Steam
    Write-HubProgress 25 'Steam closed'

    if ($Repair) {
        Write-HubProgress 40 'Repairing...'
        [void](Invoke-SteamRepair $steam)
        Write-HubProgress 100 'Repair complete'
        exit 0
    }

    Write-HubProgress 35 'Disabling Windows startup...'
    $startupRemoved = Disable-SteamWindowsStartup

    Write-HubProgress 50 'Cleaning safe caches...'
    $freed = 0L
    if (-not $Quick) {
        $freed = Clear-SteamSafeCaches $steam
    } else {
        Write-Ok 'Cache clean skipped (-Quick)'
    }
    Write-Ok ("Cache cleanup freed ~{0:N1} MB" -f ($freed / 1MB))

    Write-HubProgress 70 'Applying Steam config hints...'
    $cfgOk = Set-SteamLibraryConfigHints $steam
    $local = Set-SteamLocalConfigTweaks
    [void](Disable-SteamGameRecordingHints $steam)

    Write-HubProgress 90 'Saving status...'
    $state = @{
        version          = $Script:SteamOptVersion
        appliedUtc       = (Get-Date).ToUniversalTime().ToString('o')
        steamPath        = $steam
        startupDisabled  = $true
        startupRemoved   = $startupRemoved
        cacheFreedBytes  = $freed
        configTouched    = [bool]$cfgOk
        webGpuReduced    = [bool]$local.Gpu
        quick            = [bool]$Quick
    }
    Save-SteamOptState $state

    Write-Ok 'Steam Optimizer finished'
    Write-HubProgress 100 'Completed successfully'
    Write-Output 'DONE - Steam optimized'
    exit 0
} catch {
    Write-Err $_.Exception.Message
    Write-HubProgress 100 'Failed'
    exit 1
}
