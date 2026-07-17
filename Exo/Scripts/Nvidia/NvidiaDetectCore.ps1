# NvidiaDetectCore.ps1 - pure detect classifiers (no NVAPI launch required for unit paths).
# Dot-sourced by Exo-Nvidia-Detect.ps1; smokes call this file.
# Keep aligned with Exo.Services.NvidiaDetectLogic.

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

function Test-ExoDisplayStatusOk {
    param([bool]$RefreshOk, [bool]$RegistryOk, [bool]$ColorOk, [bool]$PathScalingOk)
    return [bool]($RefreshOk -and ($RegistryOk -or ($ColorOk -and $PathScalingOk)))
}

function Test-ExoProfileNameMatchesSeries {
    param([AllowNull()][string]$ProfileFile, [AllowNull()][string]$Series, [bool]$Gsync)
    if ([string]::IsNullOrWhiteSpace($ProfileFile) -or [string]::IsNullOrWhiteSpace($Series)) { return $false }
    $expected = Get-ExoExpectedProfileFileName -SeriesId $Series -Gsync $Gsync
    return ($ProfileFile.Trim() -ieq $expected)
}

# Live DRS row strings (bound by the ViewModel). The drifted text uses an em dash;
# built from a char code because PowerShell sources must stay pure ASCII.
function Get-ExoDrsVerifiedDetailText { return 'Verified in driver' }
function Get-ExoDrsDriftedDetailText { return ('Drifted ' + [char]0x2014 + ' re-apply') }

function Get-ExoDrsVerificationResult {
    # Pure DRS live/import verification classifier.
    # Keep aligned with Nvidia-Optimizer.ps1 + Exo.Services.NvidiaDetectLogic.ClassifyDrsVerification.
    # Compares the intersection of pack pins vs a -exportCustomized driver dump:
    #   Expected $null/empty  -> unavailable (no pack to compare against)
    #   Exported $null        -> unavailable (export missing/unparseable, e.g. old NPI)
    #   Exported empty        -> drifted (export parsed; Base Profile has no customized pins)
    #   value mismatch        -> drifted (mismatch list reported honestly)
    #   RequiredIds missing from export -> drifted (pack always customizes them)
    param(
        [AllowNull()][hashtable]$Expected,
        [AllowNull()][hashtable]$Exported,
        [string[]]$RequiredIds = @()
    )
    if ($null -eq $Expected -or $Expected.Count -eq 0) {
        return [pscustomobject]@{ Status = 'unavailable'; ComparedCount = 0; Mismatches = @() }
    }
    if ($null -eq $Exported) {
        return [pscustomobject]@{ Status = 'unavailable'; ComparedCount = 0; Mismatches = @() }
    }
    $mismatches = New-Object System.Collections.Generic.List[string]
    $compared = 0
    foreach ($id in @($Expected.Keys | Sort-Object)) {
        if (-not $Exported.ContainsKey($id)) { continue }
        $compared++
        if ([string]$Exported[$id] -ne [string]$Expected[$id]) {
            [void]$mismatches.Add(("{0}: expected {1}, driver has {2}" -f $id, $Expected[$id], $Exported[$id]))
        }
    }
    foreach ($id in @($RequiredIds)) {
        if (-not $Expected.ContainsKey($id)) { continue }
        if (-not $Exported.ContainsKey($id)) {
            [void]$mismatches.Add(("{0}: expected {1}, missing from driver export" -f $id, $Expected[$id]))
        }
    }
    if ($compared -eq 0 -and $mismatches.Count -eq 0) {
        return [pscustomobject]@{
            Status        = 'drifted'
            ComparedCount = 0
            Mismatches    = @('no imported pack settings present in the driver export')
        }
    }
    $status = if ($mismatches.Count -eq 0) { 'verified' } else { 'drifted' }
    return [pscustomobject]@{
        Status        = $status
        ComparedCount = $compared
        Mismatches    = @($mismatches)
    }
}
