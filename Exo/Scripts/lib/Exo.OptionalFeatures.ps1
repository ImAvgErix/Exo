# Exo.OptionalFeatures.ps1 - timeout-safe optional feature quieting via dism.exe only.
# NEVER call Get-WindowsOptionalFeature / Disable-WindowsOptionalFeature (DismHost hang).

Set-StrictMode -Version Latest

function Get-ExoOptionalFeatureDisableList {
    @(
        'SMB1Protocol'
        'SMB1Protocol-Client'
        'SMB1Protocol-Server'
        'FaxServicesClientPackage'
        'Printing-XPSServices-Features'
        'WorkFolders-Client'
        'SimpleTCP'
        'Internet-Explorer-Optional-amd64'
        'WindowsMediaPlayer'
        'MediaPlayback'
        'DirectPlay'
        'LegacyComponents'
    )
}

function Invoke-ExoDismTimed {
    param(
        [Parameter(Mandatory)][string[]]$ArgumentList,
        [int]$TimeoutMs = 20000
    )
    try {
        $p = Start-Process -FilePath "$env:SystemRoot\System32\dism.exe" `
            -ArgumentList $ArgumentList `
            -WindowStyle Hidden -PassThru -RedirectStandardOutput "$env:TEMP\exo-dism-out.txt" `
            -RedirectStandardError "$env:TEMP\exo-dism-err.txt" -ErrorAction Stop
        if (-not $p.WaitForExit($TimeoutMs)) {
            try { $p.Kill($true) } catch { try { $p.Kill() } catch { } }
            return [pscustomobject]@{ ExitCode = -1; TimedOut = $true; Output = '' }
        }
        $out = ''
        try { $out = [IO.File]::ReadAllText("$env:TEMP\exo-dism-out.txt") } catch { }
        return [pscustomobject]@{ ExitCode = [int]$p.ExitCode; TimedOut = $false; Output = $out }
    } catch {
        return [pscustomobject]@{ ExitCode = -1; TimedOut = $true; Output = $_.Exception.Message }
    }
}

function Set-ExoOptionalFeaturesQuieted {
    # Full shortlist with hard timeouts - never hangs Apply.
    param([switch]$Force)
    $disabled = 0
    $skipped = 0
    $errors = 0
    $timedOut = 0
    $samples = [System.Collections.Generic.List[string]]::new()
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $budgetMs = 120000

    foreach ($name in @(Get-ExoOptionalFeatureDisableList)) {
        if ($sw.ElapsedMilliseconds -gt $budgetMs) { $timedOut++; break }

        $q = Invoke-ExoDismTimed -ArgumentList @('/Online', '/Get-FeatureInfo', "/FeatureName:$name") -TimeoutMs 8000
        if ($q.TimedOut) { $timedOut++; $skipped++; continue }
        if ($q.ExitCode -ne 0 -or [string]::IsNullOrWhiteSpace($q.Output)) { $skipped++; continue }
        if ($q.Output -match 'State : Disabled|State : Disable Pending') { $skipped++; continue }
        if ($q.Output -notmatch 'State : Enabled') { $skipped++; continue }

        $d = Invoke-ExoDismTimed -ArgumentList @('/Online', '/Disable-Feature', "/FeatureName:$name", '/NoRestart') -TimeoutMs 25000
        if ($d.TimedOut) { $timedOut++; $skipped++; continue }
        if ($d.ExitCode -eq 0 -or $d.ExitCode -eq 3010) {
            $disabled++
            if ($samples.Count -lt 12) { [void]$samples.Add("feat:$name") }
        } else {
            $errors++
        }
    }

    return [pscustomobject]@{
        FeaturesDisabled    = [int]$disabled
        CapabilitiesRemoved = 0
        Skipped             = [int]$skipped
        Errors              = [int]$errors
        TimedOut            = [int]$timedOut
        Samples             = @($samples)
        Ok                  = $true
        ElapsedMs           = [int]$sw.ElapsedMilliseconds
    }
}

function Test-ExoOptionalFeaturesQuieted {
    try {
        $path = Join-Path $env:LOCALAPPDATA 'Exo\windows-optimizer.json'
        if (Test-Path -LiteralPath $path) {
            $state = Get-Content -LiteralPath $path -Raw -Encoding UTF8 | ConvertFrom-Json
            if ($state.optionalFeaturesOk -eq $true) { return $true }
            if ($state.optionalFeaturesDeepPass -eq $true) { return $true }
            if ([int]$state.optionalFeaturesDisabled -gt 0) { return $true }
        }
    } catch { }
    return $false
}
