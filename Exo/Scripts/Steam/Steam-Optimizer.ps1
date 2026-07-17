# Steam Optimizer - multi-PC safe tweaks focused on Steam/CEF responsiveness and CPU contention.
# Steam is CEF/Chromium (not Electron) so Discord-style asar/kernel inject does
# not apply. Instead we use Valve CEF flags, interface settings, cache cleanup,
# startup quieting, and an in-game CPU contention guard for webhelper.
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
$Script:SteamOptVersion = '1.9.6'
$Root = Split-Path -Parent $MyInvocation.MyCommand.Path

# --- PowerShell 7 host (stable pwsh 7.x; never Windows PowerShell 5.1) ---
function Test-ExoIsPwsh7Host {
    # Any pwsh 7.x host is accepted (stable preferred; preview tolerated).
    # Windows PowerShell 5.1 is rejected - the optimizer uses Core-only APIs.
    if ($PSVersionTable.PSEdition -ne 'Core') { return $false }
    if ([int]$PSVersionTable.PSVersion.Major -lt 7) { return $false }
    $hostPath = ''
    try { $hostPath = [string](Get-Process -Id $PID -ErrorAction Stop).Path } catch { }
    if ($hostPath -match 'WindowsPowerShell') { return $false }
    return $true
}
function Get-ExoPwsh {
    # Stable PowerShell 7 first; preview paths only as a last resort.
    $candidates = [System.Collections.Generic.List[string]]::new()
    $stable = Join-Path $env:ProgramFiles 'PowerShell\7\pwsh.exe'
    if ($stable) { [void]$candidates.Add($stable) }

    $cmdPwsh = Get-Command pwsh -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($cmdPwsh -and $cmdPwsh.Source) { [void]$candidates.Add([string]$cmdPwsh.Source) }

    $appsRoot = Join-Path $env:ProgramFiles 'WindowsApps'
    if (Test-Path -LiteralPath $appsRoot) {
        Get-ChildItem -LiteralPath $appsRoot -Directory -Filter 'Microsoft.PowerShell_*' -ErrorAction SilentlyContinue |
            Sort-Object Name -Descending |
            ForEach-Object { [void]$candidates.Add((Join-Path $_.FullName 'pwsh.exe')) }
    }

    # Preview is a fallback only - never the requirement.
    foreach ($p in @(
        (Join-Path $env:ProgramFiles 'PowerShell\7-preview\pwsh.exe'),
        (Join-Path $env:LOCALAPPDATA 'Microsoft\WindowsApps\pwsh-preview.exe')
    )) {
        if ($p) { [void]$candidates.Add($p) }
    }

    foreach ($p in ($candidates | Select-Object -Unique)) {
        if (-not $p -or $p -match 'WindowsPowerShell') { continue }
        if (Test-Path -LiteralPath $p) { return $p }
    }
    throw 'PowerShell 7 is required for Exo Steam helpers. Install it with: winget install Microsoft.PowerShell'
}
function Assert-ExoPwsh7 {
    if (Test-ExoIsPwsh7Host) { return }
    $hint = $null
    try { $hint = Get-ExoPwsh } catch { }
    $msg = 'PowerShell 7 is required to run the Steam Optimizer (not Windows PowerShell 5.1). Install it with: winget install Microsoft.PowerShell, then re-run from Exo.'
    if ($hint) { $msg += " Found PowerShell 7 at: $hint" }
    throw $msg
}
Assert-ExoPwsh7

# Default Steam launch flags (quiet cold start).
# NEVER use -cef-disable-gpu / -cef-disable-gpu-compositing - modern Steam's
# steamwebhelper goes blank or freezes on many GPUs (2024+ CEF).
# Also forbidden: occlusion / renderer-accessibility disables, -silent on Start Menu.
$Script:DefaultCefArgs = @(
    '-nofriendsui',
    '-nointro',
    '-nobigpicture',
    '-vrdisable',
    '-cef-disable-breakpad',
    '-cef-disable-spell-checking'
)

function Write-HubProgress([int]$Percent, [string]$Status) {
    $p = [Math]::Max(0, [Math]::Min(100, $Percent))
    $line = "EXO_PROGRESS:$p|$Status"
    # Host + EXO_LOG (do not Write-Output-only; elevated host polls the log).
    Write-Host $line
    Write-Output $line
    if ($env:EXO_LOG) {
        try { Add-Content -LiteralPath $env:EXO_LOG -Value $line -Encoding utf8 -ErrorAction SilentlyContinue } catch { }
    }
}
function Write-SteamLog([string]$Prefix, [string]$Msg) {
    $line = "$Prefix $Msg"
    Write-Host $line
    if ($env:EXO_LOG) {
        try { Add-Content -LiteralPath $env:EXO_LOG -Value $line -Encoding UTF8 -ErrorAction SilentlyContinue } catch { }
    }
}
function Write-Step([string]$Msg) { Write-SteamLog '[*]' $Msg }
function Write-Ok([string]$Msg)   { Write-SteamLog '[+]' $Msg }
function Write-Warn([string]$Msg) { Write-SteamLog '[!]' $Msg }
function Write-Err([string]$Msg)  { Write-SteamLog '[-]' $Msg }

$Script:ExoApplyReport = [Collections.Generic.List[string]]::new()
function Add-ExoReport([string]$Step, [string]$Status, [string]$Reason = '') {
    # Structured last-apply report line: EXO_REPORT:<step>|ok / |fail:<reason> / |skip:<reason>
    $entry = if ([string]::IsNullOrWhiteSpace($Reason)) { "$Step|$Status" } else { "$Step|$Status`:$Reason" }
    [void]$Script:ExoApplyReport.Add($entry)
    $line = "EXO_REPORT:$entry"
    Write-Host $line
    if ($env:EXO_LOG) {
        try { Add-Content -LiteralPath $env:EXO_LOG -Value $line -Encoding UTF8 -ErrorAction SilentlyContinue } catch { }
    }
}
function Get-ExoReportEntries {
    return @($Script:ExoApplyReport)
}

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

function Get-ExoSteamStatePath {
    $dir = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Exo'
    if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    return (Join-Path $dir 'steam-optimizer.json')
}

function Save-SteamOptState([hashtable]$State) {
    $path = Get-ExoSteamStatePath
    $json = $State | ConvertTo-Json -Depth 12
    $temp = "$path.$PID.$([Guid]::NewGuid().ToString('N')).tmp"
    try {
        [IO.File]::WriteAllText($temp, $json, [Text.UTF8Encoding]::new($false))
        Move-Item -LiteralPath $temp -Destination $path -Force
    } finally {
        Remove-Item -LiteralPath $temp -Force -ErrorAction SilentlyContinue
    }
}

function Read-SteamOptState {
    $path = Get-ExoSteamStatePath
    if (-not (Test-Path -LiteralPath $path)) { return $null }
    try { return (Get-Content -LiteralPath $path -Raw -Encoding UTF8 | ConvertFrom-Json) }
    catch { return $null }
}

function Get-SteamObjectProperty($Object, [string]$Name, $Default = $null) {
    if (-not $Object) { return $Default }
    if ($Object -is [hashtable] -and $Object.ContainsKey($Name)) { return $Object[$Name] }
    if ($Object.PSObject -and ($Object.PSObject.Properties.Name -contains $Name)) { return $Object.$Name }
    return $Default
}

function Get-SteamRecoveryFromState($State) {
    if (-not $State) { return $null }
    $source = if ($State.PSObject.Properties.Name -contains 'recovery') { $State.recovery } else { $State }
    if (-not $source) { return $null }

    $names = @($source.PSObject.Properties.Name)
    $hasEntries = $names -contains 'startupEntries'
    $hasMode = $names -contains 'startupModeCaptured'
    $hasLegacyMode = $names -contains 'hadStartupMode'
    $hasWindowsRecovery = $hasEntries -or $hasMode -or $hasLegacyMode -or
        ($names -contains 'scheduledTasks') -or
        ($names -contains 'notifications') -or
        ($names -contains 'trayEntries') -or
        ($names -contains 'appPath') -or
        ($names -contains 'clientPerformance')
    if (-not $hasWindowsRecovery) { return $null }

    return @{
        StartupEntries          = @(Get-SteamObjectProperty $source 'startupEntries' @() | Where-Object { $_ })
        StartupModeCaptured    = if ($hasMode) { [bool]$source.startupModeCaptured } else { $hasLegacyMode }
        HadStartupMode         = if ($hasLegacyMode) { [bool]$source.hadStartupMode } else { $false }
        PreviousStartupMode    = $source.previousStartupMode
        PreviousStartupModeKind = if ($names -contains 'previousStartupModeKind') {
            [string]$source.previousStartupModeKind
        } else { 'DWord' }
        ScheduledTasks         = @(Get-SteamObjectProperty $source 'scheduledTasks' @() | Where-Object { $_ })
        Notifications          = @(Get-SteamObjectProperty $source 'notifications' @() | Where-Object { $_ })
        TrayEntries            = @(Get-SteamObjectProperty $source 'trayEntries' @() | Where-Object { $_ })
        AppPath                = Get-SteamObjectProperty $source 'appPath' $null
        ClientPerformance      = @(Get-SteamObjectProperty $source 'clientPerformance' @() | Where-Object { $_ })
    }
}

function Get-SteamRegistryValueSnapshot([string]$Key, [string]$Name) {
    if (-not (Test-Path $Key)) { return $null }
    $item = Get-Item -Path $Key -ErrorAction Stop
    if ($item.GetValueNames() -notcontains $Name) { return $null }
    return @{
        Key   = $Key
        Name  = $Name
        Value = $item.GetValue($Name, $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
        Kind  = $item.GetValueKind($Name).ToString()
    }
}

function Restore-SteamRegistryValue($Entry) {
    if (-not (Test-Path $Entry.Key)) { New-Item -Path $Entry.Key -Force -ErrorAction Stop | Out-Null }
    $kind = if ([string]$Entry.Kind -in @('String', 'ExpandString', 'Binary', 'DWord', 'MultiString', 'QWord')) {
        [string]$Entry.Kind
    } else { 'String' }
    $value = switch ($kind) {
        'Binary' { [byte[]]$Entry.Value; break }
        'DWord' { [int]$Entry.Value; break }
        'QWord' { [long]$Entry.Value; break }
        'MultiString' { [string[]]$Entry.Value; break }
        default { [string]$Entry.Value }
    }
    New-ItemProperty -Path $Entry.Key -Name ([string]$Entry.Name) -Value $value -PropertyType $kind -Force -ErrorAction Stop | Out-Null
    $item = Get-Item -Path $Entry.Key -ErrorAction Stop
    if ($item.GetValueNames() -notcontains [string]$Entry.Name) { throw 'registry value is missing after restore' }
    $actual = $item.GetValue([string]$Entry.Name, $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
    if ($item.GetValueKind([string]$Entry.Name).ToString() -ne $kind -or
        (($actual | ConvertTo-Json -Compress -Depth 4) -ne ($value | ConvertTo-Json -Compress -Depth 4))) {
        throw 'registry value verification failed'
    }
}

function Get-SteamClientPerformanceSnapshot {
    $key = 'HKCU:\Software\Valve\Steam'
    $entries = [Collections.Generic.List[hashtable]]::new()
    foreach ($name in @('H264HWAccel', 'GPUAccelWebViews', 'GPUAccelWebViewsV3')) {
        $snapshot = Get-SteamRegistryValueSnapshot $key $name
        $entries.Add(@{
            Key     = $key
            Name    = $name
            Existed = [bool]$snapshot
            Value   = if ($snapshot) { $snapshot.Value } else { $null }
            Kind    = if ($snapshot) { [string]$snapshot.Kind } else { 'DWord' }
        })
    }
    return @($entries)
}

function Test-SteamClientHardwareAcceleration {
    $key = 'HKCU:\Software\Valve\Steam'
    if (-not (Test-Path $key)) { return $false }
    try {
        $item = Get-Item -Path $key -ErrorAction Stop
        foreach ($name in @('H264HWAccel', 'GPUAccelWebViews', 'GPUAccelWebViewsV3')) {
            if ($item.GetValueNames() -notcontains $name) { return $false }
            if ([int]$item.GetValue($name, 0) -ne 1) { return $false }
        }
        return $true
    } catch { return $false }
}

function Set-SteamClientHardwareAcceleration {
    $key = 'HKCU:\Software\Valve\Steam'
    if (-not (Test-Path $key)) { New-Item -Path $key -Force -ErrorAction Stop | Out-Null }
    foreach ($name in @('H264HWAccel', 'GPUAccelWebViews', 'GPUAccelWebViewsV3')) {
        New-ItemProperty -Path $key -Name $name -Value 1 -PropertyType DWord -Force -ErrorAction Stop | Out-Null
    }
    if (-not (Test-SteamClientHardwareAcceleration)) {
        throw 'Steam hardware-accelerated web views could not be verified'
    }
    Write-Ok 'Steam CEF hardware acceleration enabled'
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

function Test-SteamScheduledTaskMatch($Task) {
    if (-not $Task) { return $false }
    return (($Task.TaskName -match '(?i)\bSteam\b' -or $Task.TaskPath -match '(?i)\\Steam\\') -and
        $Task.TaskName -notmatch '(?i)Steam(VR|Link|OS|Deck)' -and
        $Task.TaskPath -notmatch '(?i)Steam(VR|Link|OS|Deck)')
}

function Get-SteamScheduledTaskMatches {
    try {
        return @(Get-ScheduledTask -ErrorAction Stop | Where-Object { Test-SteamScheduledTaskMatch $_ })
    } catch {
        Write-Warn "Scheduled task inventory skipped: $($_.Exception.Message)"
        return @()
    }
}

function Get-SteamNotificationIds {
    return @(
        'Steam',
        'Valve.Steam',
        'Valve.Steam.Client',
        'com.valvesoftware.Steam',
        'steam.exe',
        'SteamClient'
    )
}

function New-SteamNotificationSnapshotEntry([string]$Id) {
    $base = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Notifications\Settings'
    $path = Join-Path $base $Id
    $keyExisted = Test-Path $path
    $enabled = if ($keyExisted) { Get-SteamRegistryValueSnapshot $path 'Enabled' } else { $null }
    $show = if ($keyExisted) { Get-SteamRegistryValueSnapshot $path 'ShowInActionCenter' } else { $null }
    return @{
        Id                         = $Id
        KeyExisted                 = $keyExisted
        EnabledExisted             = [bool]$enabled
        EnabledValue               = if ($enabled) { $enabled.Value } else { $null }
        EnabledKind                = if ($enabled) { $enabled.Kind } else { 'DWord' }
        ShowInActionCenterExisted  = [bool]$show
        ShowInActionCenterValue    = if ($show) { $show.Value } else { $null }
        ShowInActionCenterKind     = if ($show) { $show.Kind } else { 'DWord' }
    }
}

function Get-SteamNotificationSnapshot {
    $entries = [Collections.Generic.List[hashtable]]::new()
    $seen = @{}
    foreach ($id in (Get-SteamNotificationIds)) {
        $seen[$id.ToLowerInvariant()] = $true
        $entries.Add((New-SteamNotificationSnapshotEntry $id))
    }

    $base = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Notifications\Settings'
    if (Test-Path $base) {
        Get-ChildItem $base -ErrorAction SilentlyContinue |
            Where-Object { $_.PSChildName -match '(?i)steam' -and $_.PSChildName -notmatch '(?i)steam(vr|link|os|deck)' } |
            ForEach-Object {
                $id = [string]$_.PSChildName
                $key = $id.ToLowerInvariant()
                if ($seen.ContainsKey($key)) { return }
                $seen[$key] = $true
                $entries.Add((New-SteamNotificationSnapshotEntry $id))
            }
    }
    return @($entries)
}

function Test-SteamTrayExecutablePath([string]$Path, [string]$SteamPath) {
    if ([string]::IsNullOrWhiteSpace($Path)) { return $false }
    if ($Path -match '(?i)[\\/]steam\.exe$' -or $Path -match '(?i)\\Steam\\') { return $true }
    try {
        $prefix = [IO.Path]::GetFullPath($SteamPath).TrimEnd('\') + '\'
        $full = [IO.Path]::GetFullPath($Path)
        return $full.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)
    } catch {
        return $false
    }
}

function Get-SteamTraySnapshot([string]$SteamPath) {
    $entries = [Collections.Generic.List[hashtable]]::new()
    $notifyKey = 'HKCU:\Control Panel\NotifyIconSettings'
    if (-not (Test-Path $notifyKey)) { return @($entries) }
    foreach ($key in @(Get-ChildItem $notifyKey -ErrorAction SilentlyContinue)) {
        try {
            $item = Get-Item -Path $key.PSPath -ErrorAction Stop
            $exe = [string]$item.GetValue('ExecutablePath')
            if (-not (Test-SteamTrayExecutablePath $exe $SteamPath)) { continue }
            $hasPromoted = $item.GetValueNames() -contains 'IsPromoted'
            $entries.Add(@{
                Key               = $key.PSPath
                Name              = $key.PSChildName
                ExecutablePath    = $exe
                IsPromotedExisted = $hasPromoted
                IsPromotedValue   = if ($hasPromoted) { $item.GetValue('IsPromoted') } else { $null }
                IsPromotedKind    = if ($hasPromoted) { $item.GetValueKind('IsPromoted').ToString() } else { 'DWord' }
            })
        } catch { }
    }
    return @($entries)
}

function Get-SteamAppPathSnapshot {
    $key = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\App Paths\steam.exe'
    $keyExisted = Test-Path $key
    $default = if ($keyExisted) { Get-SteamRegistryValueSnapshot $key '(default)' } else { $null }
    $pathValue = if ($keyExisted) { Get-SteamRegistryValueSnapshot $key 'Path' } else { $null }
    return @{
        Key            = $key
        KeyExisted     = $keyExisted
        DefaultExisted = [bool]$default
        DefaultValue   = if ($default) { $default.Value } else { $null }
        DefaultKind    = if ($default) { $default.Kind } else { 'String' }
        PathExisted    = [bool]$pathValue
        PathValue      = if ($pathValue) { $pathValue.Value } else { $null }
        PathKind       = if ($pathValue) { $pathValue.Kind } else { 'String' }
    }
}

function Get-SteamWindowsRecoverySnapshot([string]$SteamPath) {
    $startup = Get-SteamWindowsStartupSnapshot
    $scheduledTasks = [Collections.Generic.List[hashtable]]::new()
    foreach ($task in @(Get-SteamScheduledTaskMatches)) {
        $scheduledTasks.Add(@{
            TaskName = [string]$task.TaskName
            TaskPath = [string]$task.TaskPath
            Enabled  = [bool]$task.Settings.Enabled
        })
    }

    return @{
        StartupEntries          = @($startup.StartupEntries)
        StartupModeCaptured     = [bool]$startup.StartupModeCaptured
        HadStartupMode          = [bool]$startup.HadStartupMode
        PreviousStartupMode     = $startup.PreviousStartupMode
        PreviousStartupModeKind = [string]$startup.PreviousStartupModeKind
        ScheduledTasks          = @($scheduledTasks)
        Notifications           = @(Get-SteamNotificationSnapshot)
        TrayEntries             = @(Get-SteamTraySnapshot $SteamPath)
        AppPath                 = Get-SteamAppPathSnapshot
        ClientPerformance       = @(Get-SteamClientPerformanceSnapshot)
    }
}

function Merge-SteamRecoveryItems($Prior, $Current, [string[]]$IdentityFields) {
    $result = [Collections.Generic.List[object]]::new()
    $seen = @{}
    foreach ($set in @($Prior, $Current)) {
        foreach ($item in @($set | Where-Object { $_ })) {
            $parts = foreach ($field in $IdentityFields) { [string](Get-SteamObjectProperty $item $field '') }
            $id = ($parts -join "`0").ToLowerInvariant()
            if ($seen.ContainsKey($id)) { continue }
            $seen[$id] = $true
            $result.Add($item)
        }
    }
    return @($result)
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
        ScheduledTasks          = @(Merge-SteamRecoveryItems (Get-SteamObjectProperty $Prior 'ScheduledTasks' @()) (Get-SteamObjectProperty $Current 'ScheduledTasks' @()) @('TaskPath', 'TaskName'))
        Notifications           = @(Merge-SteamRecoveryItems (Get-SteamObjectProperty $Prior 'Notifications' @()) (Get-SteamObjectProperty $Current 'Notifications' @()) @('Id'))
        TrayEntries             = @(Merge-SteamRecoveryItems (Get-SteamObjectProperty $Prior 'TrayEntries' @()) (Get-SteamObjectProperty $Current 'TrayEntries' @()) @('Key'))
        AppPath                 = if (Get-SteamObjectProperty $Prior 'AppPath' $null) { Get-SteamObjectProperty $Prior 'AppPath' $null } else { Get-SteamObjectProperty $Current 'AppPath' $null }
        ClientPerformance       = @(Merge-SteamRecoveryItems (Get-SteamObjectProperty $Prior 'ClientPerformance' @()) (Get-SteamObjectProperty $Current 'ClientPerformance' @()) @('Key', 'Name'))
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

function Disable-SteamScheduledTasks {
    try {
        $tasks = @(Get-SteamScheduledTaskMatches)
        foreach ($task in $tasks) {
            if (-not [bool]$task.Settings.Enabled) { continue }
            Disable-ScheduledTask -TaskName $task.TaskName -TaskPath $task.TaskPath -ErrorAction SilentlyContinue | Out-Null
            Write-Ok "Disabled scheduled task: $($task.TaskPath)$($task.TaskName)"
        }
    } catch {
        Write-Warn "Scheduled task cleanup skipped: $($_.Exception.Message)"
    }
}

function Set-SteamWindowsNotificationsOff {
    # Quiet Windows: disable Steam toast banners (in-client Steam alerts still work).
    Write-Step 'Disabling Windows notifications for Steam...'
    $base = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Notifications\Settings'
    if (-not (Test-Path $base)) { New-Item -Path $base -Force | Out-Null }

    $setOff = {
        param([string]$Id)
        $path = Join-Path $base $Id
        if (-not (Test-Path $path)) { New-Item -Path $path -Force | Out-Null }
        Set-ItemProperty -Path $path -Name 'Enabled' -Value 0 -Type DWord -Force
        Set-ItemProperty -Path $path -Name 'ShowInActionCenter' -Value 0 -Type DWord -Force -ErrorAction SilentlyContinue
    }

    foreach ($id in (Get-SteamNotificationIds)) {
        & $setOff $id
    }

    $n = 0
    Get-ChildItem $base -ErrorAction SilentlyContinue |
        Where-Object { $_.PSChildName -match '(?i)steam' -and $_.PSChildName -notmatch '(?i)steam(vr|link|os|deck)' } |
        ForEach-Object {
            Set-ItemProperty -Path $_.PSPath -Name 'Enabled' -Value 0 -Type DWord -Force
            Set-ItemProperty -Path $_.PSPath -Name 'ShowInActionCenter' -Value 0 -Type DWord -Force -ErrorAction SilentlyContinue
            $n++
            Write-Ok "Windows toasts off: $($_.PSChildName)"
        }
    if ($n -eq 0) { Write-Ok 'Windows Steam toast keys seeded' }
}

function Set-SteamTrayIconHidden([string]$SteamPath) {
    $notifyKey = 'HKCU:\Control Panel\NotifyIconSettings'
    if (-not (Test-Path $notifyKey)) { return }

    $hidden = 0
    Get-ChildItem $notifyKey -ErrorAction SilentlyContinue | ForEach-Object {
        $item = Get-Item -Path $_.PSPath -ErrorAction SilentlyContinue
        if (-not $item) { return }
        $path = [string]$item.GetValue('ExecutablePath')
        if (-not (Test-SteamTrayExecutablePath $path $SteamPath)) { return }
        Set-ItemProperty -Path $_.PSPath -Name 'IsPromoted' -Value 0 -Type DWord -Force
        $hidden++
    }

    if ($hidden -gt 0) { Write-Ok "Tray icon hidden ($hidden entries)" }
    else { Write-Warn 'Steam tray registry entry not found yet - launch once, then re-run' }
}

function Apply-SteamWindowsQuiet([string]$SteamPath) {
    Write-Step 'Applying Windows quiet shell (toasts, tray, tasks)...'
    Disable-SteamScheduledTasks
    Set-SteamWindowsNotificationsOff
    Set-SteamTrayIconHidden $SteamPath
    Write-Ok 'Windows quiet applied (toasts OFF, tray not promoted, no Steam scheduled tasks)'
}

function Test-SteamToastsOff {
    $base = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Notifications\Settings'
    $ids = @('Steam', 'Valve.Steam', 'Valve.Steam.Client', 'com.valvesoftware.Steam', 'steam.exe')
    $seen = $false
    foreach ($id in $ids) {
        $path = Join-Path $base $id
        if (-not (Test-Path -LiteralPath $path)) { continue }
        $seen = $true
        try {
            $entry = Get-ItemProperty -Path $path -ErrorAction Stop
            $prop = $entry.PSObject.Properties['Enabled']
            if (-not $prop -or [int]$prop.Value -ne 0) { return $false }
        } catch { return $false }
    }
    return $seen
}

function Test-SteamTrayQuiet([string]$SteamPath) {
    $notifyKey = 'HKCU:\Control Panel\NotifyIconSettings'
    if (-not (Test-Path $notifyKey)) { return $true }
    foreach ($key in @(Get-ChildItem -Path $notifyKey -ErrorAction SilentlyContinue)) {
        $item = Get-Item -Path $key.PSPath -ErrorAction SilentlyContinue
        if (-not $item) { continue }
        $exe = [string]$item.GetValue('ExecutablePath')
        if (-not (Test-SteamTrayExecutablePath $exe $SteamPath)) { continue }
        if ($item.GetValueNames() -notcontains 'IsPromoted' -or [int]$item.GetValue('IsPromoted') -ne 0) {
            return $false
        }
    }
    return $true
}

function Test-SteamScheduledTasksQuiet {
    try {
        foreach ($task in @(Get-SteamScheduledTaskMatches)) {
            if ([bool]$task.Settings.Enabled) { return $false }
        }
        return $true
    } catch { return $true }
}

function Test-SteamWindowsQuiet([string]$SteamPath) {
    return (Test-SteamWindowsStartupDisabled) -and
        (Test-SteamToastsOff) -and
        (Test-SteamTrayQuiet $SteamPath) -and
        (Test-SteamScheduledTasksQuiet)
}

function Clear-SteamDesktopShortcuts {
    $removed = 0
    foreach ($desktop in @(
        [Environment]::GetFolderPath('Desktop'),
        [Environment]::GetFolderPath('CommonDesktopDirectory')
    )) {
        if (-not $desktop -or -not (Test-Path -LiteralPath $desktop)) { continue }
        Get-ChildItem -LiteralPath $desktop -Filter 'Steam*.lnk' -Force -ErrorAction SilentlyContinue | ForEach-Object {
            try {
                Remove-Item -LiteralPath $_.FullName -Force -ErrorAction Stop
                $removed++
                Write-Ok "Removed desktop shortcut: $($_.Name)"
            } catch { }
        }
    }
    return $removed
}

function Invoke-SteamCompleteClientDebloat([string]$SteamPath) {
    # Discord-parity "complete client debloat": leftovers, disposable caches,
    # crashpads, desktop icons, orphaned Exo launchers. Never touch
    # installed games, userdata login, or active shader caches for installed apps.
    Write-Step 'Complete client debloat (leftovers, crashpads, disposable caches)...'
    [long]$freed = 0
    $actions = 0

    foreach ($f in @(
        (Join-Path $SteamPath 'Steam-Exo-Aggressive.cmd'),
        (Join-Path $SteamPath 'Steam-Exo-Lean.cmd'),
        (Join-Path $SteamPath 'Steam-Exo-Legacy.cmd')
    )) {
        if (Test-Path -LiteralPath $f) {
            Remove-Item -LiteralPath $f -Force -ErrorAction SilentlyContinue
            $actions++
            Write-Ok "Removed leftover launcher: $(Split-Path $f -Leaf)"
        }
    }

    $actions += [int](Clear-SteamDesktopShortcuts)

    foreach ($d in @(
        (Join-Path $env:LOCALAPPDATA 'Steam\htmlcache\Crashpad'),
        (Join-Path $env:LOCALAPPDATA 'Steam\Crashpad'),
        (Join-Path $SteamPath 'dumps'),
        (Join-Path $SteamPath 'logs')
    )) {
        if (Test-Path -LiteralPath $d) {
            $freed += Clear-PathTree $d
            $actions++
            Write-Ok "Cleared debloat path: $d"
        }
    }

    # Stale package bootstrap leftovers (safe; Steam re-downloads if needed)
    $package = Join-Path $SteamPath 'package'
    if (Test-Path -LiteralPath $package) {
        Get-ChildItem -LiteralPath $package -File -ErrorAction SilentlyContinue |
            Where-Object { $_.Extension -match '(?i)^\.(tmp|old|bak)$' -or $_.Name -match '(?i)\.(tmp|old|bak)$' } |
            ForEach-Object {
                try {
                    $freed += [long]$_.Length
                    Remove-Item -LiteralPath $_.FullName -Force -ErrorAction Stop
                    $actions++
                } catch { }
            }
    }

    # Root-level junk logs already handled in Clear-SteamSafeCaches; wipe known temp
    foreach ($pat in @('*.dmp', 'steam_log*.txt', 'bootstrap_log.txt')) {
        Get-ChildItem -LiteralPath $SteamPath -Filter $pat -File -ErrorAction SilentlyContinue | ForEach-Object {
            try {
                $freed += [long]$_.Length
                Remove-Item -LiteralPath $_.FullName -Force -ErrorAction Stop
                $actions++
            } catch { }
        }
    }

    Write-Ok ("Client debloat actions={0}, freed ~{1:N1} MB" -f $actions, ($freed / 1MB))
    return @{
        Freed   = $freed
        Actions = $actions
    }
}

function Test-SteamCompleteClientDebloat([string]$SteamPath) {
    if (-not $SteamPath -or -not (Test-Path -LiteralPath $SteamPath)) { return $false }

    foreach ($f in @(
        (Join-Path $SteamPath 'Steam-Exo-Aggressive.cmd'),
        (Join-Path $SteamPath 'Steam-Exo-Lean.cmd'),
        (Join-Path $SteamPath 'Steam-Exo-Legacy.cmd')
    )) {
        if (Test-Path -LiteralPath $f) { return $false }
    }

    foreach ($desktop in @(
        [Environment]::GetFolderPath('Desktop'),
        [Environment]::GetFolderPath('CommonDesktopDirectory')
    )) {
        if (-not $desktop -or -not (Test-Path -LiteralPath $desktop)) { continue }
        $hits = @(Get-ChildItem -LiteralPath $desktop -Filter 'Steam*.lnk' -Force -ErrorAction SilentlyContinue)
        if ($hits.Count -gt 0) { return $false }
    }

    foreach ($d in @(
        (Join-Path $env:LOCALAPPDATA 'Steam\htmlcache\Crashpad'),
        (Join-Path $env:LOCALAPPDATA 'Steam\Crashpad')
    )) {
        if (Test-Path -LiteralPath $d) {
            $kids = @(Get-ChildItem -LiteralPath $d -Force -ErrorAction SilentlyContinue)
            if ($kids.Count -gt 0) { return $false }
        }
    }

    # Must have the lean launcher present after a full apply
    if (-not (Test-Path -LiteralPath (Join-Path $SteamPath 'Steam-Exo.cmd'))) { return $false }
    return $true
}

function Test-SteamRuntimeIntegrity([string]$SteamPath) {
    if (-not $SteamPath) { return $false }
    if (-not (Test-Path -LiteralPath (Join-Path $SteamPath 'steam.exe'))) { return $false }
    if (-not (Test-Path -LiteralPath (Join-Path $SteamPath 'bin'))) { return $false }
    # Modern Steam ships steamwebhelper under bin\cef\cef.win*\steamwebhelper.exe
    $helpers = @(
        (Join-Path $SteamPath 'steamwebhelper.exe'),
        (Join-Path $SteamPath 'bin\cef\cef.win64\steamwebhelper.exe'),
        (Join-Path $SteamPath 'bin\cef\cef.win7x64\steamwebhelper.exe'),
        (Join-Path $SteamPath 'bin\cef\cef.win7\steamwebhelper.exe')
    )
    foreach ($h in $helpers) {
        if (Test-Path -LiteralPath $h) { return $true }
    }
    try {
        $found = Get-ChildItem -LiteralPath (Join-Path $SteamPath 'bin') -Filter 'steamwebhelper.exe' -Recurse -ErrorAction Stop |
            Select-Object -First 1
        return [bool]$found
    } catch { return $false }
}

function Test-SteamStartMenuLaunchPath([string]$SteamPath) {
    if (-not $SteamPath) { return $false }
    $cmdPath = Join-Path $SteamPath 'Steam-Exo.cmd'
    if (-not (Test-Path -LiteralPath $cmdPath)) { return $false }

    $candidates = @(
        (Join-Path ([Environment]::GetFolderPath('Programs')) 'Steam\Steam.lnk'),
        (Join-Path ([Environment]::GetFolderPath('CommonPrograms')) 'Steam\Steam.lnk'),
        (Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Steam\Steam.lnk'),
        (Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\Steam\Steam.lnk')
    )

    $wsh = $null
    try { $wsh = New-Object -ComObject WScript.Shell } catch { return $false }

    $found = $false
    foreach ($lnk in $candidates) {
        if (-not (Test-Path -LiteralPath $lnk)) { continue }
        try {
            $sc = $wsh.CreateShortcut($lnk)
            $target = [string]$sc.TargetPath
            if ($target -and (
                    $target -ieq $cmdPath -or
                    $target -match '(?i)Steam-Exo\.cmd$'
                )) {
                $found = $true
                break
            }
        } catch { }
    }
    return $found
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
    # Rewrite-existing-only. Used for keys whose modern section path is not
    # verified; missing keys are skipped, never invented at a guessed path.
    $pattern = '"' + [regex]::Escape($Key) + '"\s+"[^"]*"'
    $replacement = '"' + $Key + '"		"' + $Value + '"'
    if ($Raw -match $pattern) {
        return [regex]::Replace($Raw, $pattern, $replacement)
    }
    return $Raw
}

function Find-ExoVdfSection([string]$Raw, [int]$From, [int]$To, [string]$Name) {
    # Locate a "<Name>" { ... } section at the TOP level of the given range.
    # Returns @{ Open = index-after-open-brace; Close = index-of-close-brace }
    # or $null. Quote-aware and depth-aware so nested same-named sections in
    # deeper blocks are never matched by accident.
    $depth = 0
    $i = $From
    while ($i -lt $To) {
        $ch = $Raw[$i]
        if ($ch -eq '"') {
            $tokenStart = $i + 1
            $tokenEnd = $Raw.IndexOf('"', $tokenStart)
            if ($tokenEnd -lt 0 -or $tokenEnd -ge $To) { return $null }
            $token = $Raw.Substring($tokenStart, $tokenEnd - $tokenStart)
            $i = $tokenEnd + 1
            if ($depth -ne 0) { continue }
            $j = $i
            while ($j -lt $To -and [char]::IsWhiteSpace($Raw[$j])) { $j++ }
            if ($j -lt $To -and $Raw[$j] -eq '"') {
                # Key-value pair: skip the value token.
                $valueEnd = $Raw.IndexOf('"', $j + 1)
                if ($valueEnd -lt 0 -or $valueEnd -ge $To) { return $null }
                $i = $valueEnd + 1
                continue
            }
            if ($j -lt $To -and $Raw[$j] -eq '{' -and ($token -ieq $Name)) {
                $braceDepth = 0
                for ($k = $j; $k -lt $To; $k++) {
                    $c = $Raw[$k]
                    if ($c -eq '"') {
                        $k = $Raw.IndexOf('"', $k + 1)
                        if ($k -lt 0 -or $k -ge $To) { return $null }
                        continue
                    }
                    if ($c -eq '{') { $braceDepth++ }
                    elseif ($c -eq '}') {
                        $braceDepth--
                        if ($braceDepth -eq 0) { return @{ Open = ($j + 1); Close = $k } }
                    }
                }
                return $null
            }
            continue
        }
        if ($ch -eq '{') { $depth++ }
        elseif ($ch -eq '}') {
            $depth--
            if ($depth -lt 0) { return $null }
        }
        $i++
    }
    return $null
}

function Set-SteamVdfKeyAtPath([string]$Raw, [string[]]$SectionPath, [string]$Key, [string]$Value) {
    # VDF-aware injector: rewrite the key when present anywhere; otherwise INSERT
    # it at the exact section path (creating intermediate sections as needed)
    # with tab indentation matching Valve's own writer. Callers back up the file
    # (.exo-bak) before persisting the result.
    $pattern = '"' + [regex]::Escape($Key) + '"\s+"[^"]*"'
    if ($Raw -match $pattern) {
        return [regex]::Replace($Raw, $pattern, ('"' + $Key + '"' + "`t`t" + '"' + $Value + '"'))
    }

    $nl = "`n"
    if ($Raw -match "`r`n") { $nl = "`r`n" }

    $from = 0
    $to = $Raw.Length
    $depth = 0
    foreach ($name in $SectionPath) {
        $section = Find-ExoVdfSection $Raw $from $to $name
        if ($null -eq $section) {
            $indent = "`t" * $depth
            if ($depth -eq 0) {
                # Missing root (empty/new file): create it at the top.
                $block = '"' + $name + '"' + $nl + '{' + $nl + '}' + $nl
                $Raw = $Raw.Insert(0, $block)
            } else {
                $block = $nl + $indent + '"' + $name + '"' + $nl + $indent + '{' + $nl + $indent + '}'
                $Raw = $Raw.Insert($from, $block)
                $to = $to + $block.Length
            }
            if ($depth -eq 0) { $to = $Raw.Length }
            $section = Find-ExoVdfSection $Raw $from $to $name
            if ($null -eq $section) { return $Raw }
        }
        $from = [int]$section.Open
        $to = [int]$section.Close
        $depth++
    }

    $keyIndent = "`t" * $depth
    $line = $nl + $keyIndent + '"' + $Key + '"' + "`t`t" + '"' + $Value + '"'
    return $Raw.Insert($from, $line)
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
    if (-not $steamPath) {
        return @{ Gpu = $false; Patched = $false; Path = $null; Snappy = $false; Verified = $false; Skipped = $false; Present = $false; Reason = 'Steam path was not found' }
    }

    $userdata = Join-Path $steamPath 'userdata'
    if (-not (Test-Path $userdata)) {
        Write-Warn 'No userdata yet - open Steam once, then Reapply for deeper client tweaks'
        return @{ Gpu = $false; Patched = $false; Path = $null; Snappy = $false; Verified = $true; Skipped = $true; Present = $false; Reason = 'userdata not present yet' }
    }

    $files = @(Get-ChildItem -LiteralPath $userdata -Directory -ErrorAction SilentlyContinue |
        ForEach-Object {
            $p = Join-Path $_.FullName 'config\localconfig.vdf'
            if (Test-Path $p) { Get-Item $p }
        } | Sort-Object LastWriteTime -Descending)

    if ($files.Count -eq 0) {
        Write-Warn 'No localconfig.vdf yet - open Steam once, then Reapply'
        return @{ Gpu = $false; Patched = $false; Path = $null; Snappy = $false; Verified = $true; Skipped = $true; Present = $false; Reason = 'localconfig.vdf not present yet' }
    }

    # Patch all accounts on this PC (universal multi-user machine)
    $anyGpu = $false
    $anySnappy = $false
    $anyPatched = $false
    $lastPath = $null
    $verificationOk = $true
    $verificationObserved = 0

    # INJECTED keys: section path verified against public localconfig.vdf
    # documentation (l3laze/Steam-Data + Valve VDF dumps). Missing keys are
    # inserted at the exact path; present keys are rewritten in place.
    $injectSet = @(
        # Library quiet / low-churn (documented direct UserLocalConfigStore keys)
        @{ Path = @('UserLocalConfigStore'); K = 'LibraryLowBandwidthMode'; V = '1'; Kind = 'lean' },
        @{ Path = @('UserLocalConfigStore'); K = 'LibraryLowPerfMode'; V = '1'; Kind = 'lean' },
        @{ Path = @('UserLocalConfigStore'); K = 'LibraryDisableCommunityContent'; V = '1'; Kind = 'snappy' },
        # Friends notification set fully quieted (UserLocalConfigStore\friends)
        @{ Path = @('UserLocalConfigStore', 'friends'); K = 'PersonaStateDesired'; V = '1'; Kind = 'snappy' },
        @{ Path = @('UserLocalConfigStore', 'friends'); K = 'Notifications_ShowIngame'; V = '0'; Kind = 'quiet' },
        @{ Path = @('UserLocalConfigStore', 'friends'); K = 'Notifications_ShowOnline'; V = '0'; Kind = 'quiet' },
        @{ Path = @('UserLocalConfigStore', 'friends'); K = 'Notifications_ShowMessage'; V = '0'; Kind = 'quiet' },
        @{ Path = @('UserLocalConfigStore', 'friends'); K = 'Notifications_EventsAndAnnouncements'; V = '0'; Kind = 'quiet' },
        @{ Path = @('UserLocalConfigStore', 'friends'); K = 'Sounds_PlayIngame'; V = '0'; Kind = 'quiet' },
        @{ Path = @('UserLocalConfigStore', 'friends'); K = 'Sounds_PlayOnline'; V = '0'; Kind = 'quiet' },
        @{ Path = @('UserLocalConfigStore', 'friends'); K = 'Sounds_PlayMessage'; V = '0'; Kind = 'quiet' },
        @{ Path = @('UserLocalConfigStore', 'friends'); K = 'Sounds_EventsAndAnnouncements'; V = '0'; Kind = 'quiet' },
        # Interface noise (UserLocalConfigStore\News)
        @{ Path = @('UserLocalConfigStore', 'News'); K = 'NotifyAvailableGames'; V = '0'; Kind = 'quiet' },
        # Overlay: keep ON, quiet the hitch sources (UserLocalConfigStore\system)
        @{ Path = @('UserLocalConfigStore', 'system'); K = 'EnableGameOverlay'; V = '1'; Kind = 'snappy' },
        @{ Path = @('UserLocalConfigStore', 'system'); K = 'InGameOverlayScreenshotNotification'; V = '0'; Kind = 'snappy' },
        @{ Path = @('UserLocalConfigStore', 'system'); K = 'InGameOverlayScreenshotPlaySound'; V = '0'; Kind = 'quiet' }
    )

    # REWRITE-ONLY keys: real on older clients but the modern section path is
    # not verifiable, so they are updated only when Steam itself wrote them.
    $rewriteGpu = @('H264HWAccel', 'GPUAccelWebViews', 'GPUAccelWebViews2', 'GPUAccelWebViewsD3D11')
    $rewriteSnappy = @(
        @{ K = 'SmoothScrollWebViews'; V = '0' },
        @{ K = 'StartupMovieMode'; V = '0' },
        @{ K = 'LibraryDisplayIconInGameList'; V = '0' },
        @{ K = 'InGameOverlayShowFPSCounterHotKey'; V = '0' },
        @{ K = 'SteamInputConfigEnabled'; V = '1' },
        @{ K = 'Controller_EnableChrome'; V = '0' },
        @{ K = 'BigPictureInForeground'; V = '0' },
        @{ K = 'MusicPlayerEnabled'; V = '0' },
        @{ K = 'FriendsUI'; V = '0' },
        @{ K = 'LibraryDisableFriendsActivity'; V = '1' },
        @{ K = 'ShaderPreCacheAllowed'; V = '1' },
        @{ K = 'ShaderPreCacheProgress'; V = '0' },
        @{ K = 'AutoUpdateWindowEnabled'; V = '0' }
    )
    $rewriteQuiet = @(
        @{ K = 'SoundPlay_DownloadComplete'; V = '0' },
        @{ K = 'SoundPlay_FriendOnline'; V = '0' },
        @{ K = 'FriendsAlwaysShowAvatars'; V = '0' },
        @{ K = 'AllowDownloadsDuringGameplay'; V = '0' },
        @{ K = 'CloudEnabled'; V = '1' }
    )

    $expectations = @()
    foreach ($entry in $injectSet) { $expectations += @{ K = $entry.K; V = $entry.V } }
    foreach ($k in $rewriteGpu) { $expectations += @{ K = $k; V = '1' } }
    foreach ($pair in ($rewriteSnappy + $rewriteQuiet)) { $expectations += @{ K = $pair.K; V = $pair.V } }

    foreach ($file in $files) {
        try {
            attrib -R $file.FullName 2>$null
            $raw = [IO.File]::ReadAllText($file.FullName)
            $orig = $raw

            # Keep modern CEF on hardware acceleration. Forcing software rendering creates
            # a SwiftShader GPU process and can inflate the main renderer working set.
            foreach ($k in $rewriteGpu) {
                $before = $raw
                $raw = Set-SteamVdfKey $raw $k '1'
                if ($raw -ne $before) { $anyGpu = $true }
            }

            # VDF-aware injection at verified section paths (insert when missing)
            foreach ($entry in $injectSet) {
                $before = $raw
                $raw = Set-SteamVdfKeyAtPath $raw ([string[]]$entry.Path) ([string]$entry.K) ([string]$entry.V)
                if ($raw -ne $before -and [string]$entry.Kind -in @('snappy','lean')) { $anySnappy = $true }
            }

            # Rewrite-existing-only extras (older client keys)
            foreach ($pair in $rewriteSnappy) {
                $before = $raw
                $raw = Set-SteamVdfKey $raw $pair.K $pair.V
                if ($raw -ne $before) { $anySnappy = $true }
            }
            foreach ($pair in $rewriteQuiet) {
                $raw = Set-SteamVdfKey $raw $pair.K $pair.V
            }

            if ($raw -ne $orig) {
                $bak = $file.FullName + '.exo-bak'
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
        # All target keys already at target values (injection guarantees presence).
        Write-Ok 'localconfig.vdf: target keys already present at target values'
    }

    return @{
        Gpu     = $anyGpu
        Patched = $anyPatched
        Path    = $lastPath
        Snappy  = $anySnappy
        # Missing first-run files soft-pass above. Existing files must verify.
        Verified = $verificationOk
        Observed = $verificationObserved
        Skipped  = $false
        Present  = $true
        Reason   = ''
    }
}

function Set-SteamFastLoginHints([string]$SteamPath) {
    # Single-account machines still show a "loading profile" path if MostRecent is unset.
    # Mark the only / most recent saved user so Steam auto-continues.
    $path = Join-Path $SteamPath 'config\loginusers.vdf'
    if (-not (Test-Path -LiteralPath $path)) {
        Write-Ok 'loginusers.vdf not present yet (open Steam once) - skip profile hint'
        return $false
    }
    try {
        attrib -R $path 2>$null
        $raw = [IO.File]::ReadAllText($path)
        $orig = $raw
        # Ensure AutoLogin / RememberPassword / MostRecent for every listed account block.
        foreach ($pair in @(
            @{ K = 'RememberPassword'; V = '1' },
            @{ K = 'AutoLogin'; V = '1' },
            @{ K = 'WantsOfflineMode'; V = '0' }
        )) {
            $raw = Set-SteamVdfKey $raw $pair.K $pair.V
        }
        # Inject MostRecent if missing (Set-SteamVdfKey only rewrites existing keys).
        if ($raw -notmatch '"MostRecent"') {
            if ($raw -match '"AutoLogin"\s+"[^"]*"') {
                $raw = [regex]::Replace($raw, '("AutoLogin"\s+"[^"]*")', "`$1`r`n`"MostRecent`"`t`t`"1`"", 1)
            } elseif ($raw -match '"RememberPassword"\s+"[^"]*"') {
                $raw = [regex]::Replace($raw, '("RememberPassword"\s+"[^"]*")', "`$1`r`n`"MostRecent`"`t`t`"1`"", 1)
            }
        } else {
            $raw = Set-SteamVdfKey $raw 'MostRecent' '1'
        }
        $userBlocks = [regex]::Matches($raw, '"\d{17}"')
        if ($userBlocks.Count -gt 1) {
            Write-Ok "loginusers.vdf has $($userBlocks.Count) accounts - stamped AutoLogin/RememberPassword"
        }
        if ($raw -ne $orig) {
            $bak = $path + '.exo-bak'
            if (-not (Test-Path $bak)) { Copy-Item $path $bak -Force }
            [IO.File]::WriteAllText($path, $raw, [Text.UTF8Encoding]::new($false))
            Write-Ok 'loginusers.vdf: auto-login / most-recent profile hints applied'
            return $true
        }
        Write-Ok 'loginusers.vdf: profile hints already set'
        return $true
    } catch {
        Write-Warn "loginusers.vdf: $($_.Exception.Message)"
        return $false
    }
}

function Merge-SteamCfgKey([string]$Raw, [string]$Key, [string]$Value) {
    $lines = [Collections.Generic.List[string]]::new()
    if (-not [string]::IsNullOrEmpty($Raw)) {
        foreach ($line in ($Raw -split "\r?\n")) { [void]$lines.Add($line) }
        while ($lines.Count -gt 0 -and [string]::IsNullOrWhiteSpace($lines[$lines.Count - 1])) {
            $lines.RemoveAt($lines.Count - 1)
        }
    }

    $found = $false
    $pattern = '^\s*' + [regex]::Escape($Key) + '\s*='
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -match $pattern) {
            $lines[$i] = "$Key=$Value"
            $found = $true
        }
    }
    if (-not $found) { [void]$lines.Add("$Key=$Value") }
    return ($lines -join "`r`n")
}

function Set-SteamBootstrapFastStart([string]$SteamPath) {
    # steam.cfg next to steam.exe stops the bootstrapper "Checking for updates" pause
    # on every launch. Client can still update via Steam > Check for Steam Client Updates.
    $cfg = Join-Path $SteamPath 'steam.cfg'
    try {
        $existing = if (Test-Path -LiteralPath $cfg) {
            [IO.File]::ReadAllText($cfg)
        } else { '' }
        $merged = Merge-SteamCfgKey $existing 'BootStrapperInhibitAll' 'enable'
        $merged = Merge-SteamCfgKey $merged 'BootStrapperForceSelfUpdate' 'disable'
        if (($merged.TrimEnd() + "`r`n") -eq ($existing.TrimEnd() + "`r`n")) {
            Write-Ok 'steam.cfg: bootstrap update check already inhibited'
            return $true
        }
        if ((Test-Path -LiteralPath $cfg) -and -not (Test-Path -LiteralPath ($cfg + '.exo-bak'))) {
            Copy-Item -LiteralPath $cfg -Destination ($cfg + '.exo-bak') -Force
        }
        [IO.File]::WriteAllText($cfg, $merged.TrimEnd() + "`r`n", [Text.UTF8Encoding]::new($false))
        Write-Ok 'steam.cfg: skip bootstrap update check on launch (manual client update still available)'
        return $true
    } catch {
        Write-Warn "steam.cfg: $($_.Exception.Message)"
        return $false
    }
}

function Set-SteamLibraryConfigHints([string]$SteamPath) {
    $config = Join-Path $SteamPath 'config\config.vdf'
    if (-not (Test-Path -LiteralPath $config)) {
        Write-Warn 'config.vdf not found - open Steam once, then Reapply for download config tweaks'
        return @{ Verified = $true; Skipped = $true; Present = $false; Touched = $false; Reason = 'config.vdf not present yet' }
    }
    try {
        attrib -R $config 2>$null
        $raw = [IO.File]::ReadAllText($config)
        $orig = $raw

        # Verified config.vdf keys (InstallConfigStore\Software\Valve\Steam per
        # public Steam-Data docs): inject when missing, rewrite when present.
        $steamSection = @('InstallConfigStore', 'Software', 'Valve', 'Steam')
        foreach ($pair in @(
            @{ K = 'DownloadThrottleKbps'; V = '0' },
            @{ K = 'AllowDownloadsDuringGameplay'; V = '0' },
            @{ K = 'AutoUpdateWindowEnabled'; V = '0' }
        )) {
            $raw = Set-SteamVdfKeyAtPath $raw $steamSection $pair.K $pair.V
        }

        # Unverified section path - rewrite-existing-only (never invented)
        foreach ($pair in @(
            @{ K = 'ThrottleKbps'; V = '0' },
            @{ K = 'RateLimitBps'; V = '0' },
            @{ K = 'MaxSimDownloads'; V = '8' }
        )) {
            $raw = Set-SteamVdfKey $raw $pair.K $pair.V
        }

        $verification = Test-SteamVdfExpectations $raw @(
            @{ K = 'DownloadThrottleKbps'; V = '0' },
            @{ K = 'AllowDownloadsDuringGameplay'; V = '0' },
            @{ K = 'ThrottleKbps'; V = '0' },
            @{ K = 'RateLimitBps'; V = '0' },
            @{ K = 'MaxSimDownloads'; V = '8' },
            @{ K = 'AutoUpdateWindowEnabled'; V = '0' }
        )
        if (-not $verification.Valid) {
            Write-Warn 'config.vdf verification found a conflicting download value'
            return @{ Verified = $false; Skipped = $false; Present = $true; Touched = $false; Reason = 'config.vdf verification found a conflicting download value' }
        }

        if ($raw -ne $orig) {
            $bak = $config + '.exo-bak'
            if (-not (Test-Path $bak)) { Copy-Item $config $bak -Force }
            [IO.File]::WriteAllText($config, $raw, [Text.UTF8Encoding]::new($false))
            Write-Ok 'config.vdf: download throttle off / snappier download settings'
            return @{ Verified = $true; Skipped = $false; Present = $true; Touched = $true; Reason = '' }
        }
        Write-Ok 'config.vdf: no download keys to patch (Steam UI rate limit still available in Settings)'
        return @{ Verified = $true; Skipped = $false; Present = $true; Touched = $false; Reason = '' }
    } catch {
        Write-Warn "config.vdf: $($_.Exception.Message)"
        return @{ Verified = $false; Skipped = $false; Present = $true; Touched = $false; Reason = $_.Exception.Message }
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
    $ps = Get-ExoPwsh
    # Percent signs are expanded while a .cmd file is parsed.
    $cmdSteamPath = $SteamPath.Replace('%', '%%')
    $cmdExe = $exe.Replace('%', '%%')
    $cmdHelper = $HelperPath.Replace('%', '%%')
    $cmdPs = $ps.Replace('%', '%%')
    # Start Steam first (HIGH) so the UI appears ASAP; kick the contention guard right
    # after without waiting for it. Helper self-limits with a mutex.
    # Helpers are hosted by stable PowerShell 7 (resolved via Get-ExoPwsh).
    $cmd = @(
        '@echo off'
        ("rem Exo {0} - fast quiet CEF + in-game contention guard (PowerShell 7)" -f $Label)
        ('start "" /HIGH /D "{0}" "{1}" {2} %*' -f $cmdSteamPath, $cmdExe, $args)
        ('start "" /MIN "{0}" -NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File "{1}"' -f $cmdPs, $cmdHelper)
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

function Test-SteamTaskbarPinnedShortcut([string]$LnkPath) {
    if ([string]::IsNullOrWhiteSpace($LnkPath)) { return $false }
    return (($LnkPath -replace '/', '\') -match '(?i)\\User Pinned\\TaskBar\\')
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
        if ($target -match '(?i)Steam-Exo(\.cmd|-Aggressive\.cmd)$') { return $true }
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

function Set-SteamShortcutStockTarget([string]$LnkPath, [string]$SteamPath, [string]$SteamExe, $Wsh) {
    $sc = $Wsh.CreateShortcut($LnkPath)
    $existingArguments = [string]$sc.Arguments
    $sc.TargetPath = $SteamExe
    $sc.Arguments = $existingArguments
    $sc.WorkingDirectory = $SteamPath
    $sc.IconLocation = "$SteamExe,0"
    $sc.WindowStyle = 1
    $sc.Description = 'Steam'
    $sc.Save()
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
    # Single default launcher (full CEF quiet flags). Patch Start Menu only.
    # Taskbar pins stay pointed at steam.exe because Windows pinning can reject
    # command targets. Never create Desktop shortcuts.
    $exe = Join-Path $SteamPath 'steam.exe'
    $cmdPath = Join-Path $SteamPath 'Steam-Exo.cmd'
    Write-SteamLaunchCmd $cmdPath $SteamPath $HelperPath $Script:DefaultCefArgs 'default CEF'
    Write-Ok "Steam launcher: $cmdPath"
    Write-Ok ("CEF flags: {0}" -f ($Script:DefaultCefArgs -join ' '))

    # Remove old optional aggressive launcher if present
    $oldAgg = Join-Path $SteamPath 'Steam-Exo-Aggressive.cmd'
    if (Test-Path -LiteralPath $oldAgg) {
        Remove-Item -LiteralPath $oldAgg -Force -ErrorAction SilentlyContinue
        Write-Ok 'Removed old Steam-Exo-Aggressive.cmd'
    }

    $wsh = New-Object -ComObject WScript.Shell
    $patched = 0
    $seen = @{}
    $desc = 'Steam (Exo - quiet CEF + in-game contention guard)'

    foreach ($root in (Get-SteamShortcutSearchRoots)) {
        $lnks = @(Get-ChildItem -LiteralPath $root -Filter '*.lnk' -Recurse -Force -ErrorAction SilentlyContinue)
        foreach ($lnk in $lnks) {
            $key = $lnk.FullName.ToLowerInvariant()
            if ($seen.ContainsKey($key)) { continue }
            if (-not (Test-LnkIsSteamClient $lnk.FullName $exe $wsh)) { continue }
            if (Test-SteamTaskbarPinnedShortcut $lnk.FullName) {
                try {
                    $sc = $wsh.CreateShortcut($lnk.FullName)
                    if ([string]$sc.TargetPath -match '(?i)Steam-Exo|Exo') {
                        Set-SteamShortcutStockTarget $lnk.FullName $SteamPath $exe $wsh
                        Write-Ok ("Taskbar pin kept on steam.exe: {0}" -f $lnk.Name)
                    } else {
                        Write-Ok ("Skipped taskbar pin (kept steam.exe): {0}" -f $lnk.Name)
                    }
                } catch {
                    Write-Warn "Taskbar pin skip $($lnk.Name): $($_.Exception.Message)"
                }
                $seen[$key] = $true
                continue
            }
            # Never create/keep Exo-branded desktop entries - remove if found
            $onDesktop = $lnk.FullName -match '(?i)[\\/]Desktop[\\/]'
            if ($onDesktop -and $lnk.Name -match '(?i)Exo') {
                try {
                    Remove-Item -LiteralPath $lnk.FullName -Force -ErrorAction SilentlyContinue
                    Write-Ok ("Removed Exo desktop shortcut: {0}" -f $lnk.Name)
                } catch { }
                $seen[$key] = $true
                continue
            }
            try {
                Set-SteamShortcutTarget $lnk.FullName $cmdPath $SteamPath $exe $desc $wsh
                $seen[$key] = $true
                $patched++
                Write-Ok ("Shortcut -> Exo launcher: {0}" -f $lnk.FullName.Replace($env:USERPROFILE, '~').Replace($env:ProgramData, '%ProgramData%'))
            } catch {
                # ProgramData / all-users Start Menu often needs elevation; user Start Menu is enough.
                if ($lnk.FullName -match '(?i)\\ProgramData\\') {
                    Write-Ok ("Skipped all-users shortcut (needs admin): {0}" -f $lnk.Name)
                } else {
                    Write-Warn "Shortcut skip $($lnk.Name): $($_.Exception.Message)"
                }
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

            $aggLnk = Join-Path $dir 'Steam (Exo Aggressive).lnk'
            if (Test-Path -LiteralPath $aggLnk) {
                Remove-Item -LiteralPath $aggLnk -Force -ErrorAction SilentlyContinue
                Write-Ok 'Removed Start Menu Aggressive shortcut (now default launcher)'
            }
        } catch {
            Write-Warn "Start Menu Steam folder: $($_.Exception.Message)"
        }
    }

    # Never leave Steam / Exo icons on the Desktop (user or public).
    foreach ($desktop in @(
        [Environment]::GetFolderPath('Desktop'),
        [Environment]::GetFolderPath('CommonDesktopDirectory')
    )) {
        if (-not $desktop -or -not (Test-Path -LiteralPath $desktop)) { continue }
        foreach ($name in @(
            'Steam.lnk', 'Steam (Exo Lean).lnk', 'Steam (Exo Aggressive).lnk', 'Steam (Exo).lnk'
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

    Write-Ok "Updated $patched Steam shortcut(s) (Start Menu; taskbar pins kept on steam.exe; no desktop icons created)"
    return @{
        Cmd       = $cmdPath
        Args      = ($Script:DefaultCefArgs -join ' ')
        Shortcuts = $patched
    }
}

function Set-SteamGpuHighPerformance([string]$SteamPath) {
    # Prefer discrete GPU for steam.exe + steamwebhelper when multi-GPU (laptop dGPU / multi-adapter).
    $targets = @(
        (Join-Path $SteamPath 'steam.exe'),
        (Join-Path $SteamPath 'bin\cef\cef.win64\steamwebhelper.exe'),
        (Join-Path $SteamPath 'steamwebhelper.exe')
    )
    $hasDgpu = $false
    try {
        foreach ($n in @(Get-CimInstance Win32_VideoController -EA SilentlyContinue | ForEach-Object { [string]$_.Name })) {
            if ($n -match '(?i)NVIDIA|GeForce|RTX|GTX|Radeon|RX\s*\d|Arc\s*A' -and $n -notmatch '(?i)Microsoft Basic|Hyper-V|Remote') {
                $hasDgpu = $true; break
            }
        }
    } catch { }
    $key = 'HKCU:\Software\Microsoft\DirectX\UserGpuPreferences'
    try {
        if (-not (Test-Path $key)) { New-Item -Path $key -Force | Out-Null }
        foreach ($exe in $targets) {
            if (-not (Test-Path -LiteralPath $exe)) { continue }
            if ($hasDgpu) {
                New-ItemProperty -LiteralPath $key -Name $exe -Value 'GpuPreference=2;' -PropertyType String -Force -EA SilentlyContinue | Out-Null
            } else {
                Remove-ItemProperty -LiteralPath $key -Name $exe -Force -EA SilentlyContinue
            }
        }
        if ($hasDgpu) { Write-Ok 'Steam GPU preference = High performance (discrete GPU)' }
        else { Write-Ok 'Steam GPU preference = Auto (no discrete GPU)' }
    } catch {
        Write-Warn "Steam GPU preference: $($_.Exception.Message)"
    }
}

function Install-WebHelperMemoryGuard([string]$SteamPath) {
    # One reversible companion. Background CEF pages receive low Windows memory
    # priority so the OS reclaims them first under pressure; the foreground Steam UI
    # returns to normal. This avoids EmptyWorkingSet, hard caps, suspension, and kills.
    $helper = Join-Path $SteamPath 'Exo-SteamMemoryGuard.ps1'
    $body = @'
# Exo - Steam memory + contention guard. No EmptyWorkingSet, hard cap, suspend, or kill.
$ErrorActionPreference = 'SilentlyContinue'
$created = $false
$mutex = [Threading.Mutex]::new($true, 'Local\Exo.SteamMemoryGuard', [ref]$created)
if (-not $created) { $mutex.Dispose(); exit 0 }

Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public static class ExoSteamMemory {
  [StructLayout(LayoutKind.Sequential)] struct MEMORY_PRIORITY_INFORMATION { public uint MemoryPriority; }
  [DllImport("kernel32.dll", SetLastError=true)] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
  [DllImport("kernel32.dll", SetLastError=true)] static extern bool SetProcessInformation(IntPtr process, int infoClass, ref MEMORY_PRIORITY_INFORMATION info, uint size);
  [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
  [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
  public static int ForegroundPid() { uint pid; GetWindowThreadProcessId(GetForegroundWindow(), out pid); return (int)pid; }
  public static bool SetMemoryPriority(int pid, uint priority) {
    IntPtr handle = OpenProcess(0x0200u | 0x1000u, false, pid);
    if (handle == IntPtr.Zero) return false;
    try { var info = new MEMORY_PRIORITY_INFORMATION { MemoryPriority = priority }; return SetProcessInformation(handle, 0, ref info, 4); }
    finally { CloseHandle(handle); }
  }
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

function Set-SteamClientPriority([bool]$InGame) {
  # Foreground Steam stays fully responsive. Background renderers get low memory
  # priority (not a destructive trim); Windows may reclaim their pages first.
  $foregroundPid = [ExoSteamMemory]::ForegroundPid()
  $steamCls = if ($InGame) {
    [System.Diagnostics.ProcessPriorityClass]::BelowNormal
  } else {
    [System.Diagnostics.ProcessPriorityClass]::Normal
  }
  $webCls = if ($InGame) {
    [System.Diagnostics.ProcessPriorityClass]::BelowNormal
  } else {
    [System.Diagnostics.ProcessPriorityClass]::Normal
  }
  Get-Process -Name 'steam' -ErrorAction SilentlyContinue | ForEach-Object {
    try { if ($_.PriorityClass -ne $steamCls) { $_.PriorityClass = $steamCls } } catch {}
  }
  Get-Process -Name 'steamwebhelper' -ErrorAction SilentlyContinue | ForEach-Object {
    try {
      if ($_.PriorityClass -ne $webCls) {
        $_.PriorityClass = $webCls
      }
      $memoryPriority = if ($InGame) { 1 } elseif ($_.Id -eq $foregroundPid) { 5 } else { 2 }
      [void][ExoSteamMemory]::SetMemoryPriority($_.Id, [uint32]$memoryPriority)
    } catch {}
  }
}

function Reinstate-SteamQuiet {
  try {
    $steamKey = 'HKCU:\Software\Valve\Steam'
    if (-not (Test-Path $steamKey)) { New-Item -Path $steamKey -Force | Out-Null }
    New-ItemProperty -Path $steamKey -Name 'StartupMode' -PropertyType DWord -Value 0 -Force | Out-Null
  } catch {}
  foreach ($key in @(
    'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run',
    'HKLM:\Software\Microsoft\Windows\CurrentVersion\Run',
    'HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run'
  )) {
    if (-not (Test-Path $key)) { continue }
    try {
      $item = Get-Item -Path $key -ErrorAction Stop
      foreach ($name in @($item.GetValueNames())) {
        $val = [string]$item.GetValue($name)
        if ($val -match '(?i)steam\.exe' -or $name -match '(?i)^steam') {
          Remove-ItemProperty -Path $key -Name $name -Force -ErrorAction SilentlyContinue
        }
      }
    } catch {}
  }
}

try {
  $startupDeadline = (Get-Date).AddSeconds(30)
  while (-not (Get-Process steam -ErrorAction SilentlyContinue) -and (Get-Date) -lt $startupDeadline) {
    Start-Sleep -Milliseconds 250
  }
  Reinstate-SteamQuiet
  $ticks = 0
  while (Get-Process steam -ErrorAction SilentlyContinue) {
    Set-SteamClientPriority -InGame:(Test-SteamGameRunning)
    $ticks++
    if (($ticks % 12) -eq 0) { Reinstate-SteamQuiet }
    Start-Sleep -Seconds 5
  }
} finally {
  try { $mutex.ReleaseMutex() } catch {}
  $mutex.Dispose()
}
'@
    [IO.File]::WriteAllText($helper, $body, [Text.UTF8Encoding]::new($false))
    $oldHelper = Join-Path $SteamPath 'Exo-SteamWebHelperTrim.ps1'
    if (Test-Path -LiteralPath $oldHelper) { Remove-Item -LiteralPath $oldHelper -Force -ErrorAction SilentlyContinue }
    Write-Ok 'Steam memory guard installed (foreground responsive; background CEF reclaimed first under pressure)'
    return $helper
}


function Remove-SteamCfgExoKeys([string]$SteamCfg) {
    if (-not (Test-Path -LiteralPath $SteamCfg)) { return $false }
    $txt = [IO.File]::ReadAllText($SteamCfg)
    $lines = [Collections.Generic.List[string]]::new()
    $removed = $false
    foreach ($line in ($txt -split "\r?\n")) {
        if ($line -match '^\s*BootStrapper(InhibitAll|ForceSelfUpdate)\s*=') {
            $removed = $true
            continue
        }
        [void]$lines.Add($line)
    }
    if (-not $removed) { return $false }
    while ($lines.Count -gt 0 -and [string]::IsNullOrWhiteSpace($lines[$lines.Count - 1])) {
        $lines.RemoveAt($lines.Count - 1)
    }
    if ($lines.Count -eq 0) {
        Remove-Item -LiteralPath $SteamCfg -Force -ErrorAction Stop
    } else {
        [IO.File]::WriteAllText($SteamCfg, (($lines -join "`r`n").TrimEnd() + "`r`n"), [Text.UTF8Encoding]::new($false))
    }
    return $true
}

function Restore-SteamOptionalRegistryValue([string]$Key, [string]$Name, [bool]$Existed, $Value, [string]$Kind) {
    if ($Existed) {
        Restore-SteamRegistryValue @{
            Key = $Key; Name = $Name; Value = $Value; Kind = $Kind
        }
    } elseif (Test-Path $Key) {
        Remove-ItemProperty -Path $Key -Name $Name -Force -ErrorAction SilentlyContinue
        if ((Get-Item -Path $Key -ErrorAction Stop).GetValueNames() -contains $Name) {
            throw "$Name value is still present"
        }
    }
}

function Restore-SteamWindowsIntegration([string]$SteamPath, $Recovery, [ref]$Failures) {
    Write-Step 'Repair: restoring captured Steam Windows integration...'
    if (-not $Recovery) {
        Write-Warn 'No Steam Windows recovery snapshot exists (older optimizer state); re-enabling matching scheduled tasks only'
        foreach ($task in @(Get-SteamScheduledTaskMatches)) {
            try {
                Enable-ScheduledTask -TaskName $task.TaskName -TaskPath $task.TaskPath -ErrorAction Stop | Out-Null
                Write-Ok "Re-enabled Steam task: $($task.TaskPath)$($task.TaskName)"
            } catch {
                $Failures.Value.Add("Task $($task.TaskPath)$($task.TaskName): $($_.Exception.Message)")
            }
        }
        return
    }

    $taskEntries = @(Get-SteamObjectProperty $Recovery 'ScheduledTasks' @())
    if ($taskEntries.Count -gt 0) {
        foreach ($entry in $taskEntries) {
            try {
                $task = Get-ScheduledTask -TaskName ([string]$entry.TaskName) -TaskPath ([string]$entry.TaskPath) -ErrorAction SilentlyContinue
                if (-not $task) {
                    Write-Warn "Captured Steam task no longer exists: $($entry.TaskPath)$($entry.TaskName)"
                    continue
                }
                if ([bool]$entry.Enabled) {
                    Enable-ScheduledTask -TaskName $entry.TaskName -TaskPath $entry.TaskPath -ErrorAction Stop | Out-Null
                } else {
                    Disable-ScheduledTask -TaskName $entry.TaskName -TaskPath $entry.TaskPath -ErrorAction Stop | Out-Null
                }
                $verified = Get-ScheduledTask -TaskName $entry.TaskName -TaskPath $entry.TaskPath -ErrorAction Stop
                if ([bool]$verified.Settings.Enabled -ne [bool]$entry.Enabled) { throw 'task enabled state verification failed' }
                Write-Ok "Restored task state: $($entry.TaskPath)$($entry.TaskName)"
            } catch {
                $Failures.Value.Add("Task $($entry.TaskPath)$($entry.TaskName): $($_.Exception.Message)")
            }
        }
    } else {
        foreach ($task in @(Get-SteamScheduledTaskMatches)) {
            try {
                Enable-ScheduledTask -TaskName $task.TaskName -TaskPath $task.TaskPath -ErrorAction Stop | Out-Null
                Write-Ok "Re-enabled Steam task: $($task.TaskPath)$($task.TaskName)"
            } catch {
                $Failures.Value.Add("Task $($task.TaskPath)$($task.TaskName): $($_.Exception.Message)")
            }
        }
    }

    $notificationRoot = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Notifications\Settings'
    foreach ($entry in @(Get-SteamObjectProperty $Recovery 'Notifications' @())) {
        $path = Join-Path $notificationRoot ([string]$entry.Id)
        try {
            Restore-SteamOptionalRegistryValue $path 'Enabled' `
                ([bool]$entry.EnabledExisted) $entry.EnabledValue ([string]$entry.EnabledKind)
            Restore-SteamOptionalRegistryValue $path 'ShowInActionCenter' `
                ([bool]$entry.ShowInActionCenterExisted) $entry.ShowInActionCenterValue ([string]$entry.ShowInActionCenterKind)
            Write-Ok "Restored notification state: $($entry.Id)"
        } catch {
            $Failures.Value.Add("Notification $($entry.Id): $($_.Exception.Message)")
        }
    }

    foreach ($entry in @(Get-SteamObjectProperty $Recovery 'TrayEntries' @())) {
        try {
            if (-not (Test-Path $entry.Key)) {
                Write-Warn "Captured tray entry no longer exists: $($entry.ExecutablePath)"
                continue
            }
            Restore-SteamOptionalRegistryValue ([string]$entry.Key) 'IsPromoted' `
                ([bool]$entry.IsPromotedExisted) $entry.IsPromotedValue ([string]$entry.IsPromotedKind)
            Write-Ok "Restored tray state: $($entry.ExecutablePath)"
        } catch {
            $Failures.Value.Add("Tray $($entry.ExecutablePath): $($_.Exception.Message)")
        }
    }

    foreach ($entry in @(Get-SteamObjectProperty $Recovery 'ClientPerformance' @())) {
        try {
            Restore-SteamOptionalRegistryValue ([string]$entry.Key) ([string]$entry.Name) `
                ([bool]$entry.Existed) $entry.Value ([string]$entry.Kind)
            Write-Ok "Restored Steam client setting: $($entry.Name)"
        } catch {
            $Failures.Value.Add("Steam client setting $($entry.Name): $($_.Exception.Message)")
        }
    }

    $appPath = Get-SteamObjectProperty $Recovery 'AppPath' $null
    if ($appPath) {
        $key = [string]$appPath.Key
        try {
            if ([bool]$appPath.KeyExisted) {
                Restore-SteamOptionalRegistryValue $key '(default)' `
                    ([bool]$appPath.DefaultExisted) $appPath.DefaultValue ([string]$appPath.DefaultKind)
                Restore-SteamOptionalRegistryValue $key 'Path' `
                    ([bool]$appPath.PathExisted) $appPath.PathValue ([string]$appPath.PathKind)
                Write-Ok 'Restored Steam App Paths entry'
            } elseif (Test-Path $key) {
                Remove-Item -Path $key -Recurse -Force -ErrorAction Stop
                if (Test-Path $key) { throw 'App Paths key is still present' }
                Write-Ok 'Removed Exo-created Steam App Paths entry'
            }
        } catch {
            $Failures.Value.Add("App Paths steam.exe: $($_.Exception.Message)")
        }
    } else {
        Write-Warn 'No Steam App Paths recovery snapshot exists; App Paths entry left untouched'
    }
}

function Invoke-SteamRepair([string]$SteamPath) {
    Write-Step 'Repair: restoring backups and stock Steam shortcuts...'
    $restored = 0
    $failures = [Collections.Generic.List[string]]::new()
    $statePath = Get-ExoSteamStatePath
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

    $bak = Join-Path $SteamPath 'config\config.vdf.exo-bak'
    $cfg = Join-Path $SteamPath 'config\config.vdf'
    if (Test-Path $bak) {
        Copy-Item $bak $cfg -Force
        Remove-Item $bak -Force -ErrorAction SilentlyContinue
        $restored++
        Write-Ok 'Restored config.vdf'
    }

    # Remove Exo steam.cfg bootstrap inhibit (restore stock update-on-launch behavior)
    $steamCfg = Join-Path $SteamPath 'steam.cfg'
    $steamCfgBak = $steamCfg + '.exo-bak'
    if (Test-Path -LiteralPath $steamCfgBak) {
        Copy-Item $steamCfgBak $steamCfg -Force
        Remove-Item $steamCfgBak -Force -ErrorAction SilentlyContinue
        $restored++
        Write-Ok 'Restored steam.cfg'
    } elseif (Test-Path -LiteralPath $steamCfg) {
        try {
            if (Remove-SteamCfgExoKeys $steamCfg) {
                $restored++
                Write-Ok 'Removed Exo bootstrap keys from steam.cfg'
            }
        } catch { }
    }

    $loginBak = Join-Path $SteamPath 'config\loginusers.vdf.exo-bak'
    $login = Join-Path $SteamPath 'config\loginusers.vdf'
    if (Test-Path $loginBak) {
        Copy-Item $loginBak $login -Force
        Remove-Item $loginBak -Force -ErrorAction SilentlyContinue
        $restored++
        Write-Ok 'Restored loginusers.vdf'
    }

    Get-ChildItem -LiteralPath (Join-Path $SteamPath 'userdata') -Directory -ErrorAction SilentlyContinue | ForEach-Object {
        $lb = Join-Path $_.FullName 'config\localconfig.vdf.exo-bak'
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
                if ($sc.TargetPath -match 'Steam-Exo|Exo') {
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

    foreach ($f in @('Steam-Exo.cmd', 'Steam-Exo-Aggressive.cmd', 'Exo-SteamMemoryGuard.ps1', 'Exo-SteamWebHelperTrim.ps1')) {
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
    foreach ($name in @('Steam (Exo Lean).lnk', 'Steam (Exo Aggressive).lnk', 'Steam (Exo).lnk')) {
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
        $agg = Join-Path $dir 'Steam (Exo Aggressive).lnk'
        if (Test-Path $agg) {
            try {
                Remove-Item -LiteralPath $agg -Force -ErrorAction Stop
                if (Test-Path -LiteralPath $agg) { throw 'shortcut is still present' }
                Write-Ok "Removed $agg"
            } catch { $failures.Add("Remove $agg`: $($_.Exception.Message)") }
        }
    }

    Restore-SteamWindowsIntegration $SteamPath $recovery ([ref]$failures)

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
        Write-Ok 'Cleared Exo Steam marker'
    }

    Write-Ok "Repair finished ($restored restore action(s))."
    return $restored
}

# --- main ---
try {
    Write-HubProgress 5 'Starting Steam Optimizer...'
    $steam = Get-SteamInstallPath
    if (-not $steam) {
        throw 'Steam not found. Install Steam, open it once, then rerun Exo.'
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
    # the original pre-Exo value for every key already captured.
    $priorState = Read-SteamOptState
    $priorRecovery = Get-SteamRecoveryFromState $priorState
    $currentRecovery = Get-SteamWindowsRecoverySnapshot $steam
    $recovery = Merge-SteamStartupRecovery $priorRecovery $currentRecovery
    Save-SteamOptState @{
        version         = $Script:SteamOptVersion
        applyStatus     = 'applying'
        applied         = $false
        applyStartedUtc = (Get-Date).ToUniversalTime().ToString('o')
        steamPath       = $steam
        recovery        = $recovery
    }

    # Skip stages that live-verify already correct (user asked: do not re-run proven tweaks).
    Write-HubProgress 24 'Complete client debloat...'
    $debloatResult = @{ Freed = 0L }
    if (Test-SteamCompleteClientDebloat $steam) {
        Write-Ok 'Client debloat already correct'
        Add-ExoReport 'client-debloat' 'ok' 'already correct'
    } else {
        $debloatResult = Invoke-SteamCompleteClientDebloat $steam
        Add-ExoReport 'client-debloat' 'ok'
    }

    Write-HubProgress 30 'Disabling Windows startup...'
    # Always initialize - skip path used to leave $startupResult unset and crash state save.
    $startupResult = @{ Success = $true; Count = 0 }
    if (Test-SteamWindowsStartupDisabled) {
        Write-Ok 'Windows startup already quiet'
        Add-ExoReport 'startup-quiet' 'ok' 'already correct'
        $startupResult = @{
            Success = $true
            Count   = @($recovery.StartupEntries).Count
        }
    } else {
        $startupResult = Disable-SteamWindowsStartup $currentRecovery
        if (-not $startupResult.Success -or -not (Test-SteamWindowsStartupDisabled)) {
            Add-ExoReport 'startup-quiet' 'fail' 'startup suppression could not be verified'
            throw 'Steam startup suppression could not be fully verified; recovery state was kept'
        }
        Add-ExoReport 'startup-quiet' 'ok'
    }

    Write-HubProgress 34 'Windows quiet shell (toasts / tray / tasks)...'
    if (Test-SteamWindowsQuiet $steam) {
        Write-Ok 'Windows quiet shell already correct'
        Add-ExoReport 'windows-quiet' 'ok' 'already correct'
    } else {
        Apply-SteamWindowsQuiet $steam
        Add-ExoReport 'windows-quiet' 'ok'
    }
    Write-HubProgress 36 'GPU preference (discrete when present)...'
    Set-SteamGpuHighPerformance $steam
    Add-ExoReport 'gpu-preference' 'ok'

    Write-HubProgress 38 'Hardware-accelerated Steam web views...'
    Set-SteamClientHardwareAcceleration
    $clientHardwareOk = Test-SteamClientHardwareAcceleration
    if ($clientHardwareOk) { Add-ExoReport 'cef-hardware' 'ok' }
    else {
        Add-ExoReport 'cef-hardware' 'fail' 'Steam registry did not retain hardware acceleration'
        throw 'Steam hardware-accelerated web views could not be enabled'
    }

    Write-HubProgress 40 'Cleaning webhelper / CEF caches...'
    $freed = [long]$debloatResult.Freed
    $shaderFreed = 0L
    $shaderInventoryVerified = $false
    if (-not $Quick) {
        $freed += [long](Clear-SteamSafeCaches $steam)
        Add-ExoReport 'cache-clean' 'ok'
        Write-HubProgress 46 'Cleaning orphaned shader pre-caches...'
        $shaderResult = Clear-SteamShaderCaches $steam
        $shaderFreed = [long]$shaderResult.Freed
        $shaderInventoryVerified = [bool]$shaderResult.InventoryVerified
        if (-not $shaderInventoryVerified) {
            Add-ExoReport 'shader-orphans' 'fail' 'manifest inventory unreadable or ambiguous'
            throw 'Shader cleanup stopped because the installed-game manifest inventory was unreadable or ambiguous'
        }
        Add-ExoReport 'shader-orphans' 'ok'
        Write-HubProgress 50 'Checking resumable downloads...'
        [void](Optimize-SteamDownloadFolder $steam)
    } else {
        Write-Ok 'Deep cache/shader clean skipped (-Quick) - still applying CEF lean + helpers'
        Add-ExoReport 'cache-clean' 'skip' 'quick pass requested'
        Add-ExoReport 'shader-orphans' 'skip' 'quick pass requested'
        $shaderInventoryVerified = $true
    }
    Write-Ok ("Cache cleanup freed ~{0:N1} MB" -f ($freed / 1MB))

    Write-HubProgress 58 'Installing adaptive Steam memory guard...'
    $helper = Install-WebHelperMemoryGuard $steam
    Add-ExoReport 'memory-guard' 'ok'
    Write-HubProgress 68 'Writing quiet CEF launcher...'
    # Always rewrite launcher so CEF flags stay current (e.g. drop broken gpu-disable).
    $launch = Install-LeanSteamLauncher $steam $helper
    Add-ExoReport 'cef-launcher' 'ok'

    Write-HubProgress 74 'Fast login / skip bootstrap update pause...'
    $bootstrapOk = Set-SteamBootstrapFastStart $steam
    if ($bootstrapOk) { Add-ExoReport 'bootstrap-faststart' 'ok' }
    else { Add-ExoReport 'bootstrap-faststart' 'fail' 'steam.cfg could not be written' }
    $loginOk = Set-SteamFastLoginHints $steam
    if ($loginOk) { Add-ExoReport 'fast-login' 'ok' }
    else { Add-ExoReport 'fast-login' 'skip' 'loginusers.vdf not writable yet' }
    # Note: Set-Steam* helpers already no-op / report when keys are already correct.

    Write-HubProgress 78 'Download speed / config.vdf...'
    $cfg = Set-SteamLibraryConfigHints $steam
    $cfgOk = [bool]$cfg.Verified
    $cfgSkipped = [bool]$cfg.Skipped
    if ($cfgOk -and $cfgSkipped) { Add-ExoReport 'download-config' 'skip' ([string]$cfg.Reason) }
    elseif ($cfgOk) { Add-ExoReport 'download-config' 'ok' }
    else { Add-ExoReport 'download-config' 'fail' ([string]$cfg.Reason) }
    Write-HubProgress 88 'Overlay / library / localconfig...'
    $local = Set-SteamLocalConfigTweaks
    $clientTweaksOk = [bool]$local.Verified
    $clientTweaksSkipped = [bool]$local.Skipped
    if ($clientTweaksOk -and $clientTweaksSkipped) { Add-ExoReport 'localconfig-tweaks' 'skip' ([string]$local.Reason) }
    elseif ($clientTweaksOk) { Add-ExoReport 'localconfig-tweaks' 'ok' }
    else { Add-ExoReport 'localconfig-tweaks' 'fail' 'localconfig.vdf verification failed' }

    Write-HubProgress 94 'Saving status...'
    $startupOk = Test-SteamWindowsStartupDisabled
    $windowsQuietOk = Test-SteamWindowsQuiet $steam
    $debloatOk = Test-SteamCompleteClientDebloat $steam
    $runtimeOk = Test-SteamRuntimeIntegrity $steam
    $launchPathOk = Test-SteamStartMenuLaunchPath $steam
    $launcherOk = $false
    try {
        $launcherText = Get-Content -LiteralPath $launch.Cmd -Raw -ErrorAction Stop
        # Must NOT require -cef-disable-gpu (breaks modern steamwebhelper UI).
        $launcherOk = $launcherText -match '(?i)steam\.exe' -and
            $launcherText -match '(?i)-nofriendsui' -and
            $launcherText -match '(?i)-nointro' -and
            $launcherText -match '(?i)start\s+""\s+/HIGH' -and
            $launcherText -notmatch '(?i)-cef-disable-gpu'
    } catch { }
    $helperOk = $false
    try {
        $helperText = Get-Content -LiteralPath $helper -Raw -ErrorAction Stop
        # Audit executable lines only: documentation names the operations this
        # helper forbids and must not make a safe generated helper fail verification.
        $helperUnsafe = $false
        foreach ($rawLine in ($helperText -split "`n")) {
            $line = $rawLine.TrimStart()
            if ($line.StartsWith('#') -or $line.StartsWith('//')) { continue }
            if ($line.Contains('EmptyWorkingSet(') -or $line -match '(?i)Stop-Process.*steamwebhelper') {
                $helperUnsafe = $true
                break
            }
        }
        $helperOk = $helperText -match 'Exo\.SteamMemoryGuard' -and
            $helperText -match 'SetProcessInformation' -and
            $helperText -match 'SetMemoryPriority' -and
            $helperText -match 'ForegroundPid' -and
            $helperText -match 'ProcessPriorityClass\]::BelowNormal' -and
            $helperText -match '(?s)\$steamCls\s*=\s*if\s*\(\$InGame\).*?BelowNormal.*?Normal' -and
            $helperText -match '(?s)\$webCls\s*=\s*if\s*\(\$InGame\).*?BelowNormal.*?Normal' -and
            $helperText -match '\$_\.PriorityClass\s*=\s*\$webCls' -and
            (-not $helperUnsafe)
    } catch { }
    $fullPassOk = -not [bool]$Quick
    # Core pack (always required for applied). VDF first-run skips are NOT essentials-
    # satisfied: detect requires downloadOptimized/snappyUi true, so applied must match.
    $coreOk = $startupOk -and $windowsQuietOk -and $debloatOk -and $runtimeOk -and $clientHardwareOk -and
        $launchPathOk -and $launcherOk -and $helperOk -and $fullPassOk -and $shaderInventoryVerified
    $vdfReady = [bool]$cfgOk -and -not $cfgSkipped -and [bool]$clientTweaksOk -and -not $clientTweaksSkipped
    $essentialOk = $coreOk -and $vdfReady
    $state = @{
        version              = $Script:SteamOptVersion
        applyStatus          = if ($essentialOk) { 'applied' } else { 'incomplete' }
        applied              = $essentialOk
        appliedUtc           = (Get-Date).ToUniversalTime().ToString('o')
        steamPath            = $steam
        recovery             = $recovery
        startupDisabled      = $startupOk
        startupRemoved       = [int]$(if ($null -ne $startupResult -and $null -ne $startupResult.Count) { $startupResult.Count } else { 0 })
        startupEntries       = @($recovery.StartupEntries)
        startupModeCaptured  = [bool]$recovery.StartupModeCaptured
        hadStartupMode       = [bool]$recovery.HadStartupMode
        previousStartupMode  = $recovery.PreviousStartupMode
        previousStartupModeKind = $recovery.PreviousStartupModeKind
        windowsVerified      = $windowsQuietOk
        debloatVerified      = $debloatOk
        launchPathVerified   = $launchPathOk
        runtimeVerified      = $runtimeOk
        cacheFreedBytes      = $freed
        cacheCleanupCompleted = $fullPassOk
        shaderCacheFreedBytes = $shaderFreed
        shaderInventoryVerified = $shaderInventoryVerified
        configTouched        = [bool]$cfg.Touched
        configVerified       = [bool]$cfgOk
        configSkipped        = $cfgSkipped
        clientTweaksVerified = $clientTweaksOk
        clientTweaksSkipped  = $clientTweaksSkipped
        clientHardwareAcceleration = $clientHardwareOk
        webGpuReduced        = ($clientTweaksOk -and -not $clientTweaksSkipped)
        snappyUi             = ($clientTweaksOk -and -not $clientTweaksSkipped)
        overlayTweaks        = ($clientTweaksOk -and -not $clientTweaksSkipped)
        cefLeanLaunch        = $launcherOk
        cefArgs              = ($Script:DefaultCefArgs -join ' ')
        leanCmd              = $launch.Cmd
        memoryGuard          = $helperOk
        inGameContentionGuard = $helperOk
        inGamePriorityYield  = $helperOk
        highPriority         = $helperOk
        downloadOptimized    = ([bool]$cfgOk -and -not $cfgSkipped)
        installedShaderCachesPreserved = $shaderInventoryVerified
        noDesktopShortcuts   = $debloatOk
        fullApply            = $fullPassOk
        quick                = [bool]$Quick
        applyReport          = @(Get-ExoReportEntries)
    }
    Save-SteamOptState $state

    if (-not $essentialOk) {
        if ($Quick) {
            Write-Warn 'Quick pass completed, but the full no-compromise applied state remains incomplete by design'
            Write-HubProgress 100 'Quick pass complete (full apply still required)'
            Write-Output 'DONE - Steam quick pass complete; run full Apply for verified no-compromise state'
            exit 0
        }
        $missing = @()
        if (-not $startupOk) { $missing += 'startup' }
        if (-not $windowsQuietOk) { $missing += 'windows-quiet' }
        if (-not $debloatOk) { $missing += 'debloat' }
        if (-not $runtimeOk) { $missing += 'runtime' }
        if (-not $clientHardwareOk) { $missing += 'cef-hardware' }
        if (-not $launchPathOk) { $missing += 'launch-path' }
        if (-not $launcherOk) { $missing += 'cef-launcher' }
        if (-not $helperOk) { $missing += 'memory-guard' }
        if (-not $cfgOk) { $missing += 'download-config' }
        elseif ($cfgSkipped) { $missing += 'download-config-open-steam-once' }
        if (-not $clientTweaksOk) { $missing += 'client-tweaks' }
        elseif ($clientTweaksSkipped) { $missing += 'localconfig-open-steam-once' }
        if (-not $shaderInventoryVerified) { $missing += 'shader-inventory' }
        # First-run only: core pack OK but VDF absent - incomplete (not full applied), exit 0 so UI is not "Failed".
        $firstRunVdfOnly = $coreOk -and (-not $vdfReady) -and (
            ($cfgSkipped -or $clientTweaksSkipped) -and
            ([bool]$cfgOk -or $cfgSkipped) -and
            ([bool]$clientTweaksOk -or $clientTweaksSkipped)
        )
        if ($firstRunVdfOnly) {
            Write-Warn ("Steam core pack applied; open Steam once then Reapply for VDF tweaks: {0}" -f ($missing -join ', '))
            Write-HubProgress 100 'Core complete - open Steam once, then Reapply'
            Write-Output 'DONE - Steam core optimized; open Steam once then Reapply for download/library VDF keys'
            exit 0
        }
        throw ("Steam apply finished with incomplete verification: {0}" -f ($missing -join ', '))
    }

    Write-Ok 'Steam Optimizer finished (CEF quiet, full debloat, Windows quiet, in-game priority yield)'
    Write-Ok 'Start Steam from Start Menu for Exo launcher; taskbar pins stay stock steam.exe.'
    Write-HubProgress 100 'Completed successfully'
    Write-Output 'DONE - Steam optimized (debloat + Windows quiet + CEF + in-game contention guard)'
    exit 0
} catch {
    $failureRecord = $_
    if (-not $Repair) {
        # Persist the structured report for incomplete applies so the UI can
        # show exactly which step failed. Recovery data is preserved.
        try {
            $failedState = Read-SteamOptState
            $failedRecovery = Get-SteamRecoveryFromState $failedState
            Add-ExoReport 'apply' 'fail' ([string]$failureRecord.Exception.Message)
            Save-SteamOptState @{
                version     = $Script:SteamOptVersion
                applyStatus = 'incomplete'
                applied     = $false
                steamPath   = $(if ($failedState -and $failedState.PSObject.Properties.Name -contains 'steamPath') { [string]$failedState.steamPath } else { '' })
                recovery    = $failedRecovery
                applyReport = @(Get-ExoReportEntries)
                failedUtc   = (Get-Date).ToUniversalTime().ToString('o')
            }
        } catch { }
    }
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
