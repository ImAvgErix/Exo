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

function Test-SteamTrimHelperText {
    param([AllowNull()][string]$Text)
    if ([string]::IsNullOrWhiteSpace($Text)) { return $false }
    if ($Text -notmatch 'Exo\.SteamWebHelper') { return $false }
    if ($Text -notmatch 'ProcessPriorityClass\]::BelowNormal') { return $false }
    if ($Text -notmatch '(?s)\$steamCls\s*=\s*if\s*\(\$InGame\).*?BelowNormal.*?Normal') { return $false }
    if ($Text -notmatch '(?s)\$webCls\s*=\s*if\s*\(\$InGame\).*?BelowNormal.*?Normal') { return $false }
    if ($Text -notmatch '\$_\.PriorityClass\s*=\s*\$webCls') { return $false }
    # EmptyWorkingSet on steamwebhelper freezes/kills CEF UI - reject thrashing helpers.
    # Evaluate code lines only: a '# Never EmptyWorkingSet' comment must not exempt a real call.
    foreach ($rawLine in ($Text -split "`n")) {
        $line = $rawLine.TrimStart()
        if ($line.StartsWith('#') -or $line.StartsWith('//')) { continue }
        if ($line.Contains('EmptyWorkingSet(')) { return $false }
    }
    if ($Text -match 'Start-Sleep\s+-Seconds\s+(\d+)') {
        $sec = [int]$Matches[1]
        if ($sec -ge 2 -and $sec -le 15) { return $true }
    }
    if ($Text -match 'Start-Sleep\s+-Milliseconds\s+(\d+)') {
        $ms = [int]$Matches[1]
        if ($ms -ge 2000 -and $ms -le 15000) { return $true }
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
