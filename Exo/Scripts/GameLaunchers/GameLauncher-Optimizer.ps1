# Exo Riot / Epic optimizer.
# Reversible Windows policy only: quiet startup plus per-game GPU preference and
# Above Normal CPU priority. Anti-cheat, launchers, services, files, and caches
# are deliberately left untouched.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('Riot','Epic')][string]$Module,
    [switch]$Repair,
    [switch]$NonInteractive
)

$ErrorActionPreference = 'Stop'
$Version = '1.0.1'
$ExoRoot = Join-Path $env:LOCALAPPDATA 'Exo'
$StatePath = Join-Path $ExoRoot ("{0}-optimizer.json" -f $Module.ToLowerInvariant())
$SnapshotPath = Join-Path $ExoRoot ("{0}-snapshot.json" -f $Module.ToLowerInvariant())
$RunSubKey = 'Software\Microsoft\Windows\CurrentVersion\Run'
$GpuSubKey = 'Software\Microsoft\DirectX\UserGpuPreferences'
$IfeoBase = 'SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options'
$Report = [System.Collections.Generic.List[string]]::new()

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
        "$( $_.DisplayName ) $( $_.Publisher )" -match '(?i)Riot|VALORANT|League of Legends'
    })) {
        $root = Normalize-Path ([string]$entry.InstallLocation)
        if ($root -and (Test-Path -LiteralPath $root -PathType Container)) { [void]$roots.Add($root) }
    }
    foreach ($procName in @('VALORANT-Win64-Shipping','VALORANT','League of Legends')) {
        Get-Process -Name $procName -ErrorAction SilentlyContinue | ForEach-Object {
            try { Add-Target $targets ([string]$_.Path) 'running-process' } catch { }
        }
    }
    $gameNames = @(
        'VALORANT-Win64-Shipping.exe', 'VALORANT.exe',
        'League of Legends.exe'
    )
    foreach ($root in @($roots | Sort-Object -Unique)) {
        Get-ChildItem -LiteralPath $root -File -Recurse -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -in $gameNames } |
            ForEach-Object { Add-Target $targets $_.FullName 'riot-install' }
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
    foreach ($process in @(Get-Process -ErrorAction SilentlyContinue)) {
        try {
            $path = [string]$process.Path
            if ($path -and $path -match '(?i)\\Epic Games\\' -and
                [IO.Path]::GetFileName($path) -notmatch '(?i)^EpicGamesLauncher|EpicWebHelper') {
                Add-Target $targets $path 'running-process'
            }
        } catch { }
    }
    return @($targets)
}
function Get-Targets { if ($Module -eq 'Riot') { @(Get-RiotTargets) } else { @(Get-EpicTargets) } }
function Test-Installed {
    if ($Module -eq 'Riot') {
        if (Test-Path -LiteralPath (Join-Path ${env:ProgramFiles} 'Riot Vanguard') -PathType Container) { return $true }
        if (Test-Path -LiteralPath (Join-Path ${env:SystemDrive} 'Riot Games') -PathType Container) { return $true }
    } else {
        $epic = Join-Path ${env:ProgramFiles(x86)} 'Epic Games\Launcher\Portal\Binaries\Win64\EpicGamesLauncher.exe'
        if (Test-Path -LiteralPath $epic -PathType Leaf) { return $true }
    }
    return @((Get-UninstallEntries) | Where-Object { "$( $_.DisplayName ) $( $_.Publisher )" -match "(?i)$Module" }).Count -gt 0
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
function New-Snapshot([object[]]$Targets) {
    if (Test-Path -LiteralPath $SnapshotPath -PathType Leaf) { return }
    $gpu = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($GpuSubKey)
    $targetStates = [System.Collections.Generic.List[object]]::new()
    try {
        foreach ($target in $Targets) {
            $ifeoSub = "$IfeoBase\$($target.exe)\PerfOptions"
            $ifeo = [Microsoft.Win32.Registry]::LocalMachine.OpenSubKey($ifeoSub)
            try {
                [void]$targetStates.Add([ordered]@{
                    path = $target.path
                    exe = $target.exe
                    gpu = Get-ValueSnapshot $gpu ([string]$target.path)
                    cpu = Get-ValueSnapshot $ifeo 'CpuPriorityClass'
                    ifeoKeyExisted = ($null -ne $ifeo)
                })
            } finally { if ($ifeo) { $ifeo.Dispose() } }
        }
    } finally { if ($gpu) { $gpu.Dispose() } }
    $snapshot = [ordered]@{
        schema = 1
        module = $Module
        capturedUtc = (Get-Date).ToUniversalTime().ToString('o')
        run = @(Get-RunMatches)
        targets = @($targetStates)
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
            $value = [string]$key.GetValue($name, '')
            if ("$name $value" -match "(?i)$Module") { $key.DeleteValue($name, $false); $removed++ }
        }
    } finally { $key.Dispose() }
    return $removed
}
function Apply-TargetPolicy([object[]]$Targets) {
    $gpu = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($GpuSubKey, $true)
    try {
        foreach ($target in $Targets) {
            $gpu.SetValue([string]$target.path, 'GpuPreference=2;', [Microsoft.Win32.RegistryValueKind]::String)
            $ifeoSub = "$IfeoBase\$($target.exe)\PerfOptions"
            $ifeo = [Microsoft.Win32.Registry]::LocalMachine.CreateSubKey($ifeoSub, $true)
            try { $ifeo.SetValue('CpuPriorityClass', 6, [Microsoft.Win32.RegistryValueKind]::DWord) }
            finally { $ifeo.Dispose() }
        }
    } finally { $gpu.Dispose() }
}
function Restore-Value([Microsoft.Win32.RegistryKey]$Key, [string]$Name, $Snapshot) {
    if ([bool]$Snapshot.existed) {
        $Key.SetValue($Name, $Snapshot.value, (Convert-Kind ([string]$Snapshot.kind)))
    } else { $Key.DeleteValue($Name, $false) }
}
function Invoke-Repair {
    if (-not (Test-Path -LiteralPath $SnapshotPath -PathType Leaf)) {
        Add-Report 'snapshot' 'skip' 'no pre-Exo snapshot'
        Remove-Item -LiteralPath $StatePath -Force -ErrorAction SilentlyContinue
        return
    }
    $snapshot = Get-Content -LiteralPath $SnapshotPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if ([int]$snapshot.schema -ne 1 -or [string]$snapshot.module -ne $Module) { throw 'Snapshot contract mismatch.' }
    $run = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($RunSubKey, $true)
    try {
        foreach ($entry in @($snapshot.run)) {
            $run.SetValue([string]$entry.name, $entry.value, (Convert-Kind ([string]$entry.kind)))
        }
    } finally { $run.Dispose() }
    $gpu = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($GpuSubKey, $true)
    try {
        foreach ($target in @($snapshot.targets)) {
            Restore-Value $gpu ([string]$target.path) $target.gpu
            $ifeoSub = "$IfeoBase\$($target.exe)\PerfOptions"
            $ifeo = [Microsoft.Win32.Registry]::LocalMachine.CreateSubKey($ifeoSub, $true)
            try { Restore-Value $ifeo 'CpuPriorityClass' $target.cpu }
            finally { $ifeo.Dispose() }
            if (-not [bool]$target.ifeoKeyExisted) {
                $parent = [Microsoft.Win32.Registry]::LocalMachine.OpenSubKey("$IfeoBase\$($target.exe)", $true)
                try {
                    $perf = $parent.OpenSubKey('PerfOptions')
                    $empty = $perf -and $perf.ValueCount -eq 0 -and $perf.SubKeyCount -eq 0
                    if ($perf) { $perf.Dispose() }
                    if ($empty) { $parent.DeleteSubKey('PerfOptions', $false) }
                } finally { if ($parent) { $parent.Dispose() } }
            }
        }
    } finally { $gpu.Dispose() }
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
    Write-ProgressLine 22 ("Detected {0} game executable(s)" -f $targets.Count)
    New-Snapshot $targets
    Add-Report 'snapshot' 'ok'
    $removed = Remove-StartupEntries
    Add-Report 'startup' 'ok' ("removed {0} auto-start value(s)" -f $removed)
    Apply-TargetPolicy $targets
    Add-Report 'game-policy' 'ok' ("{0} executable(s)" -f $targets.Count)
    $verified = 0
    $gpu = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($GpuSubKey)
    try {
        foreach ($target in $targets) {
            $gpuOk = [string]$gpu.GetValue([string]$target.path, '') -eq 'GpuPreference=2;'
            $ifeo = [Microsoft.Win32.Registry]::LocalMachine.OpenSubKey("$IfeoBase\$($target.exe)\PerfOptions")
            try { $cpuOk = $ifeo -and [int]$ifeo.GetValue('CpuPriorityClass', 0) -eq 6 }
            finally { if ($ifeo) { $ifeo.Dispose() } }
            if ($gpuOk -and $cpuOk) { $verified++ }
        }
    } finally { if ($gpu) { $gpu.Dispose() } }
    if ($verified -ne $targets.Count) { throw "Only $verified of $($targets.Count) game policies verified." }
    Add-Report 'verify' 'ok'
    $state = [ordered]@{
        version = $Version
        module = $Module
        applied = $true
        applyStatus = 'applied'
        appliedUtc = (Get-Date).ToUniversalTime().ToString('o')
        startupQuiet = $true
        gamePolicyVerified = $true
        targetCount = $targets.Count
        targets = @($targets)
        antiCheatUntouched = $true
        launcherFilesUntouched = $true
        snapshotReady = (Test-Path -LiteralPath $SnapshotPath -PathType Leaf)
        applyReport = @($Report)
        lastError = $null
    }
    $state | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $StatePath -Encoding UTF8
    Write-ProgressLine 100 'Verified'
    Write-Output ("DONE - {0}: startup quiet; {1} game executable(s) hardware-prioritized" -f $Module, $targets.Count)
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
