# NvidiaDetectCore.ps1 - pure detect classifiers (no NVAPI launch required for unit paths).
# Dot-sourced by Exo-Nvidia-Detect.ps1; smokes call this file.
# Keep aligned with Exo.Services.NvidiaPeakLogic.

Set-StrictMode -Version Latest

function Get-ExoGpuSeriesFromName {
    param([AllowNull()][string]$Name)
    if ([string]::IsNullOrWhiteSpace($Name)) { return $null }
    if ($Name -match '(?i)\b(?:RTX|GTX)\s*([1-5])0\d{2}\b') { return $Matches[1] + '0' }
    if ($Name -match '(?i)\b([1-5])0\d{2}\b') { return $Matches[1] + '0' }
    if ($Name -match '(?i)\b16\d{2}\b') { return '10' }
    return $null
}

function Get-ExoExpectedProfileFileName {
    param([string]$SeriesId, [bool]$Gsync)
    if ($Gsync) { return "$SeriesId Series G-SYNC.nip" }
    return "$SeriesId Series.nip"
}

function Test-ExoDisplayContainerExe {
    param([AllowNull()][string]$Exe)
    return [bool]($Exe -match '(?i)NVDisplay\.Container|Display\.NvContainer|nv_dispi\.inf')
}

function Test-ExoNvidiaAppTrayExe {
    param([AllowNull()][string]$Exe)
    if ([string]::IsNullOrWhiteSpace($Exe)) { return $false }
    if (Test-ExoDisplayContainerExe $Exe) { return $false }
    return [bool]($Exe -match '(?i)NVIDIA App|GFExperience|NvBackend|NvNode|ShadowPlay|nvsphelper|nvapp')
}

function Test-ExoDisplayTrayHidden {
    param([bool]$KeyExists, [AllowNull()]$IsPromoted)
    if (-not $KeyExists) { return $true }
    try { return ([int]$IsPromoted -eq 0) } catch { return $false }
}

function Test-ExoDisplayStatusPeakOk {
    param([bool]$RefreshOk, [bool]$RegistryOk, [bool]$ColorOk, [bool]$PathScalingOk)
    return [bool]($RefreshOk -and ($RegistryOk -or ($ColorOk -and $PathScalingOk)))
}

function Test-ExoProfileNameMatchesSeries {
    param([AllowNull()][string]$ProfileFile, [AllowNull()][string]$Series, [bool]$Gsync)
    if ([string]::IsNullOrWhiteSpace($ProfileFile) -or [string]::IsNullOrWhiteSpace($Series)) { return $false }
    $expected = Get-ExoExpectedProfileFileName -SeriesId $Series -Gsync $Gsync
    return ($ProfileFile.Trim() -ieq $expected)
}
