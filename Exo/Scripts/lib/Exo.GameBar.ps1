# Exo.GameBar.ps1 - shared Game Bar / Game DVR quieting for gaming optimizers.
# Dot-source from Steam / Riot / Epic apply scripts. Reversible via snapshot restore.
# Does not touch anti-cheat, game binaries, or unrelated policies.

Set-StrictMode -Version Latest

function Get-ExoGameBarTargets {
    # Ordered: path, name, desired DWORD value for "quiet".
    # Does not touch anti-cheat, VBS, or game binaries  -  Game DVR/overlay only.
    @(
        @{ Hive = 'HKCU'; Path = 'Software\Microsoft\Windows\CurrentVersion\GameDVR'; Name = 'AppCaptureEnabled'; Desired = 0 },
        @{ Hive = 'HKCU'; Path = 'Software\Microsoft\Windows\CurrentVersion\GameDVR'; Name = 'HistoricalCaptureEnabled'; Desired = 0 },
        @{ Hive = 'HKCU'; Path = 'System\GameConfigStore'; Name = 'GameDVR_Enabled'; Desired = 0 },
        @{ Hive = 'HKCU'; Path = 'Software\Microsoft\GameBar'; Name = 'UseNexusForGameBarEnabled'; Desired = 0 },
        @{ Hive = 'HKCU'; Path = 'Software\Microsoft\GameBar'; Name = 'ShowStartupPanel'; Desired = 0 },
        @{ Hive = 'HKCU'; Path = 'Software\Microsoft\GameBar'; Name = 'GamePanelStartupTipIndex'; Desired = 3 },
        @{ Hive = 'HKLM'; Path = 'SOFTWARE\Policies\Microsoft\Windows\GameDVR'; Name = 'AllowGameDVR'; Desired = 0 }
    )
}

function Get-ExoGameBarOpenKey {
    param([Parameter(Mandatory)][string]$Hive, [Parameter(Mandatory)][string]$Path, [bool]$Writable = $false)
    $root = if ($Hive -eq 'HKLM') { [Microsoft.Win32.Registry]::LocalMachine } else { [Microsoft.Win32.Registry]::CurrentUser }
    if ($Writable) {
        return $root.CreateSubKey($Path, $true)
    }
    return $root.OpenSubKey($Path)
}

function Get-ExoGameBarSnapshot {
    $list = [System.Collections.Generic.List[object]]::new()
    foreach ($t in @(Get-ExoGameBarTargets)) {
        $entry = [ordered]@{
            hive    = [string]$t.Hive
            path    = [string]$t.Path
            name    = [string]$t.Name
            existed = $false
            value   = $null
            kind    = $null
        }
        try {
            $key = Get-ExoGameBarOpenKey -Hive $t.Hive -Path $t.Path
            if ($key) {
                try {
                    if ($t.Name -in @($key.GetValueNames())) {
                        $entry.existed = $true
                        $entry.value = $key.GetValue($t.Name)
                        $entry.kind = [string]$key.GetValueKind($t.Name)
                    }
                } finally { $key.Dispose() }
            }
        } catch { }
        [void]$list.Add([pscustomobject]$entry)
    }
    return @($list)
}

function Test-ExoGameBarQuiet {
    # Core quiet signal: DVR capture + GameDVR_Enabled + policy when present.
    # Missing optional tip keys do not fail the whole check after a clean apply.
    $core = @(
        @{ Hive = 'HKCU'; Path = 'Software\Microsoft\Windows\CurrentVersion\GameDVR'; Name = 'AppCaptureEnabled'; Desired = 0 },
        @{ Hive = 'HKCU'; Path = 'System\GameConfigStore'; Name = 'GameDVR_Enabled'; Desired = 0 },
        @{ Hive = 'HKCU'; Path = 'Software\Microsoft\GameBar'; Name = 'UseNexusForGameBarEnabled'; Desired = 0 }
    )
    foreach ($t in $core) {
        try {
            $key = Get-ExoGameBarOpenKey -Hive $t.Hive -Path $t.Path
            if (-not $key) { return $false }
            try {
                $v = $key.GetValue($t.Name, $null)
                if ($null -eq $v) { return $false }
                if ([int]$v -ne [int]$t.Desired) { return $false }
            } finally { $key.Dispose() }
        } catch { return $false }
    }
    # Historical capture: treat missing as not quiet
    try {
        $key = Get-ExoGameBarOpenKey -Hive 'HKCU' -Path 'Software\Microsoft\Windows\CurrentVersion\GameDVR'
        if ($key) {
            try {
                $h = $key.GetValue('HistoricalCaptureEnabled', $null)
                if ($null -ne $h -and [int]$h -ne 0) { return $false }
                # null/missing after apply is OK if AppCapture is 0 (some builds omit the value)
            } finally { $key.Dispose() }
        }
    } catch { }
    return $true
}

function Set-ExoGameBarQuiet {
    param([switch]$Force)
    $written = 0
    foreach ($t in @(Get-ExoGameBarTargets)) {
        try {
            $key = Get-ExoGameBarOpenKey -Hive $t.Hive -Path $t.Path -Writable
            if (-not $key) { continue }
            try {
                $cur = $key.GetValue($t.Name, $null)
                if (-not $Force -and $null -ne $cur -and [int]$cur -eq [int]$t.Desired) { continue }
                $key.SetValue($t.Name, [int]$t.Desired, [Microsoft.Win32.RegistryValueKind]::DWord)
                $written++
            } finally { $key.Dispose() }
        } catch { }
    }
    return $written
}

function Restore-ExoGameBarFromSnapshot {
    param($SnapshotEntries)
    if (-not $SnapshotEntries) { return 0 }
    $restored = 0
    foreach ($entry in @($SnapshotEntries)) {
        try {
            $hive = [string]$entry.hive
            $path = [string]$entry.path
            $name = [string]$entry.name
            if ([string]::IsNullOrWhiteSpace($path) -or [string]::IsNullOrWhiteSpace($name)) { continue }
            $key = Get-ExoGameBarOpenKey -Hive $hive -Path $path -Writable
            if (-not $key) { continue }
            try {
                if ([bool]$entry.existed) {
                    $kind = [Microsoft.Win32.RegistryValueKind]::DWord
                    if ($entry.kind -and [enum]::TryParse([Microsoft.Win32.RegistryValueKind], [string]$entry.kind, $true, [ref]$kind)) {
                        # parsed
                    }
                    $key.SetValue($name, $entry.value, $kind)
                } else {
                    try { $key.DeleteValue($name, $false) } catch { }
                }
                $restored++
            } finally { $key.Dispose() }
        } catch { }
    }
    return $restored
}

function Set-ExoGameQosPolicy {
    # Per-game DSCP 46 UDP (same model as Discord voice QoS). Path-scoped by exe name.
    param(
        [Parameter(Mandatory)][string]$PolicyName,
        [Parameter(Mandatory)][string]$ExeName
    )
    $root = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\QoS'
    $path = Join-Path $root $PolicyName
    if (-not (Test-Path -LiteralPath $root)) {
        New-Item -Path $root -Force | Out-Null
    }
    if (-not (Test-Path -LiteralPath $path)) {
        New-Item -Path $path -Force | Out-Null
    }
    foreach ($pair in @(
        @{ N = 'Version'; V = '1.0' },
        @{ N = 'Application Name'; V = $ExeName },
        @{ N = 'Protocol'; V = 'UDP' },
        @{ N = 'Local Port'; V = '*' },
        @{ N = 'Remote Port'; V = '*' },
        @{ N = 'Local IP'; V = '*' },
        @{ N = 'Remote IP'; V = '*' },
        @{ N = 'DSCP Value'; V = '46' },
        @{ N = 'Throttle Rate'; V = '-1' }
    )) {
        New-ItemProperty -LiteralPath $path -Name $pair.N -Value $pair.V -PropertyType String -Force -ErrorAction Stop | Out-Null
    }
    $item = Get-Item -LiteralPath $path -ErrorAction Stop
    return ([string]$item.GetValue('DSCP Value') -eq '46') -and
        ([string]$item.GetValue('Application Name') -ieq $ExeName)
}

function Remove-ExoGameQosPolicy {
    param([Parameter(Mandatory)][string]$PolicyName)
    $path = Join-Path 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\QoS' $PolicyName
    if (Test-Path -LiteralPath $path) {
        Remove-Item -LiteralPath $path -Recurse -Force -ErrorAction SilentlyContinue
        return $true
    }
    return $false
}

function Test-ExoGameQosPolicy {
    param([Parameter(Mandatory)][string]$PolicyName, [Parameter(Mandatory)][string]$ExeName)
    $path = Join-Path 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\QoS' $PolicyName
    if (-not (Test-Path -LiteralPath $path)) { return $false }
    try {
        $item = Get-Item -LiteralPath $path -ErrorAction Stop
        return ([string]$item.GetValue('DSCP Value') -eq '46') -and
            ([string]$item.GetValue('Application Name') -ieq $ExeName) -and
            ([string]$item.GetValue('Protocol') -eq 'UDP')
    } catch { return $false }
}
