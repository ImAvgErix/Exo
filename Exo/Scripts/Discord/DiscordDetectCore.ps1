# DiscordDetectCore.ps1 - pure detect classifiers (no Discord launch, no elevation).
# Dot-sourced by Exo-Discord-Detect.ps1; smokes call this file directly.
# Keep in sync with Exo.Services.DiscordPeakLogic (C# host heuristic).

Set-StrictMode -Version Latest

function Test-DiscOptStablePathText {
    param(
        [AllowNull()][string]$Text,
        [Parameter(Mandatory)][string]$Root
    )
    if ([string]::IsNullOrWhiteSpace($Text)) { return $false }
    try {
        $prefix = [IO.Path]::GetFullPath($Root).TrimEnd('\') + '\'
        $expanded = [Environment]::ExpandEnvironmentVariables($Text).Replace('/', '\')
        return $expanded.IndexOf($prefix, [StringComparison]::OrdinalIgnoreCase) -ge 0
    } catch { return $false }
}

function Test-DiscOptEquicordLoaderBytes {
    param(
        [AllowNull()][byte[]]$Bytes
    )
    if ($null -eq $Bytes -or $Bytes.Length -lt 64 -or $Bytes.Length -ge 4096) { return $false }
    try {
        $text = [Text.Encoding]::UTF8.GetString($Bytes)
        return ($text -match '(?i)equicord\.asar') -and ($text -match '(?i)require')
    } catch { return $false }
}

function Test-DiscOptEquicordLoaderText {
    param(
        [AllowNull()][string]$Text,
        [long]$Length = -1
    )
    if ($Length -lt 0 -and $null -ne $Text) { $Length = [Text.Encoding]::UTF8.GetByteCount($Text) }
    if ($Length -lt 64 -or $Length -ge 4096) { return $false }
    if ([string]::IsNullOrWhiteSpace($Text)) { return $false }
    return ($Text -match '(?i)equicord\.asar') -and ($Text -match '(?i)require')
}

function Test-DiscOptOpenAsarSize {
    param([long]$SizeBytes)
    return ($SizeBytes -gt 10000 -and $SizeBytes -lt 500000)
}

function Test-DiscOptKernelLayout {
    param(
        [long]$FfmpegProxyBytes,
        [long]$FfmpegRealBytes,
        [long]$VersionDllBytes
    )
    return ($FfmpegProxyBytes -gt 0 -and $FfmpegProxyBytes -lt 500000 -and
            $FfmpegRealBytes -gt 500000 -and
            $VersionDllBytes -gt 50000)
}

<#
.SYNOPSIS
  True when config.ini content is a valid gaming DiscOpt kernel (not folklore).
  Accepts current kit (TrimIntervalMs=4000) and prior peak (5000) - exact kit hash not required for ini.
#>
function Test-DiscOptKernelConfigText {
    param([AllowNull()][string]$ConfigText)
    if ([string]::IsNullOrWhiteSpace($ConfigText)) { return $false }
    if ($ConfigText -notmatch '(?m)^\s*EnableTrim\s*=\s*1\s*$') { return $false }
    if ($ConfigText -notmatch '(?m)^\s*PriorityClass\s*=\s*3\s*$') { return $false }
    if ($ConfigText -notmatch '(?m)^\s*TrimIntervalMs\s*=\s*(\d+)\s*$') { return $false }
    $trimMs = [int]$Matches[1]
    # Peak range: 2s-15s idle trim (kit ships 4000; older applies used 5000)
    if ($trimMs -lt 2000 -or $trimMs -gt 15000) { return $false }
    return $true
}

function Test-DiscOptKernelApplied {
    param(
        [long]$FfmpegProxyBytes,
        [long]$FfmpegRealBytes,
        [long]$VersionDllBytes,
        [AllowNull()][string]$ConfigText,
        [bool]$ProxyHashMatchesKit = $true,
        [bool]$VersionHashMatchesKit = $true
    )
    if (-not (Test-DiscOptKernelLayout -FfmpegProxyBytes $FfmpegProxyBytes -FfmpegRealBytes $FfmpegRealBytes -VersionDllBytes $VersionDllBytes)) {
        return $false
    }
    if (-not (Test-DiscOptKernelConfigText -ConfigText $ConfigText)) { return $false }
    # Proxy + version must be the shipping DiscOpt binaries when kit is available
    if (-not $ProxyHashMatchesKit) { return $false }
    if (-not $VersionHashMatchesKit) { return $false }
    return $true
}

function Test-DiscOptToastsOffFromMap {
    <#
      $Map: hashtable id -> Enabled value (int) or $null if key missing.
      Policy: at least one Discord toast key must exist; every present key Enabled=0.
    #>
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

function Test-DiscOptQuickStartFromSettingsJson {
    param([AllowNull()][string]$JsonText)
    if ([string]::IsNullOrWhiteSpace($JsonText)) { return $false }
    try {
        $sj = $JsonText | ConvertFrom-Json
        # Legacy OpenAsar quickstart
        if ($sj.openasar -and $sj.openasar.quickstart -eq $true) { return $true }
        # Modern Exo Host: SKIP_HOST_UPDATE + chromium lean / TTI flags
        if ($sj.SKIP_HOST_UPDATE -eq $true) {
            if ($sj.chromiumSwitches) { return $true }
            $names = @($sj.PSObject.Properties.Name)
            if ($names | Where-Object { $_ -match 'DESKTOP_TTI' }) { return $true }
        }
        return $false
    } catch { return $false }
}

function Test-DiscOptStartupOffFromSettingsJson {
    param([AllowNull()][string]$JsonText)
    if ([string]::IsNullOrWhiteSpace($JsonText)) { return $false }
    try {
        $sj = $JsonText | ConvertFrom-Json
        return ($sj.OPEN_ON_STARTUP -eq $false)
    } catch { return $false }
}

function Test-DiscOptApplyRecord {
    param(
        $State,
        [AllowNull()][string]$CurrentAppDir
    )
    if ($null -eq $State) { return $false }
    try {
        if ([string]$State.applyStatus -ne 'applied') { return $false }
        if ($State.applied -ne $true) { return $false }
        if ($State.fullApply -ne $true) { return $false }
        if ($State.windowsVerified -ne $true) { return $false }
        if ($State.debloatVerified -ne $true) { return $false }
        if ([string]::IsNullOrWhiteSpace($CurrentAppDir)) { return $false }
        $a = [IO.Path]::GetFullPath([string]$State.appDir).TrimEnd('\')
        $b = [IO.Path]::GetFullPath($CurrentAppDir).TrimEnd('\')
        return ($a -ieq $b)
    } catch { return $false }
}

function Test-DiscOptModuleDirHasPayload {
    param([AllowNull()][string]$ModuleDir)
    if ([string]::IsNullOrWhiteSpace($ModuleDir)) { return $false }
    if (-not (Test-Path -LiteralPath $ModuleDir)) { return $false }
    try {
        return (@(Get-ChildItem -LiteralPath $ModuleDir -File -Recurse -ErrorAction SilentlyContinue).Count -gt 0)
    } catch {
        return $true
    }
}

<#
.SYNOPSIS
  Complete client debloat classifier (aligned with DiscordPeakLogic.IsClientDebloatApplied).
  Hard: leftover app builds + optional modules with payload files.
  Soft: game SDK + extra locales - recoverable via state only when hard is clean.
#>
function Test-DiscOptClientDebloat {
    param(
        [int]$LeftoverAppBuildCount = 0,
        [int]$OptionalModulePayloadCount = 0,
        [int]$GameSdkFileCount = 0,
        [int]$ExtraLocaleCount = 0,
        [bool]$StateDebloatVerifiedSameApp = $false
    )
    if ($LeftoverAppBuildCount -lt 0) { $LeftoverAppBuildCount = 0 }
    if ($OptionalModulePayloadCount -lt 0) { $OptionalModulePayloadCount = 0 }
    if ($GameSdkFileCount -lt 0) { $GameSdkFileCount = 0 }
    if ($ExtraLocaleCount -lt 0) { $ExtraLocaleCount = 0 }

    $hardOk = ($LeftoverAppBuildCount -eq 0) -and ($OptionalModulePayloadCount -eq 0)
    $softOk = ($GameSdkFileCount -eq 0) -and ($ExtraLocaleCount -eq 0)
    if ($hardOk -and $softOk) { return $true }
    if ($hardOk -and $StateDebloatVerifiedSameApp) { return $true }
    return $false
}
