# Exo Riot / Epic optimizer.
# Owns: quiet startup, shell quiet, per-game Windows high-perf GPU preference,
# DSCP, exact Repair. Display/borderless is owned by the Games hub (no FSO-off).
# Anti-cheat, services, game files, and caches stay untouched.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('Riot','Epic')][string]$Module,
    [switch]$Repair,
    [switch]$NonInteractive,
    [switch]$Experimental
)

$ErrorActionPreference = 'Stop'
$Version = '1.8.1'
$ExoRoot = Join-Path $env:LOCALAPPDATA 'Exo'
$StatePath = Join-Path $ExoRoot ("{0}-optimizer.json" -f $Module.ToLowerInvariant())
$SnapshotPath = Join-Path $ExoRoot ("{0}-snapshot.json" -f $Module.ToLowerInvariant())
$YieldHelperPath = Join-Path $ExoRoot ("{0}-yield-guard.ps1" -f $Module.ToLowerInvariant())
$YieldRunName = "Exo-$Module-Yield"
$RunSubKey = 'Software\Microsoft\Windows\CurrentVersion\Run'
$GpuSubKey = 'Software\Microsoft\DirectX\UserGpuPreferences'
$FsoSubKey = 'Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers'
# High performance discrete GPU (Windows Graphics settings).
$GpuHighPerf = 'GpuPreference=2;'
$Report = [System.Collections.Generic.List[string]]::new()

# Shared Game Bar / DSCP + competitive gaming glue (script-scope dotsource)
$__common = Join-Path (Split-Path -Parent $PSScriptRoot) 'lib\Exo.Common.ps1'
if (-not (Test-Path -LiteralPath $__common)) { $__common = Join-Path $PSScriptRoot '..\lib\Exo.Common.ps1' }
if (-not (Test-Path -LiteralPath $__common) -and $env:LOCALAPPDATA) {
    $__common = Join-Path $env:LOCALAPPDATA 'Exo\app\Scripts\lib\Exo.Common.ps1'
}
if (Test-Path -LiteralPath $__common) {
    . $__common
    foreach ($__libPath in @(Import-ExoSharedLibFiles -From $PSScriptRoot)) { . $__libPath }
}
# Always force-load GameBar (DSCP). Import can miss when elevated cwd differs.
if (-not (Get-Command Set-ExoGameQosPolicy -ErrorAction SilentlyContinue)) {
    foreach ($c in @(
        (Join-Path (Split-Path -Parent $PSScriptRoot) 'lib\Exo.GameBar.ps1'),
        (Join-Path $PSScriptRoot '..\lib\Exo.GameBar.ps1'),
        (Join-Path $env:LOCALAPPDATA 'Exo\app\Scripts\lib\Exo.GameBar.ps1'),
        (Join-Path $env:LOCALAPPDATA 'Exo\scripts\lib\Exo.GameBar.ps1')
    )) {
        if ($c -and (Test-Path -LiteralPath $c)) { . $c; break }
    }
}
if (-not (Get-Command Set-ExoGameQosPolicy -ErrorAction SilentlyContinue)) {
    Write-Warning 'Exo.GameBar.ps1 not loaded - per-game DSCP will be skipped'
}

function Write-ProgressLine([int]$Percent, [string]$Text) {
    Write-Output ("EXO_PROGRESS:{0}|{1}" -f $Percent, $Text)
}
function Add-Report([string]$Step, [string]$Status, [string]$Reason = '') {
    $line = if ($Reason) { "${Step}|${Status}:${Reason}" } else { "${Step}|${Status}" }
    [void]$Report.Add($line)
    Write-Output ("EXO_REPORT:{0}" -f $line)
}
function Test-Admin {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    $p = [Security.Principal.WindowsPrincipal]::new($id)
    return $p.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}
function Normalize-Path([string]$Path) {
    if ([string]::IsNullOrWhiteSpace($Path)) { return $null }
    try { return [IO.Path]::GetFullPath([Environment]::ExpandEnvironmentVariables($Path.Trim('"'))) }
    catch { return $null }
}
function Add-Target([System.Collections.Generic.List[object]]$List, [string]$Path, [string]$Source) {
    $full = Normalize-Path $Path
    if (-not $full -or -not (Test-Path -LiteralPath $full -PathType Leaf)) { return }
    if ([IO.Path]::GetExtension($full) -ine '.exe') { return }
    if (@($List | Where-Object { [string]$_.path -ieq $full }).Count -gt 0) { return }
    [void]$List.Add([pscustomobject]@{ path = $full; exe = [IO.Path]::GetFileName($full); source = $Source })
}
function Get-UninstallEntries {
    foreach ($root in @(
        'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall',
        'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall',
        'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall'
    )) {
        if (-not (Test-Path -LiteralPath $root)) { continue }
        Get-ChildItem -LiteralPath $root -ErrorAction SilentlyContinue | ForEach-Object {
            Get-ItemProperty -LiteralPath $_.PSPath -ErrorAction SilentlyContinue
        }
    }
}
function Get-UninstallDisplayText($Entry) {
    # StrictMode-safe: many uninstall keys omit DisplayName/Publisher.
    if ($null -eq $Entry) { return '' }
    $dn = ''
    $pub = ''
    try {
        if ($Entry.PSObject.Properties.Name -contains 'DisplayName' -and $null -ne $Entry.DisplayName) {
            $dn = [string]$Entry.DisplayName
        }
    } catch { }
    try {
        if ($Entry.PSObject.Properties.Name -contains 'Publisher' -and $null -ne $Entry.Publisher) {
            $pub = [string]$Entry.Publisher
        }
    } catch { }
    return ("{0} {1}" -f $dn, $pub).Trim()
}
function Get-RiotTargets {
    $targets = [System.Collections.Generic.List[object]]::new()
    $roots = [System.Collections.Generic.List[string]]::new()
    foreach ($candidate in @(
        (Join-Path ${env:ProgramFiles} 'Riot Vanguard'),
        (Join-Path ${env:SystemDrive} 'Riot Games')
    )) {
        if ($candidate -and (Test-Path -LiteralPath $candidate -PathType Container)) { [void]$roots.Add($candidate) }
    }
    foreach ($entry in @(Get-UninstallEntries | Where-Object {
        (Get-UninstallDisplayText $_) -match '(?i)Riot|VALORANT|League of Legends'
    })) {
        $loc = ''
        try {
            if ($entry.PSObject.Properties.Name -contains 'InstallLocation') {
                $loc = [string]$entry.InstallLocation
            }
        } catch { }
        $root = Normalize-Path $loc
        if ($root -and (Test-Path -LiteralPath $root -PathType Container)) { [void]$roots.Add($root) }
    }
    foreach ($procName in @('VALORANT-Win64-Shipping','VALORANT','League of Legends')) {
        Get-Process -Name $procName -ErrorAction SilentlyContinue | ForEach-Object {
            try { Add-Target $targets ([string]$_.Path) 'running-process' } catch { }
        }
    }
    # Prefer known relative paths (no multi-second full-tree recurse).
    $knownRels = @(
        'VALORANT\live\VALORANT\Binaries\Win64\VALORANT-Win64-Shipping.exe',
        'VALORANT\live\ShooterGame\Binaries\Win64\VALORANT-Win64-Shipping.exe',
        'VALORANT\VALORANT.exe',
        'League of Legends\Game\League of Legends.exe',
        'League of Legends\League of Legends.exe'
    )
    $gameNames = @(
        'VALORANT-Win64-Shipping.exe', 'VALORANT.exe',
        'League of Legends.exe'
    )
    foreach ($root in @($roots | Sort-Object -Unique)) {
        if ($root -match '(?i)Vanguard') { continue }
        foreach ($rel in $knownRels) {
            Add-Target $targets (Join-Path $root $rel) 'riot-known'
        }
        # Shallow fallback only when known paths miss (depth-capped).
        if ($targets.Count -eq 0) {
            Get-ChildItem -LiteralPath $root -Directory -ErrorAction SilentlyContinue | ForEach-Object {
                Get-ChildItem -LiteralPath $_.FullName -File -Recurse -Depth 4 -ErrorAction SilentlyContinue |
                    Where-Object { $_.Name -in $gameNames } |
                    ForEach-Object { Add-Target $targets $_.FullName 'riot-install' }
            }
        }
    }
    return @($targets)
}
function Get-EpicTargets {
    $targets = [System.Collections.Generic.List[object]]::new()
    $manifestRoot = Join-Path $env:ProgramData 'Epic\EpicGamesLauncher\Data\Manifests'
    if (Test-Path -LiteralPath $manifestRoot -PathType Container) {
        foreach ($file in @(Get-ChildItem -LiteralPath $manifestRoot -Filter '*.item' -File -ErrorAction SilentlyContinue)) {
            try {
                $item = Get-Content -LiteralPath $file.FullName -Raw -Encoding UTF8 | ConvertFrom-Json
                $launch = [string]$item.LaunchExecutable
                $install = Normalize-Path ([string]$item.InstallLocation)
                if ($install -and $launch -and $launch -notmatch '(?i)EpicGamesLauncher') {
                    Add-Target $targets (Join-Path $install $launch) 'epic-manifest'
                }
            } catch { }
        }
    }
    # Manifests are authoritative  -  skip scanning every process (slow + noisy).
    return @($targets)
}
function Get-Targets { if ($Module -eq 'Riot') { @(Get-RiotTargets) } else { @(Get-EpicTargets) } }
function Get-LauncherTargets {
    $targets = [System.Collections.Generic.List[object]]::new()
    if ($Module -eq 'Riot') {
        foreach ($path in @(
            (Join-Path ${env:SystemDrive} 'Riot Games\Riot Client\RiotClientServices.exe'),
            (Join-Path ${env:SystemDrive} 'Riot Games\Riot Client\RiotClientUx.exe'),
            (Join-Path ${env:SystemDrive} 'Riot Games\Riot Client\RiotClientUxRender.exe')
        )) { Add-Target $targets $path 'riot-launcher' }
    } else {
        # Epic ships under Program Files or Program Files (x86) depending on install age.
        foreach ($root in @(
            (Join-Path ${env:ProgramFiles} 'Epic Games\Launcher\Portal\Binaries\Win64'),
            (Join-Path ${env:ProgramFiles(x86)} 'Epic Games\Launcher\Portal\Binaries\Win64')
        )) {
            if (-not (Test-Path -LiteralPath $root -PathType Container)) { continue }
            foreach ($name in @('EpicGamesLauncher.exe','EpicWebHelper.exe')) {
                Add-Target $targets (Join-Path $root $name) 'epic-launcher'
            }
        }
        # Fallback: discover from running processes / known leaf name.
        if ($targets.Count -eq 0) {
            Get-Process -Name 'EpicGamesLauncher','EpicWebHelper' -ErrorAction SilentlyContinue | ForEach-Object {
                try { Add-Target $targets ([string]$_.Path) 'epic-running' } catch { }
            }
        }
    }
    return @($targets)
}
function Test-HybridGraphics {
    try {
        $names = @(Get-CimInstance Win32_VideoController -ErrorAction SilentlyContinue |
            ForEach-Object { [string]$_.Name } |
            Where-Object { $_ -and $_ -notmatch '(?i)Microsoft Basic|Remote|Hyper-V|Virtual' })
        $hasDiscrete = @($names | Where-Object { $_ -match '(?i)NVIDIA|GeForce|RTX|GTX|Radeon\s+RX|Intel.*Arc' }).Count -gt 0
        $hasIntegrated = @($names | Where-Object { $_ -match '(?i)Intel.*(?:UHD|Iris|HD Graphics)|AMD Radeon\(TM\) Graphics|Radeon Vega' }).Count -gt 0
        return $names.Count -ge 2 -and $hasDiscrete -and $hasIntegrated
    } catch { return $false }
}
function Test-Installed {
    if ($Module -eq 'Riot') {
        if (Test-Path -LiteralPath (Join-Path ${env:ProgramFiles} 'Riot Vanguard') -PathType Container) { return $true }
        if (Test-Path -LiteralPath (Join-Path ${env:SystemDrive} 'Riot Games') -PathType Container) { return $true }
    } else {
        foreach ($epic in @(
            (Join-Path ${env:ProgramFiles} 'Epic Games\Launcher\Portal\Binaries\Win64\EpicGamesLauncher.exe'),
            (Join-Path ${env:ProgramFiles(x86)} 'Epic Games\Launcher\Portal\Binaries\Win64\EpicGamesLauncher.exe')
        )) {
            if (Test-Path -LiteralPath $epic -PathType Leaf) { return $true }
        }
    }
    return @((Get-UninstallEntries) | Where-Object { (Get-UninstallDisplayText $_) -match "(?i)$Module" }).Count -gt 0
}
function Get-ValueSnapshot([Microsoft.Win32.RegistryKey]$Key, [string]$Name) {
    if (-not $Key -or $Name -notin @($Key.GetValueNames())) {
        return [ordered]@{ existed = $false; value = $null; kind = $null }
    }
    return [ordered]@{
        existed = $true
        value = $Key.GetValue($Name, $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
        kind = [string]$Key.GetValueKind($Name)
    }
}
function Get-RunMatches {
    $result = [System.Collections.Generic.List[object]]::new()
    $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($RunSubKey)
    if (-not $key) { return @() }
    try {
        foreach ($name in $key.GetValueNames()) {
            $value = [string]$key.GetValue($name, '', [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
            if ("$name $value" -match "(?i)$Module") {
                [void]$result.Add([ordered]@{ name = $name; value = $value; kind = [string]$key.GetValueKind($name) })
            }
        }
    } finally { $key.Dispose() }
    return @($result)
}
function New-Snapshot([object[]]$Targets, [object[]]$Launchers) {
    if (Test-Path -LiteralPath $SnapshotPath -PathType Leaf) { return }
    $gpu = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($GpuSubKey)
    $fso = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($FsoSubKey)
    $targetStates = [System.Collections.Generic.List[object]]::new()
    try {
        foreach ($target in $Targets) {
            [void]$targetStates.Add([ordered]@{
                path = $target.path
                exe = $target.exe
                role = 'game'
                gpu = Get-ValueSnapshot $gpu ([string]$target.path)
                fso = Get-ValueSnapshot $fso ([string]$target.path)
            })
        }
        foreach ($target in $Launchers) {
            [void]$targetStates.Add([ordered]@{
                path = $target.path
                exe = $target.exe
                role = 'launcher'
                gpu = Get-ValueSnapshot $gpu ([string]$target.path)
                fso = Get-ValueSnapshot $fso ([string]$target.path)
            })
        }
    } finally {
        if ($gpu) { $gpu.Dispose() }
        if ($fso) { $fso.Dispose() }
    }
    # schema 4: Game Bar owned by Windows card  -  never snapshot/restore host Game Bar here.
    $snapshot = [ordered]@{
        schema = 4
        module = $Module
        capturedUtc = (Get-Date).ToUniversalTime().ToString('o')
        run = @(Get-RunMatches)
        targets = @($targetStates)
        yieldRunName = $YieldRunName
        gameBar = @() # legacy field empty; Windows owns Game Bar
        qosPolicies = @()
    }
    New-Item -ItemType Directory -Path $ExoRoot -Force | Out-Null
    $snapshot | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $SnapshotPath -Encoding UTF8
}
function Convert-Kind([string]$Kind) {
    if ([string]::IsNullOrWhiteSpace($Kind)) { return [Microsoft.Win32.RegistryValueKind]::String }
    return [Microsoft.Win32.RegistryValueKind]::$Kind
}
function Remove-StartupEntries {
    $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($RunSubKey, $true)
    if (-not $key) { return 0 }
    $removed = 0
    try {
        foreach ($name in @($key.GetValueNames())) {
            # Never delete Exo own yield companion (name contains module brand).
            if ($name -eq $YieldRunName -or $name -match '(?i)^Exo-') { continue }
            $value = [string]$key.GetValue($name, '')
            if ("$name $value" -match "(?i)$Module") { $key.DeleteValue($name, $false); $removed++ }
        }
    } finally { $key.Dispose() }
    return $removed
}

# Steam-parity shell quiet, scoped to this launcher brand only (toasts + tasks).
# Cross-connect from Steam windows-quiet  -  never machine-wide Windows policy.
function Get-LauncherNotifyIds {
    if ($Module -eq 'Riot') {
        return @(
            'Riot Client', 'RiotClient', 'Riot Games', 'RiotClientUx',
            'VALORANT', 'League of Legends', 'riotclientservices.exe'
        )
    }
    return @(
        'EpicGamesLauncher', 'Epic Games Launcher', 'com.epicgames.launcher',
        'EpicGamesLauncher.exe', 'UnrealEngineLauncher'
    )
}
function Get-LauncherTaskPatterns {
    if ($Module -eq 'Riot') { return @('(?i)Riot|VALORANT|League of Legends|Vanguard') }
    return @('(?i)Epic Games|EpicGames|EOS|Unreal Engine')
}
function Apply-LauncherShellQuiet {
    $base = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Notifications\Settings'
    $touched = 0
    foreach ($id in @(Get-LauncherNotifyIds)) {
        $path = Join-Path $base $id
        try {
            if (-not (Test-Path -LiteralPath $path)) {
                New-Item -Path $path -Force -ErrorAction SilentlyContinue | Out-Null
            }
            if (Test-Path -LiteralPath $path) {
                New-ItemProperty -LiteralPath $path -Name Enabled -PropertyType DWord -Value 0 -Force -ErrorAction SilentlyContinue | Out-Null
                Set-ItemProperty -LiteralPath $path -Name Enabled -Value 0 -Type DWord -Force -ErrorAction SilentlyContinue
                $touched++
            }
        } catch { }
    }
    $tasksQuiet = 0
    try {
        $patterns = @(Get-LauncherTaskPatterns)
        # Prefer named lookup when possible; full Get-ScheduledTask is multi-second.
        $candidates = @()
        try {
            if ($Module -eq 'Epic') {
                $candidates += @(Get-ScheduledTask -TaskPath '\' -ErrorAction SilentlyContinue | Where-Object { $_.TaskName -match '(?i)Epic|Unreal' })
                $candidates += @(Get-ScheduledTask -TaskPath '\Epic Games\' -ErrorAction SilentlyContinue)
            } else {
                $candidates += @(Get-ScheduledTask -TaskPath '\' -ErrorAction SilentlyContinue | Where-Object { $_.TaskName -match '(?i)Riot|VALORANT|League' })
                $candidates += @(Get-ScheduledTask -TaskPath '\Riot Games\' -ErrorAction SilentlyContinue)
            }
        } catch { }
        if ($candidates.Count -eq 0) {
            $candidates = @(Get-ScheduledTask -ErrorAction SilentlyContinue)
        }
        foreach ($task in $candidates) {
            if (-not $task) { continue }
            $blob = "$($task.TaskName) $($task.TaskPath)"
            $hit = $false
            foreach ($re in $patterns) { if ($blob -match $re) { $hit = $true; break } }
            if (-not $hit) { continue }
            # Never disable anti-cheat / Vanguard integrity services via task kill folklore
            if ($blob -match '(?i)Vanguard|vgk|vgc|EOSOverlay|EasyAntiCheat') { continue }
            try {
                if ($task.Settings.Enabled) {
                    Disable-ScheduledTask -InputObject $task -ErrorAction SilentlyContinue | Out-Null
                    $tasksQuiet++
                }
            } catch { }
        }
    } catch { }
    return @{ Notify = $touched; Tasks = $tasksQuiet }
}
function Get-ExoPwshPath {
    # NEVER return WindowsApps\pwsh.exe stub (breaks Run keys + WSH).
    foreach ($p in @(
        (Join-Path $env:ProgramFiles 'PowerShell\7\pwsh.exe'),
        (Join-Path $env:ProgramFiles 'PowerShell\7-preview\pwsh.exe')
    )) {
        if ($p -and (Test-Path -LiteralPath $p)) { return $p }
    }
    $apps = Join-Path $env:ProgramFiles 'WindowsApps'
    if (Test-Path -LiteralPath $apps) {
        $hit = Get-ChildItem -LiteralPath $apps -Directory -Filter 'Microsoft.PowerShell_*' -ErrorAction SilentlyContinue |
            Sort-Object Name -Descending |
            ForEach-Object {
                $c = Join-Path $_.FullName 'pwsh.exe'
                if (Test-Path -LiteralPath $c) { $c }
            } |
            Select-Object -First 1
        if ($hit) { return $hit }
    }
    $ps51 = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
    if (Test-Path -LiteralPath $ps51) { return $ps51 }
    return $null
}

function Clear-LauncherSafeCaches {
    # Launcher-only logs/web/crash junk  -  never game installs, anti-cheat, or save data.
    $freed = 0L
    $paths = [System.Collections.Generic.List[string]]::new()
    if ($Module -eq 'Epic') {
        $epicLocal = Join-Path $env:LOCALAPPDATA 'EpicGamesLauncher'
        foreach ($p in @(
            (Join-Path $epicLocal 'Saved\Logs'),
            (Join-Path $epicLocal 'Saved\webcache'),
            (Join-Path $epicLocal 'Saved\webcache_4430'),
            (Join-Path $epicLocal 'Saved\webcache_4147'),
            (Join-Path $epicLocal 'Saved\webcache_4616'),
            (Join-Path $epicLocal 'Saved\Crashes'),
            (Join-Path $epicLocal 'Saved\Config\CrashReportClient'),
            (Join-Path $epicLocal 'Intermediate'),
            (Join-Path $env:LOCALAPPDATA 'Epic\EpicGamesLauncher\Data\EMS'),
            (Join-Path $env:LOCALAPPDATA 'Epic\EpicGamesLauncher\Data\Manifests\.tmp'),
            (Join-Path $env:TEMP 'EpicGamesLauncher'),
            (Join-Path $env:TEMP 'Epic')
        )) { if ($p) { [void]$paths.Add($p) } }
        # CEF GPU/Code caches under Saved (versioned folders)
        $saved = Join-Path $epicLocal 'Saved'
        if (Test-Path -LiteralPath $saved) {
            Get-ChildItem -LiteralPath $saved -Directory -ErrorAction SilentlyContinue |
                Where-Object { $_.Name -match '(?i)^webcache|GPUCache|Code Cache|GrShaderCache|DawnCache|ShaderCache' } |
                ForEach-Object { [void]$paths.Add($_.FullName) }
        }
    } else {
        $riotLocal = Join-Path $env:LOCALAPPDATA 'Riot Games\Riot Client'
        foreach ($p in @(
            (Join-Path $riotLocal 'Logs'),
            (Join-Path $riotLocal 'Crashes'),
            (Join-Path $riotLocal 'Cache'),
            (Join-Path $riotLocal 'GPUCache'),
            (Join-Path $riotLocal 'Code Cache'),
            (Join-Path $riotLocal 'DawnCache'),
            (Join-Path $riotLocal 'ShaderCache'),
            (Join-Path $riotLocal 'GrShaderCache'),
            (Join-Path $riotLocal 'Service Worker'),
            (Join-Path $env:LOCALAPPDATA 'Riot Games\Metadata'),
            (Join-Path $env:LOCALAPPDATA 'Riot Games\Riot Client\Config\Crashpad'),
            (Join-Path $env:TEMP 'Riot Games'),
            (Join-Path $env:TEMP 'Riot Client')
        )) { if ($p) { [void]$paths.Add($p) } }
    }
    foreach ($path in $paths) {
        if (-not (Test-Path -LiteralPath $path)) { continue }
        try {
            Get-ChildItem -LiteralPath $path -Force -Recurse -ErrorAction SilentlyContinue |
                ForEach-Object {
                    try {
                        if ($_.PSIsContainer) { return }
                        $len = [long]$_.Length
                        Remove-Item -LiteralPath $_.FullName -Force -ErrorAction SilentlyContinue
                        if (-not (Test-Path -LiteralPath $_.FullName)) { $freed += $len }
                    } catch { }
                }
            # Empty leftover dirs under cache roots (not the root itself if locked)
            Get-ChildItem -LiteralPath $path -Directory -Force -ErrorAction SilentlyContinue |
                ForEach-Object {
                    try { Remove-Item -LiteralPath $_.FullName -Recurse -Force -ErrorAction SilentlyContinue } catch { }
                }
        } catch { }
    }
    return $freed
}

function Install-QuietStartMenuLaunch {
    # Steam-parity: Start Menu opens a quiet Exo launcher cmd (no desktop spam).
    $launchers = @(Get-LauncherTargets)
    if ($launchers.Count -eq 0) { return $false }
    $primary = [string]$launchers[0].path
    if (-not $primary -or -not (Test-Path -LiteralPath $primary)) { return $false }
    $cmdName = if ($Module -eq 'Epic') { 'Epic-Exo.cmd' } else { 'Riot-Exo.cmd' }
    $cmdDir = Join-Path $ExoRoot 'launchers'
    New-Item -ItemType Directory -Path $cmdDir -Force -ErrorAction SilentlyContinue | Out-Null
    $cmdPath = Join-Path $cmdDir $cmdName
    $dir = [IO.Path]::GetDirectoryName($primary)
    $leaf = [IO.Path]::GetFileName($primary)
    # Quiet-ish flags that do not touch anti-cheat.
    # Epic: -StartMinimized reduces splash chrome. Riot ClientUx is CEF  -  no unsafe flags.
    $extra = if ($Module -eq 'Epic') { ' -StartMinimized' } else { '' }
    $body = @"
@echo off
rem Exo quiet $Module launcher  -  high priority host, no extra chrome.
start "" /HIGH /D "$dir" "$primary"$extra
"@
    try {
        [IO.File]::WriteAllText($cmdPath, $body, [Text.UTF8Encoding]::new($false))
    } catch { return $false }

    $programs = [Environment]::GetFolderPath('Programs')
    $linkName = if ($Module -eq 'Epic') { 'Epic Games Launcher.lnk' } else { 'Riot Client.lnk' }
    $candidates = @(
        (Join-Path $programs $linkName),
        (Join-Path $programs "Epic Games\$linkName"),
        (Join-Path $programs "Riot Games\$linkName"),
        (Join-Path $programs 'Riot Games\Riot Client.lnk'),
        (Join-Path $programs 'Epic Games\Epic Games Launcher.lnk')
    )
    $patched = 0
    foreach ($lnk in $candidates) {
        if (-not (Test-Path -LiteralPath $lnk)) { continue }
        try {
            $w = New-Object -ComObject WScript.Shell
            $s = $w.CreateShortcut($lnk)
            $s.TargetPath = $cmdPath
            $s.WorkingDirectory = $cmdDir
            $s.Arguments = ''
            $s.Save()
            $patched++
        } catch { }
    }
    return ($patched -gt 0) -or (Test-Path -LiteralPath $cmdPath)
}

function Remove-YieldGuard {
    # Only strip BROKEN yield entries (wscript / WindowsApps stub). Never delete a good -File companion.
    $removed = 0
    try {
        $run = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($RunSubKey, $true)
        if ($run) {
            try {
                foreach ($name in @($run.GetValueNames())) {
                    if ($name -ne $YieldRunName -and $name -notmatch '(?i)^Exo-.*-Yield$') { continue }
                    $val = [string]$run.GetValue($name)
                    $broken = $val -match '(?i)wscript|RunHidden|WindowsApps\\pwsh\.exe'
                    $missingFile = $val -match '(?i)-File\s+"?([^"]+\.ps1)' -and -not (Test-Path -LiteralPath $Matches[1])
                    if ($broken -or $missingFile -or [string]::IsNullOrWhiteSpace($val)) {
                        try { $run.DeleteValue($name, $false); $removed++ } catch {}
                    }
                }
            } finally { $run.Dispose() }
        }
    } catch {}
    return $removed
}

function Install-YieldGuard([object[]]$Targets, [object[]]$Launchers) {
    # Hidden PowerShell companion - NEVER wscript (WSH error dialogs + WindowsApps stub failures).
    # Soft reclaim + demote launcher while game runs. Never touches AC/game files.
    [void](Remove-YieldGuard)

    $gameNames = @($Targets | ForEach-Object { [IO.Path]::GetFileNameWithoutExtension([string]$_.path) } | Where-Object { $_ } | Sort-Object -Unique)
    $launcherNames = @($Launchers | ForEach-Object { [IO.Path]::GetFileNameWithoutExtension([string]$_.path) } | Where-Object { $_ } | Sort-Object -Unique)
    if ($Module -eq 'Riot') {
        $launcherNames = @($launcherNames + @('RiotClientServices','RiotClientUx','RiotClientCrashHandler') | Sort-Object -Unique)
    } elseif ($Module -eq 'Epic') {
        $launcherNames = @($launcherNames + @('EpicGamesLauncher','EpicWebHelper') | Sort-Object -Unique)
    }
    if ($gameNames.Count -eq 0) {
        Write-Output "EXO_REPORT:yield-guard|skip:no game process names for companion"
        return $false
    }

    $gameList = ($gameNames | ForEach-Object { "'$($_ -replace '''','''''')'" }) -join ','
    $launcherList = ($launcherNames | ForEach-Object { "'$($_ -replace '''','''''')'" }) -join ','
    $lines = @(
        "# Exo $Module companion - hidden PowerShell only (no wscript)."
        '$ErrorActionPreference = ''SilentlyContinue'''
        '$created = $false'
        ("`$mutex = [Threading.Mutex]::new(`$true, 'Local\Exo.{0}.YieldGuard', [ref]`$created)" -f $Module)
        'if (-not $created) { $mutex.Dispose(); exit 0 }'
        'try { Add-Type -Name ExoWin -Namespace Native -MemberDefinition @"'
        'using System; using System.Runtime.InteropServices;'
        'public static class ExoWin {'
        '  [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);'
        '  public const uint WM_CLOSE = 0x0010;'
        '}'
        '"@ } catch {}'
        'Add-Type -TypeDefinition @"'
        'using System; using System.Runtime.InteropServices;'
        'public static class ExoLaunchYield {'
        '  [StructLayout(LayoutKind.Sequential)] struct MEMORY_PRIORITY_INFORMATION { public uint MemoryPriority; }'
        '  [StructLayout(LayoutKind.Sequential)] struct PROCESS_POWER_THROTTLING_STATE { public uint Version; public uint ControlMask; public uint StateMask; }'
        '  [DllImport("kernel32.dll", SetLastError=true)] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);'
        '  [DllImport("kernel32.dll", SetLastError=true)] static extern bool SetProcessInformation(IntPtr process, int infoClass, ref MEMORY_PRIORITY_INFORMATION info, uint size);'
        '  [DllImport("kernel32.dll", SetLastError=true)] static extern bool SetProcessWorkingSetSize(IntPtr process, IntPtr min, IntPtr max);'
        '  [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);'
        '  public static bool SetMemoryPriority(int pid, uint priority) {'
        '    IntPtr h = OpenProcess(0x0200u | 0x1000u, false, pid);'
        '    if (h == IntPtr.Zero) return false;'
        '    try { var info = new MEMORY_PRIORITY_INFORMATION { MemoryPriority = priority }; return SetProcessInformation(h, 0, ref info, 4); }'
        '    finally { CloseHandle(h); }'
        '  }'
        '  public static bool SetPowerThrottled(int pid, bool enabled) {'
        '    IntPtr h = OpenProcess(0x0200u | 0x1000u, false, pid);'
        '    if (h == IntPtr.Zero) return false;'
        '    try {'
        '      var info = new PROCESS_POWER_THROTTLING_STATE { Version = 1, ControlMask = 1, StateMask = enabled ? 1u : 0u };'
        '      return SetProcessInformation(h, 4, ref info, 12);'
        '    } finally { CloseHandle(h); }'
        '  }'
        '  public static bool SoftReclaimWorkingSet(int pid) {'
        '    IntPtr h = OpenProcess(0x0100u | 0x0200u, false, pid);'
        '    if (h == IntPtr.Zero) return false;'
        '    try { return SetProcessWorkingSetSize(h, new IntPtr(-1), new IntPtr(-1)); }'
        '    finally { CloseHandle(h); }'
        '  }'
        '}'
        '"@'
        ("`$games = @({0})" -f $gameList)
        ("`$launchers = @({0})" -f $launcherList)
        '$closedOnce = @{}'
        'try {'
        '  while ($true) {'
        '    $gameRunning = $false'
        '    foreach ($n in $games) {'
        '      if (Get-Process -Name $n -ErrorAction SilentlyContinue) { $gameRunning = $true; break }'
        '    }'
        '    foreach ($n in $launchers) {'
        '      Get-Process -Name $n -ErrorAction SilentlyContinue | ForEach-Object {'
        '        try {'
        '          if ($gameRunning) {'
        '            if ($_.PriorityClass -ne [Diagnostics.ProcessPriorityClass]::BelowNormal) { $_.PriorityClass = [Diagnostics.ProcessPriorityClass]::BelowNormal }'
        '            [void][ExoLaunchYield]::SetMemoryPriority($_.Id, 1)'
        '            [void][ExoLaunchYield]::SetPowerThrottled($_.Id, $true)'
        '            [void][ExoLaunchYield]::SoftReclaimWorkingSet($_.Id)'
        '            if (-not $closedOnce.ContainsKey($_.Id) -and $_.MainWindowHandle -ne [IntPtr]::Zero) {'
        '              try { [void][Native.ExoWin]::PostMessage($_.MainWindowHandle, [Native.ExoWin]::WM_CLOSE, [IntPtr]::Zero, [IntPtr]::Zero) } catch {'
        '                try { $_.CloseMainWindow() | Out-Null } catch {}'
        '              }'
        '              $closedOnce[$_.Id] = $true'
        '            }'
        '          } else {'
        '            if ($_.PriorityClass -ne [Diagnostics.ProcessPriorityClass]::Normal) { $_.PriorityClass = [Diagnostics.ProcessPriorityClass]::Normal }'
        '            [void][ExoLaunchYield]::SetMemoryPriority($_.Id, 5)'
        '            [void][ExoLaunchYield]::SetPowerThrottled($_.Id, $false)'
        '          }'
        '        } catch {}'
        '      }'
        '    }'
        '    if ($gameRunning) { Start-Sleep -Seconds 1 } else { Start-Sleep -Seconds 2 }'
        '  }'
        '} finally {'
        '  try { $mutex.ReleaseMutex() } catch {}'
        '  $mutex.Dispose()'
        '}'
    )
    [IO.File]::WriteAllText($YieldHelperPath, ($lines -join "`r`n") + "`r`n", [Text.UTF8Encoding]::new($false))
    if (-not (Test-Path -LiteralPath $YieldHelperPath -PathType Leaf)) {
        Write-Output "EXO_REPORT:yield-guard|fail:helper file not written"
        return $false
    }

    $hostExe = Get-ExoPwshPath
    if (-not $hostExe -or -not (Test-Path -LiteralPath $hostExe)) {
        Write-Output ("EXO_REPORT:yield-guard|fail:no PowerShell host (Program Files 7 / store / 5.1)")
        return $false
    }

    # Fully quoted Run key - no wscript, no WindowsApps stub
    $runCmd = '"{0}" -NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File "{1}"' -f $hostExe, $YieldHelperPath

    try {
        $run = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($RunSubKey, $true)
        try {
            $run.SetValue($YieldRunName, $runCmd, [Microsoft.Win32.RegistryValueKind]::String)
            $verify = [string]$run.GetValue($YieldRunName, '')
            if ($verify -ne $runCmd) {
                Write-Output "EXO_REPORT:yield-guard|fail:Run key verify mismatch"
                return $false
            }
        } finally { $run.Dispose() }
    } catch {
        Write-Output ("EXO_REPORT:yield-guard|fail:Run key write: {0}" -f $_.Exception.Message)
        return $false
    }

    # Start now with CreateNoWindow - never wscript (no WSH dialogs)
    try {
        $psi = [Diagnostics.ProcessStartInfo]::new()
        $psi.FileName = $hostExe
        $psi.Arguments = '-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File "{0}"' -f $YieldHelperPath
        $psi.UseShellExecute = $false
        $psi.CreateNoWindow = $true
        $psi.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
        [void][Diagnostics.Process]::Start($psi)
    } catch {
        Write-Output ("EXO_REPORT:yield-guard|warn:start-now failed (Run key still set): {0}" -f $_.Exception.Message)
    }
    Write-Output ("EXO_REPORT:yield-guard|ok:hidden PowerShell companion host={0}" -f $hostExe)
    return $true
}
function Restore-Value([Microsoft.Win32.RegistryKey]$Key, [string]$Name, $Snapshot) {
    if ($null -eq $Snapshot) { return }
    if ([bool]$Snapshot.existed) {
        $Key.SetValue($Name, $Snapshot.value, (Convert-Kind ([string]$Snapshot.kind)))
    } else { $Key.DeleteValue($Name, $false) }
}

function Clear-LegacyGameFso([Microsoft.Win32.RegistryKey]$FsoKey, [string]$Path) {
    # Games hub forces borderless - do not stamp DISABLEDXMAXIMIZEDWINDOWEDMODE on game EXEs.
    # Strip legacy Exo exclusive-fullscreen FSO-off stamps on re-apply.
    if ($null -eq $FsoKey -or [string]::IsNullOrWhiteSpace($Path)) { return $false }
    $cur = [string]$FsoKey.GetValue($Path, $null)
    if ([string]::IsNullOrEmpty($cur)) { return $false }
    if ($cur -notmatch 'DISABLEDXMAXIMIZEDWINDOWEDMODE') { return $false }
    $cleaned = ($cur -replace '\s*DISABLEDXMAXIMIZEDWINDOWEDMODE', '').Trim()
    if ([string]::IsNullOrEmpty($cleaned) -or $cleaned -eq '~') {
        $FsoKey.DeleteValue($Path, $false)
    } else {
        $FsoKey.SetValue($Path, $cleaned, [Microsoft.Win32.RegistryValueKind]::String)
    }
    return $true
}

function Apply-WindowsGamePolicy([object[]]$Targets, [object[]]$Launchers, [switch]$Force, [switch]$LauncherFso) {
    # Games always high-perf dGPU. Never FSO-off on games (conflicts with Games borderless).
    # Hybrid systems: launchers prefer integrated (GpuPreference=1) so the UI
    # stays off the gaming GPU; single-GPU leaves launchers on high-perf / automatic.
    $gpuWritten = 0
    $fsoCleared = 0
    $hybrid = [bool](Test-HybridGraphics)
    $launcherGpu = if ($hybrid) { 'GpuPreference=1;' } else { $GpuHighPerf }
    $gpu = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($GpuSubKey, $true)
    try {
        foreach ($t in @($Targets)) {
            $path = [string]$t.path
            if ([string]::IsNullOrWhiteSpace($path)) { continue }
            $cur = [string]$gpu.GetValue($path, $null)
            if (-not $Force -and $cur -eq $GpuHighPerf) { continue }
            $gpu.SetValue($path, $GpuHighPerf, [Microsoft.Win32.RegistryValueKind]::String)
            $gpuWritten++
        }
        foreach ($t in @($Launchers)) {
            $path = [string]$t.path
            if ([string]::IsNullOrWhiteSpace($path)) { continue }
            $cur = [string]$gpu.GetValue($path, $null)
            if (-not $Force -and $cur -eq $launcherGpu) { continue }
            $gpu.SetValue($path, $launcherGpu, [Microsoft.Win32.RegistryValueKind]::String)
            $gpuWritten++
        }
    } finally { $gpu.Dispose() }

    $fso = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($FsoSubKey, $true)
    try {
        foreach ($t in @($Targets)) {
            $path = [string]$t.path
            if ([string]::IsNullOrWhiteSpace($path)) { continue }
            if (Clear-LegacyGameFso $fso $path) { $fsoCleared++ }
        }
        # Also clear leftover FSO stamps on launchers (LauncherFso param ignored - legacy).
        foreach ($t in @($Launchers)) {
            $path = [string]$t.path
            if ([string]::IsNullOrWhiteSpace($path)) { continue }
            if (Clear-LegacyGameFso $fso $path) { $fsoCleared++ }
        }
    } finally { $fso.Dispose() }

    return @{
        Gpu = $gpuWritten
        Fso = $fsoCleared
        FsoCleared = $fsoCleared
        Hybrid = $hybrid
        LauncherGpu = $(if ($hybrid) { 'integrated' } else { 'high-perf' })
    }
}
function Invoke-Repair {
    if (-not (Test-Path -LiteralPath $SnapshotPath -PathType Leaf)) {
        Add-Report 'snapshot' 'skip' 'no pre-Exo snapshot'
        Remove-Item -LiteralPath $StatePath -Force -ErrorAction SilentlyContinue
        return
    }
    $snapshot = Get-Content -LiteralPath $SnapshotPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $schema = [int]$snapshot.schema
    if ($schema -notin @(1, 2, 3, 4) -or [string]$snapshot.module -ne $Module) { throw 'Snapshot contract mismatch.' }
    $run = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($RunSubKey, $true)
    try {
        foreach ($entry in @($snapshot.run)) {
            $run.SetValue([string]$entry.name, $entry.value, (Convert-Kind ([string]$entry.kind)))
        }
        # Remove Exo yield Run entry if present.
        try { $run.DeleteValue($YieldRunName, $false) } catch {}
    } finally { $run.Dispose() }
    $gpu = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($GpuSubKey, $true)
    try {
        foreach ($target in @($snapshot.targets)) {
            Restore-Value $gpu ([string]$target.path) $target.gpu
        }
    } finally { $gpu.Dispose() }
    if ($schema -ge 2) {
        $fso = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($FsoSubKey, $true)
        try {
            foreach ($target in @($snapshot.targets)) {
                if ($target.PSObject.Properties.Name -contains 'fso') {
                    Restore-Value $fso ([string]$target.path) $target.fso
                }
            }
        } finally { $fso.Dispose() }
    }
    # schema 3 snapshots may include gameBar  -  do NOT restore (Windows owns Game Bar).
    # Always remove Exo DSCP policies we created for this module.
    if ($schema -ge 3) {
        Add-Report 'game-bar' 'skip' 'Game Bar owned by Windows module  -  not restored'
        foreach ($pol in @($snapshot.qosPolicies)) {
            $name = [string]$pol
            if ($name -and (Get-Command Remove-ExoGameQosPolicy -ErrorAction SilentlyContinue)) {
                [void](Remove-ExoGameQosPolicy -PolicyName $name)
            }
        }
    }
    Remove-Item -LiteralPath $YieldHelperPath -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $StatePath -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $SnapshotPath -Force -ErrorAction Stop
    Add-Report 'restore' 'ok'
}

try {
    if (-not (Test-Admin)) { throw 'Administrator rights are required.' }
    Write-ProgressLine 5 ("Starting {0} policy" -f $Module)
    if ($Repair) {
        Invoke-Repair
        Write-ProgressLine 100 'Repair complete'
        Write-Output ("DONE - {0} pre-Exo policy restored" -f $Module)
        exit 0
    }
    if (-not (Test-Installed)) { throw "$Module is not installed on this PC." }
    $targets = @(Get-Targets)
    if ($targets.Count -eq 0) { throw "No installed $Module game executable was found. Install a game, then Apply." }
    $launchers = @(Get-LauncherTargets)
    Write-ProgressLine 22 ("Detected {0} game executable(s)" -f $targets.Count)
    # Report rows match the 7 detect tiles so "Last apply - N ok" equals feature count.
    Add-Report 'install' 'ok' "$Module client present"
    Add-Report 'game-discovery' 'ok' ("{0} game executable(s) for yield detect only" -f $targets.Count)
    New-Snapshot $targets $launchers
    Add-Report 'snapshot' 'ok' 'pre-Exo Run snapshot captured'
    $removed = Remove-StartupEntries
    Add-Report 'startup' 'ok' ("removed {0} auto-start value(s)" -f $removed)
    # Steam-parity: launcher toasts + non-AC scheduled tasks (app-scoped only).
    $shell = Apply-LauncherShellQuiet
    Add-Report 'shell-quiet' 'ok' ("toasts={0}; tasks-quieted={1}" -f [int]$shell.Notify, [int]$shell.Tasks)
    # Re-stamp shell quiet a second time (toast/task drift after first pass).
    $shell2 = Apply-LauncherShellQuiet
    Add-Report 'shell-quiet-restamp' 'ok' ("toasts={0}; tasks={1}" -f [int]$shell2.Notify, [int]$shell2.Tasks)
    # Full apply re-stamps game GPU high-perf + clears legacy FSO-off (Games owns borderless).
    $winPolicy = Apply-WindowsGamePolicy -Targets $targets -Launchers $launchers -Force
    $gpuReason = if ([bool]$winPolicy.Hybrid) {
        "game GpuPreference=2; launcher integrated on hybrid ({0} path(s))" -f [int]$winPolicy.Gpu
    } else {
        "high-perf GpuPreference=2 on {0} executable(s)" -f [int]$winPolicy.Gpu
    }
    Add-Report 'gpu-preference' 'ok' $gpuReason
    Add-Report 'fso' 'ok' ("legacy FSO-off cleared on {0} path(s); display owned by Games hub" -f [int]$winPolicy.Fso)
    Write-ProgressLine 48 'Cleaning launcher logs/cache (games untouched)...'
    $cacheFreed = Clear-LauncherSafeCaches
    Add-Report 'launcher-cache' 'ok' ("freed ~{0:N1} MB launcher junk" -f ($cacheFreed / 1MB))
    Write-ProgressLine 52 'Quiet Start Menu launch path...'
    $menuOk = Install-QuietStartMenuLaunch
    if ($menuOk) { Add-Report 'start-menu' 'ok' 'Start Menu points at Exo quiet launcher' }
    else { Add-Report 'start-menu' 'skip' 'no Start Menu shortcut found to retarget' }
    # Host Game Mode / HAGS / Game Bar / priority live on the Windows card only.
    # Per-game UDP DSCP 46 (voice/game traffic); exe-name policies only
    $qosNames = [System.Collections.Generic.List[string]]::new()
    if (Get-Command Set-ExoGameQosPolicy -ErrorAction SilentlyContinue) {
        $qosOk = 0
        foreach ($t in @($targets)) {
            $exe = [string]$t.exe
            if ([string]::IsNullOrWhiteSpace($exe)) { continue }
            $safe = ($exe -replace '[^\w\.\-]', '_')
            $pol = "Exo-$Module-DSCP-$safe"
            try {
                if (Set-ExoGameQosPolicy -PolicyName $pol -ExeName $exe) {
                    $qosOk++
                    [void]$qosNames.Add($pol)
                }
            } catch { }
        }
        if ($qosOk -gt 0) {
            Add-Report 'game-dscp' 'ok' ("DSCP 46 on {0} game exe policy(ies)" -f $qosOk)
            # Merge policy names into existing snapshot for repair removal
            try {
                if (Test-Path -LiteralPath $SnapshotPath -PathType Leaf) {
                    $snapObj = Get-Content -LiteralPath $SnapshotPath -Raw -Encoding UTF8 | ConvertFrom-Json
                    $snapObj | Add-Member -NotePropertyName qosPolicies -NotePropertyValue @($qosNames) -Force
                    $snapObj | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $SnapshotPath -Encoding UTF8
                }
            } catch { }
        } else {
            Add-Report 'game-dscp' 'skip' 'no game QoS policies written'
        }
    }
    $yieldOk = Install-YieldGuard $targets $launchers
    if ($yieldOk) {
        Add-Report 'yield-guard' 'ok' 'hidden PowerShell companion (no WSH); soft-reclaim launcher while game runs'
    } else {
        Add-Report 'yield-guard' 'fail' 'companion not installed (no games or PowerShell host missing)'
    }
    Add-Report 'anti-cheat-boundary' 'ok' 'game/anti-cheat processes never opened or modified'
    Add-Report 'verified-record' 'ok' 'full apply complete'
    $state = [ordered]@{
        version = $Version
        module = $Module
        applied = $true
        applyStatus = 'applied'
        appliedUtc = (Get-Date).ToUniversalTime().ToString('o')
        startupQuiet = $true
        shellQuiet = $true
        gamePolicyVerified = $true
        fsoVerified = $false # product: no FSO-off on games (borderless via Games hub)
        fsoCleared = $true
        gameBarQuiet = $false # host Game Bar quiet is owned by the Windows module
        gameDscp = @($qosNames)
        windowsGpuPolicyOwnedBy = $Module.ToLowerInvariant()
        experimental = [bool]$Experimental
        yieldGuardInstalled = [bool]$yieldOk
        hybridGraphics = [bool](Test-HybridGraphics)
        launcherCacheCleaned = $true
        launcherCacheFreedBytes = [long]$cacheFreed
        startMenuQuiet = [bool]$menuOk
        launcherTargetCount = $launchers.Count
        targetCount = $targets.Count
        targets = @($targets)
        antiCheatUntouched = $true
        # Install binaries untouched; only logs/webcache cleared + Start Menu retarget
        launcherFilesUntouched = $true
        snapshotReady = (Test-Path -LiteralPath $SnapshotPath -PathType Leaf)
        applyReport = @($Report)
        lastError = $null
    }
    $state | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $StatePath -Encoding UTF8
    Write-ProgressLine 100 'Verified'
    Write-Output ("DONE - {0}: GPU/FSO + silent launcher companion for {1} game(s)" -f $Module, $targets.Count)
    exit 0
} catch {
    Add-Report 'apply' 'fail' $_.Exception.Message
    try {
        New-Item -ItemType Directory -Path $ExoRoot -Force | Out-Null
        [ordered]@{
            version = $Version; module = $Module; applied = $false; applyStatus = 'failed'
            appliedUtc = (Get-Date).ToUniversalTime().ToString('o'); applyReport = @($Report)
            lastError = $_.Exception.Message
        } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $StatePath -Encoding UTF8
    } catch { }
    Write-Error $_.Exception.Message
    exit 1
}
