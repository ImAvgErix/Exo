# SteamDetectCore.ps1 - pure detect classifiers (no Steam launch).
# Dot-sourced by Exo-Steam-Detect.ps1; smokes invoke this file.
# Keep aligned with Exo.Services.SteamLogic.

Set-StrictMode -Version Latest

function Test-SteamCefLauncherText {
    param([AllowNull()][string]$Text)
    if ([string]::IsNullOrWhiteSpace($Text)) { return $false }
    # Must not require -cef-disable-gpu (breaks modern CEF/steamwebhelper UI).
    return ($Text -match '(?i)steam\.exe') -and
        ($Text -match '-nofriendsui') -and
        ($Text -match '-nointro') -and
        ($Text -match '(?i)start\s+""\s+/HIGH') -and
        ($Text -notmatch '-cef-disable-gpu')
}

function Test-SteamMemoryGuardText {
    param([AllowNull()][string]$Text)
    if ([string]::IsNullOrWhiteSpace($Text)) { return $false }
    if ($Text -notmatch 'Exo\.SteamMemoryGuard') { return $false }
    if ($Text -notmatch 'SetProcessInformation') { return $false }
    if ($Text -notmatch 'SetMemoryPriority') { return $false }
    if ($Text -notmatch 'SetPowerThrottled') { return $false }
    if ($Text -notmatch 'ForegroundPid') { return $false }
    if ($Text -notmatch 'ProcessPriorityClass\]::Normal') { return $false }
    if ($Text -notmatch 'ProcessPriorityClass\]::BelowNormal') { return $false }
    if ($Text -notmatch '(?s)\$steamCls\s*=\s*if\s*\(\$InGame\).*?BelowNormal.*?Normal') { return $false }
    if ($Text -notmatch '(?s)\$backgroundWebCls\s*=\s*if\s*\(\$InGame\).*?BelowNormal.*?Normal') { return $false }
    if ($Text -notmatch '(?s)\$webCls\s*=\s*if\s*\(\$_\.Id\s*-eq\s*\$foregroundPid\).*?Normal.*?\$backgroundWebCls') { return $false }
    if ($Text -notmatch '\$_\.PriorityClass\s*=\s*\$webCls') { return $false }
    if ($Text -notmatch '(?s)\$memoryPriority\s*=\s*if\s*\(\$_\.Id\s*-eq\s*\$foregroundPid\).*?5.*?elseif\s*\(\$InGame\).*?1.*?else\s*\{\s*2\s*\}') { return $false }
    if ($Text -notmatch 'SetPowerThrottled\(\$_\.Id, \(\$_\.Id -ne \$foregroundPid\)\)' -and
        $Text -notmatch 'SetPowerThrottled\(\$_\.Id, \(\$InGame -and \$_\.Id -ne \$foregroundPid\)\)') { return $false }
    # EmptyWorkingSet freezes CEF - always banned. SoftReclaimWorkingSet allowed
    # when gated on non-foreground CEF (library + in-game).
    $allowsSoftReclaim = ($Text -match 'SoftReclaimWorkingSet') -and
        ($Text -match '\$_\.Id -ne \$foregroundPid')
    foreach ($rawLine in ($Text -split "`n")) {
        $line = $rawLine.TrimStart()
        if ($line.StartsWith('#') -or $line.StartsWith('//')) { continue }
        if ($line.Contains('EmptyWorkingSet(')) { return $false }
        if ($line.Contains('SetProcessWorkingSetSize') -and -not $allowsSoftReclaim) { return $false }
        if ($line -match '(?i)Stop-Process.*steamwebhelper|Suspend-Process') { return $false }
    }
    # Competitive cadence uses 1s in-game / 2s library. Accept any loop sleep in 1-15s
    # (first match may be the 1s game branch  -  do not require >=2 only).
    $secHits = [regex]::Matches($Text, 'Start-Sleep\s+-Seconds\s+(\d+)', 'IgnoreCase')
    foreach ($m in $secHits) {
        $sec = [int]$m.Groups[1].Value
        if ($sec -ge 1 -and $sec -le 15) { return $true }
    }
    $msHits = [regex]::Matches($Text, 'Start-Sleep\s+-Milliseconds\s+(\d+)', 'IgnoreCase')
    foreach ($m in $msHits) {
        $ms = [int]$m.Groups[1].Value
        if ($ms -ge 1000 -and $ms -le 15000) { return $true }
    }
    return $false
}

function Test-SteamToastsOffFromMap {
    param([hashtable]$Map)
    if ($null -eq $Map -or $Map.Count -eq 0) { return $false }
    $seen = $false
    foreach ($key in $Map.Keys) {
        $val = $Map[$key]
        if ($null -eq $val) { continue }
        $seen = $true
        try {
            if ([int]$val -ne 0) { return $false }
        } catch { return $false }
    }
    return $seen
}

function Test-SteamApplyRecord {
    param($State)
    if ($null -eq $State) { return $false }
    try {
        if ([string]$State.applyStatus -ne 'applied') { return $false }
        if ($State.applied -ne $true) { return $false }
        if ($State.quick -ne $false) { return $false }
        if ($State.fullApply -ne $true) { return $false }
        if ($State.windowsVerified -ne $true) { return $false }
        if ($State.debloatVerified -ne $true) { return $false }
        if ($State.cacheCleanupCompleted -ne $true) { return $false }
        if ($State.shaderInventoryVerified -ne $true) { return $false }
        if ($State.installedShaderCachesPreserved -ne $true) { return $false }
        return $true
    } catch { return $false }
}

function Test-SteamLegacyAggressiveCmdAbsent {
    param([string]$SteamPath)
    if (-not $SteamPath) { return $false }
    foreach ($name in @('Steam-Exo-Aggressive.cmd', 'Steam-Exo-Lean.cmd', 'Steam-Exo-Legacy.cmd')) {
        if (Test-Path -LiteralPath (Join-Path $SteamPath $name)) { return $false }
    }
    return $true
}

function Test-SteamGameExeNameJunk([string]$Name) {
    if ([string]::IsNullOrWhiteSpace($Name)) { return $true }
    return ($Name -match '(?i)^(UnityCrashHandler|CrashReport|CrashHandler|EasyAntiCheat(_EOS)?|BEService|BEClient|vcredist|vc_redist|dotnet|setup|uninstall|unins\d*|REDprelauncher|EpicWebHelper|steamerrorreporter|steam_monitor|cef_server|streaming_client|write_mini_dump|installscript|dxsetup|vulkansdk|oalinst|PhysX|dotnetfx|WindowsNoEditor|Win64Server|DedicatedServer)')
}

function Get-SteamLibraryRootsCore([string]$SteamPath) {
    $roots = New-Object System.Collections.Generic.List[string]
    if ($SteamPath) { [void]$roots.Add($SteamPath) }
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

function Get-SteamInstalledGameExes {
    param(
        [Parameter(Mandatory)][string]$SteamPath,
        [int]$MaxPaths = 300
    )
    $list = [System.Collections.Generic.List[string]]::new()
    $seen = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($lib in @(Get-SteamLibraryRootsCore $SteamPath)) {
        $steamApps = Join-Path $lib 'steamapps'
        if (-not (Test-Path -LiteralPath $steamApps -PathType Container)) { continue }
        $manifests = @()
        try { $manifests = @(Get-ChildItem -LiteralPath $steamApps -Filter 'appmanifest_*.acf' -File -ErrorAction Stop) } catch { continue }
        foreach ($mf in $manifests) {
            if ($list.Count -ge $MaxPaths) { break }
            $installdir = $null
            try {
                $text = [IO.File]::ReadAllText($mf.FullName)
                $m = [regex]::Match($text, '"installdir"\s+"([^"]+)"')
                if ($m.Success) { $installdir = $m.Groups[1].Value }
            } catch { continue }
            if ([string]::IsNullOrWhiteSpace($installdir)) { continue }
            $common = Join-Path $steamApps ("common\" + $installdir)
            if (-not (Test-Path -LiteralPath $common -PathType Container)) { continue }
            $candidates = [System.Collections.Generic.List[string]]::new()
            try {
                Get-ChildItem -LiteralPath $common -Filter '*.exe' -File -ErrorAction SilentlyContinue |
                    ForEach-Object { [void]$candidates.Add($_.FullName) }
                Get-ChildItem -LiteralPath $common -Directory -ErrorAction SilentlyContinue | ForEach-Object {
                    Get-ChildItem -LiteralPath $_.FullName -Filter '*.exe' -File -ErrorAction SilentlyContinue |
                        ForEach-Object { [void]$candidates.Add($_.FullName) }
                    foreach ($sub in @('Binaries\Win64', 'bin\Win64', 'Win64', 'x64', 'binaries', 'bin')) {
                        $p = Join-Path $_.FullName $sub
                        if (Test-Path -LiteralPath $p -PathType Container) {
                            Get-ChildItem -LiteralPath $p -Filter '*.exe' -File -ErrorAction SilentlyContinue |
                                ForEach-Object { [void]$candidates.Add($_.FullName) }
                        }
                    }
                }
            } catch { }
            foreach ($exe in $candidates) {
                if ($list.Count -ge $MaxPaths) { break }
                $leaf = [IO.Path]::GetFileName($exe)
                if (Test-SteamGameExeNameJunk $leaf) { continue }
                if ($leaf -match '(?i)^steam(webhelper|errorreporter)?\.exe$') { continue }
                if ($seen.Add($exe)) { [void]$list.Add($exe) }
            }
        }
        if ($list.Count -ge $MaxPaths) { break }
    }
    return @($list)
}

function Test-SteamLibraryGamePolicy {
    # GPU high-perf only  -  Games hub owns borderless; FSO-off on games is legacy.
    param([Parameter(Mandatory)][string]$SteamPath)
    $paths = @(Get-SteamInstalledGameExes -SteamPath $SteamPath -MaxPaths 40)
    if ($paths.Count -eq 0) { return $true }
    $gpuKey = $null
    $hits = 0
    $need = 0
    try {
        $gpuKey = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Software\Microsoft\DirectX\UserGpuPreferences')
        foreach ($p in $paths) {
            $need++
            if ($gpuKey -and [string]$gpuKey.GetValue($p, '') -eq 'GpuPreference=2;') { $hits++ }
        }
    } finally {
        if ($gpuKey) { $gpuKey.Dispose() }
    }
    if ($need -eq 0) { return $true }
    return (($hits / $need) -ge 0.5)
}
