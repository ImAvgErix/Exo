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
    # Working-set thrash freezes CEF - ban EmptyWorkingSet, SetProcessWorkingSetSize,
    # and SoftReclaimWorkingSet (same thrash under another name).
    foreach ($rawLine in ($Text -split "`n")) {
        $line = $rawLine.TrimStart()
        if ($line.StartsWith('#') -or $line.StartsWith('//')) { continue }
        if ($line.Contains('EmptyWorkingSet(')) { return $false }
        if ($line.Contains('SetProcessWorkingSetSize')) { return $false }
        if ($line.Contains('SoftReclaimWorkingSet')) { return $false }
        if ($line -match '(?i)Stop-Process.*steamwebhelper|Suspend-Process') { return $false }
    }
    # Soft reclaim cadence prefers >=4s. Accept any loop sleep in 1-15s for legacy templates.
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

function Test-SteamHybridGpu {
    # THE hybrid test for the Steam kit. Everything that routes a GPU preference calls
    # this - client routing, library game routing, and detect - because they must agree.
    #
    # There used to be three spellings of this question and they disagreed. Two lived
    # inline in Steam-Optimizer: the client-routing copy matched
    #   NVIDIA|GeForce|RTX|GTX|Radeon RX|Intel.*Arc      (no Quadro)
    # and the game-routing copy matched
    #   NVIDIA|GeForce|RTX|GTX|Quadro|Radeon RX|Arc A    (no Intel.*Arc)
    # so on a Quadro laptop Apply moved the Steam client but not the games, and on an
    # Intel Arc laptop it did the reverse - each half convinced the machine was a
    # different shape. The regex below is the union, which is what both meant.
    #
    # Keep aligned with GpuTopology.IsHybrid on the C# side.
    $hybrid = $false
    try {
        $adapters = @(Get-CimInstance Win32_VideoController -ErrorAction SilentlyContinue |
            ForEach-Object { [string]$_.Name } |
            Where-Object { $_ -and ($_ -notmatch '(?i)Microsoft Basic|Hyper-V|Remote|Virtual|Parsec|DisplayLink') })
        if (@($adapters).Count -ge 2) {
            $hasDiscrete = @($adapters | Where-Object { $_ -match '(?i)NVIDIA|GeForce|RTX|GTX|Quadro|Radeon\s+RX|Intel.*Arc|Arc\s*A' }).Count -gt 0
            $hasIntegrated = @($adapters | Where-Object { $_ -match '(?i)Intel.*(?:UHD|Iris|HD Graphics)|AMD Radeon\(TM\) Graphics|Radeon Vega' }).Count -gt 0
            $hybrid = ($hasDiscrete -and $hasIntegrated)
        }
    } catch { }
    return $hybrid
}

# Test-SteamLibraryGamePolicy used to live here and had no callers anywhere in the repo.
# Detect reads the libraryGamePolicyVerified marker from state instead, deliberately: a
# live scan of every installed game is multi-second on a large library and detect runs on
# every orb refresh. A second, unreachable copy of the policy rule is exactly how Apply and
# Detect drifted apart before, so it is gone rather than left to rot. Test-SteamHybridGpu
# above is the part that had real callers and stayed.
