# Exo.AmoledTheme.ps1 - pure black / AMOLED Windows shell theme (UI only).
# Inspired by common pure-dark tools (AppsUseLightTheme / DWM accent) but owned
# by Exo with snapshot/repair. Does NOT touch anti-cheat, drivers, or game files.

Set-StrictMode -Version Latest

function Get-ExoAmoledThemeSnapshot {
    $list = [System.Collections.Generic.List[object]]::new()
    foreach ($t in @(
        @{ Hive = 'HKCU'; Path = 'Software\Microsoft\Windows\CurrentVersion\Themes\Personalize'; Name = 'AppsUseLightTheme' },
        @{ Hive = 'HKCU'; Path = 'Software\Microsoft\Windows\CurrentVersion\Themes\Personalize'; Name = 'SystemUsesLightTheme' },
        @{ Hive = 'HKCU'; Path = 'Software\Microsoft\Windows\CurrentVersion\Themes\Personalize'; Name = 'EnableTransparency' },
        @{ Hive = 'HKCU'; Path = 'Software\Microsoft\Windows\CurrentVersion\Themes\Personalize'; Name = 'ColorPrevalence' },
        @{ Hive = 'HKCU'; Path = 'Software\Microsoft\Windows\DWM'; Name = 'ColorPrevalence' },
        @{ Hive = 'HKCU'; Path = 'Software\Microsoft\Windows\DWM'; Name = 'AccentColor' },
        @{ Hive = 'HKCU'; Path = 'Software\Microsoft\Windows\DWM'; Name = 'ColorizationColor' },
        @{ Hive = 'HKCU'; Path = 'Software\Microsoft\Windows\DWM'; Name = 'EnableWindowColorization' },
        @{ Hive = 'HKCU'; Path = 'Software\Microsoft\Windows\CurrentVersion\Explorer\Accent'; Name = 'AccentColorMenu' },
        @{ Hive = 'HKCU'; Path = 'Software\Microsoft\Windows\CurrentVersion\Explorer\Accent'; Name = 'StartColorMenu' }
    )) {
        $entry = [ordered]@{
            hive = $t.Hive; path = $t.Path; name = $t.Name
            existed = $false; value = $null; kind = $null
        }
        try {
            $root = [Microsoft.Win32.Registry]::CurrentUser
            $key = $root.OpenSubKey($t.Path)
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

function Set-ExoAmoledTheme {
    # Pure black shell: dark app + system theme, no transparency, black accent.
    # Safe UI personalization only.
    param([switch]$Force)
    $n = 0
    # Opaque black for DWM (signed int bits of 0xFF000000)
    $dwmBlack = [BitConverter]::ToInt32([BitConverter]::GetBytes([uint32]4278190080), 0)
    try {
        $pers = 'Software\Microsoft\Windows\CurrentVersion\Themes\Personalize'
        $key = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($pers, $true)
        try {
            foreach ($pair in @(
                @{ N = 'AppsUseLightTheme'; V = 0 },
                @{ N = 'SystemUsesLightTheme'; V = 0 },
                @{ N = 'EnableTransparency'; V = 0 },
                @{ N = 'ColorPrevalence'; V = 1 }
            )) {
                $cur = $key.GetValue($pair.N, $null)
                if (-not $Force -and $null -ne $cur -and [int]$cur -eq [int]$pair.V) { continue }
                $key.SetValue($pair.N, [int]$pair.V, [Microsoft.Win32.RegistryValueKind]::DWord)
                $n++
            }
        } finally { $key.Dispose() }
    } catch { }
    try {
        $dwm = 'Software\Microsoft\Windows\DWM'
        $key = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($dwm, $true)
        try {
            $key.SetValue('ColorPrevalence', 1, [Microsoft.Win32.RegistryValueKind]::DWord)
            $key.SetValue('EnableWindowColorization', 1, [Microsoft.Win32.RegistryValueKind]::DWord)
            $key.SetValue('AccentColor', $dwmBlack, [Microsoft.Win32.RegistryValueKind]::DWord)
            $key.SetValue('ColorizationColor', $dwmBlack, [Microsoft.Win32.RegistryValueKind]::DWord)
            $n += 4
        } finally { $key.Dispose() }
    } catch { }
    try {
        $acc = 'Software\Microsoft\Windows\CurrentVersion\Explorer\Accent'
        $key = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($acc, $true)
        try {
            $key.SetValue('AccentColorMenu', $dwmBlack, [Microsoft.Win32.RegistryValueKind]::DWord)
            $key.SetValue('StartColorMenu', $dwmBlack, [Microsoft.Win32.RegistryValueKind]::DWord)
            # Palette blob optional  -  skip if complex
            $n += 2
        } finally { $key.Dispose() }
    } catch { }
    return $n
}

function Test-ExoAmoledTheme {
    try {
        $apps = [int](Get-ItemPropertyValue -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize' -Name 'AppsUseLightTheme' -ErrorAction Stop)
        $sys = [int](Get-ItemPropertyValue -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize' -Name 'SystemUsesLightTheme' -ErrorAction Stop)
        if ($apps -ne 0 -or $sys -ne 0) { return $false }
        try {
            $tr = [int](Get-ItemPropertyValue -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize' -Name 'EnableTransparency' -ErrorAction Stop)
            if ($tr -ne 0) { return $false }
        } catch { }
        return $true
    } catch { return $false }
}

function Restore-ExoAmoledThemeFromSnapshot {
    param($SnapshotEntries)
    if (-not $SnapshotEntries) { return 0 }
    $n = 0
    foreach ($entry in @($SnapshotEntries)) {
        try {
            $key = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey([string]$entry.path, $true)
            try {
                if ([bool]$entry.existed) {
                    $kind = [Microsoft.Win32.RegistryValueKind]::DWord
                    if ($entry.kind) {
                        [void][enum]::TryParse([Microsoft.Win32.RegistryValueKind], [string]$entry.kind, $true, [ref]$kind)
                    }
                    $key.SetValue([string]$entry.name, $entry.value, $kind)
                } else {
                    try { $key.DeleteValue([string]$entry.name, $false) } catch { }
                }
                $n++
            } finally { $key.Dispose() }
        } catch { }
    }
    return $n
}
