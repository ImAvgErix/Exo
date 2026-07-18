# Exo Riot / Epic optimizer.
# Reversible Windows policy only: quiet startup plus capability-aware GPU routing.
# Games prefer the high-performance adapter; on hybrid PCs launcher UI prefers the
# power-saving adapter so it does not wake or contend with the discrete GPU.
# Anti-cheat, services, files, caches, and game scheduling are left untouched.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('Riot','Epic')][string]$Module,
    [switch]$Repair,
    [switch]$NonInteractive
)

$ErrorActionPreference = 'Stop'
$Version = '1.2.0'
$ExoRoot = Join-Path $env:LOCALAPPDATA 'Exo'
$StatePath = Join-Path $ExoRoot ("{0}-optimizer.json" -f $Module.ToLowerInvariant())
$SnapshotPath = Join-Path $ExoRoot ("{0}-snapshot.json" -f $Module.ToLowerInvariant())
$YieldHelperPath = Join-Path $ExoRoot ("{0}-yield-guard.ps1" -f $Module.ToLowerInvariant())
$YieldRunName = "Exo-$Module-Yield"
$RunSubKey = 'Software\Microsoft\Windows\CurrentVersion\Run'
$GpuSubKey = 'Software\Microsoft\DirectX\UserGpuPreferences'
$FsoSubKey = 'Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers'
$FsoFlags = '~ DISABLEDXMAXIMIZEDWINDOWEDMODE HIGHDPIAWARE'
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
function Get-LauncherTargets {
    $targets = [System.Collections.Generic.List[object]]::new()
    if ($Module -eq 'Riot') {
        foreach ($path in @(
            (Join-Path ${env:SystemDrive} 'Riot Games\Riot Client\RiotClientServices.exe'),
            (Join-Path ${env:SystemDrive} 'Riot Games\Riot Client\RiotClientUx.exe'),
            (Join-Path ${env:SystemDrive} 'Riot Games\Riot Client\RiotClientUxRender.exe')
        )) { Add-Target $targets $path 'riot-launcher' }
    } else {
        $root = Join-Path ${env:ProgramFiles(x86)} 'Epic Games\Launcher\Portal\Binaries\Win64'
        foreach ($name in @('EpicGamesLauncher.exe','EpicWebHelper.exe')) {
            Add-Target $targets (Join-Path $root $name) 'epic-launcher'
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
    $snapshot = [ordered]@{
        schema = 2
        module = $Module
        capturedUtc = (Get-Date).ToUniversalTime().ToString('o')
        run = @(Get-RunMatches)
        targets = @($targetStates)
        yieldRunName = $YieldRunName
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
function Apply-TargetPolicy([object[]]$Targets, [object[]]$Launchers, [bool]$HybridGraphics) {
    $gpu = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($GpuSubKey, $true)
    try {
        foreach ($target in $Targets) {
            $gpu.SetValue([string]$target.path, 'GpuPreference=2;', [Microsoft.Win32.RegistryValueKind]::String)
        }
        if ($HybridGraphics) {
            foreach ($target in $Launchers) {
                $gpu.SetValue([string]$target.path, 'GpuPreference=1;', [Microsoft.Win32.RegistryValueKind]::String)
            }
        }
    } finally { $gpu.Dispose() }
}
function Apply-FsoPolicy([object[]]$Targets) {
    # Fullscreen Optimizations off + DPI aware for game exes only (never anti-cheat services).
    $fso = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($FsoSubKey, $true)
    try {
        foreach ($target in $Targets) {
            $fso.SetValue([string]$target.path, $FsoFlags, [Microsoft.Win32.RegistryValueKind]::String)
        }
    } finally { $fso.Dispose() }
}
function Install-YieldGuard([object[]]$Targets, [object[]]$Launchers) {
    # Live companion: while a game runs, demote launcher UI only (memory priority + EcoQoS).
    # Never opens, suspends, or modifies game/anti-cheat processes.
    $gameNames = @($Targets | ForEach-Object { [IO.Path]::GetFileNameWithoutExtension([string]$_.path) } | Sort-Object -Unique)
    $launcherNames = @($Launchers | ForEach-Object { [IO.Path]::GetFileNameWithoutExtension([string]$_.path) } | Sort-Object -Unique)
    if ($gameNames.Count -eq 0 -or $launcherNames.Count -eq 0) { return $false }
    $gameList = ($gameNames | ForEach-Object { "'$_'" }) -join ','
    $launcherList = ($launcherNames | ForEach-Object { "'$_'" }) -join ','
    $lines = @(
        "# Exo $Module launcher yield guard. Never touches game or anti-cheat processes."
        '$ErrorActionPreference = ''SilentlyContinue'''
        '$created = $false'
        ("`$mutex = [Threading.Mutex]::new(`$true, 'Local\Exo.{0}.YieldGuard', [ref]`$created)" -f $Module)
        'if (-not $created) { $mutex.Dispose(); exit 0 }'
        'Add-Type -TypeDefinition @"'
        'using System;'
        'using System.Runtime.InteropServices;'
        'public static class ExoLaunchYield {'
        '  [StructLayout(LayoutKind.Sequential)] struct MEMORY_PRIORITY_INFORMATION { public uint MemoryPriority; }'
        '  [StructLayout(LayoutKind.Sequential)] struct PROCESS_POWER_THROTTLING_STATE { public uint Version; public uint ControlMask; public uint StateMask; }'
        '  [DllImport("kernel32.dll", SetLastError=true)] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);'
        '  [DllImport("kernel32.dll", SetLastError=true)] static extern bool SetProcessInformation(IntPtr process, int infoClass, ref MEMORY_PRIORITY_INFORMATION info, uint size);'
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
        '}'
        '"@'
        ("`$games = @({0})" -f $gameList)
        ("`$launchers = @({0})" -f $launcherList)
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
        '            if ($_.PriorityClass -ne [Diagnostics.ProcessPriorityClass]::BelowNormal) {'
        '              $_.PriorityClass = [Diagnostics.ProcessPriorityClass]::BelowNormal'
        '            }'
        '            [void][ExoLaunchYield]::SetMemoryPriority($_.Id, 1)'
        '            [void][ExoLaunchYield]::SetPowerThrottled($_.Id, $true)'
        '          } else {'
        '            if ($_.PriorityClass -ne [Diagnostics.ProcessPriorityClass]::Normal) {'
        '              $_.PriorityClass = [Diagnostics.ProcessPriorityClass]::Normal'
        '            }'
        '            [void][ExoLaunchYield]::SetMemoryPriority($_.Id, 5)'
        '            [void][ExoLaunchYield]::SetPowerThrottled($_.Id, $false)'
        '          }'
        '        } catch {}'
        '      }'
        '    }'
        '    Start-Sleep -Seconds 3'
        '  }'
        '} finally {'
        '  try { $mutex.ReleaseMutex() } catch {}'
        '  $mutex.Dispose()'
        '}'
    )
    [IO.File]::WriteAllText($YieldHelperPath, ($lines -join "`r`n") + "`r`n", [Text.UTF8Encoding]::new($false))
    $pwsh = $null
    try { $pwsh = (Get-Command pwsh -ErrorAction SilentlyContinue).Source } catch {}
    if (-not $pwsh) { $pwsh = Join-Path $env:ProgramFiles 'PowerShell\7\pwsh.exe' }
    if (Test-Path -LiteralPath $pwsh) {
        $run = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($RunSubKey, $true)
        try {
            $cmd = '"{0}" -NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File "{1}"' -f $pwsh, $YieldHelperPath
            $run.SetValue($YieldRunName, $cmd, [Microsoft.Win32.RegistryValueKind]::String)
        } finally { $run.Dispose() }
        try {
            Start-Process -FilePath $pwsh -ArgumentList @('-NoProfile','-WindowStyle','Hidden','-ExecutionPolicy','Bypass','-File',$YieldHelperPath) -WindowStyle Hidden | Out-Null
        } catch {}
    }
    return $true
}
function Restore-Value([Microsoft.Win32.RegistryKey]$Key, [string]$Name, $Snapshot) {
    if ($null -eq $Snapshot) { return }
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
    $schema = [int]$snapshot.schema
    if ($schema -notin @(1, 2) -or [string]$snapshot.module -ne $Module) { throw 'Snapshot contract mismatch.' }
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
    $hybridGraphics = Test-HybridGraphics
    Write-ProgressLine 22 ("Detected {0} game executable(s)" -f $targets.Count)
    New-Snapshot $targets $launchers
    Add-Report 'snapshot' 'ok'
    $removed = Remove-StartupEntries
    Add-Report 'startup' 'ok' ("removed {0} auto-start value(s)" -f $removed)
    Apply-TargetPolicy $targets $launchers $hybridGraphics
    Add-Report 'gpu-routing' 'ok' ("{0} game(s); {1}" -f $targets.Count, $(if ($hybridGraphics) { "$($launchers.Count) launcher process(es) on integrated GPU" } else { 'single-GPU path' }))
    Apply-FsoPolicy $targets
    Add-Report 'fso' 'ok' 'Fullscreen Optimizations off for detected game executables'
    $yieldOk = Install-YieldGuard $targets $launchers
    if ($yieldOk) { Add-Report 'yield-guard' 'ok' 'launcher EcoQoS while game runs' }
    else { Add-Report 'yield-guard' 'skip' 'no launcher processes to demote' }
    $verified = 0
    $gpu = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($GpuSubKey)
    try {
        foreach ($target in $targets) {
            $gpuOk = [string]$gpu.GetValue([string]$target.path, '') -eq 'GpuPreference=2;'
            if ($gpuOk) { $verified++ }
        }
    } finally { if ($gpu) { $gpu.Dispose() } }
    if ($verified -ne $targets.Count) { throw "Only $verified of $($targets.Count) game policies verified." }
    $fsoOk = 0
    $fsoKey = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($FsoSubKey)
    try {
        foreach ($target in $targets) {
            if ([string]$fsoKey.GetValue([string]$target.path, '') -match 'DISABLEDXMAXIMIZEDWINDOWEDMODE') { $fsoOk++ }
        }
    } finally { if ($fsoKey) { $fsoKey.Dispose() } }
    Add-Report 'verify' 'ok' ("gpu={0}/{1}; fso={2}/{1}" -f $verified, $targets.Count, $fsoOk)
    $state = [ordered]@{
        version = $Version
        module = $Module
        applied = $true
        applyStatus = 'applied'
        appliedUtc = (Get-Date).ToUniversalTime().ToString('o')
        startupQuiet = $true
        gamePolicyVerified = $true
        fsoVerified = ($fsoOk -eq $targets.Count)
        yieldGuardInstalled = [bool]$yieldOk
        hybridGraphics = [bool]$hybridGraphics
        launcherTargetCount = $launchers.Count
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
    Write-Output ("DONE - {0}: startup quiet; GPU routing; FSO off; yield guard for {1} game executable(s)" -f $Module, $targets.Count)
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
