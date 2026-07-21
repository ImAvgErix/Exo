# Exo.NoBackground.ps1 — purge ALL Exo Run-key companions (no always-on yield).
# Product policy: zero idle background processes. One-shot Apply only.

Set-StrictMode -Version Latest

function Test-ExoSilentCompanionValue {
    param([string]$Name, [string]$Value)
    # Never keep yield / memory guard / any Exo Run companion
    return $false
}

function Unregister-ExoBackground {
    param([switch]$Quiet)
    $removed = 0
    $known = @(
        'Exo Steam Memory Guard',
        'Exo Discord Ram',
        'Exo-Steam-MemoryGuard',
        'ExoDiscordRam'
    )
    foreach ($name in $known) {
        try {
            Unregister-ScheduledTask -TaskName $name -Confirm:$false -ErrorAction SilentlyContinue
            $removed++
        } catch { }
        try { $null = schtasks /Delete /TN $name /F 2>&1 } catch { }
    }

    try {
        Get-ScheduledTask -ErrorAction SilentlyContinue | Where-Object {
            $_.TaskName -like 'Exo-*' -or $_.TaskPath -like '*\Exo\*'
        } | ForEach-Object {
            try {
                Unregister-ScheduledTask -TaskName $_.TaskName -TaskPath $_.TaskPath -Confirm:$false -ErrorAction SilentlyContinue
                $removed++
            } catch { }
        }
    } catch { }

    foreach ($run in @(
        'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run',
        'HKLM:\Software\Microsoft\Windows\CurrentVersion\Run',
        'HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run'
    )) {
        if (-not (Test-Path -LiteralPath $run)) { continue }
        try {
            $item = Get-Item -LiteralPath $run -ErrorAction SilentlyContinue
            if (-not $item) { continue }
            foreach ($name in @($item.GetValueNames())) {
                $val = [string]$item.GetValue($name)
                if (-not ($name -like 'Exo*' -or $val -match '(?i)LocalAppData\\Exo|yield-guard|MemoryGuard')) {
                    continue
                }

                # Keep silent yield companions
                if (Test-ExoSilentCompanionValue -Name $name -Value $val) { continue }

                # Drop wscript / WindowsApps stubs even if named Yield
                $isBrokenHost = $val -match '(?i)wscript|WindowsApps\\pwsh'
                $isLegacyGuard = $val -match '(?i)MemoryGuard|Exo-SteamMemoryGuard|ExoDiscordRam'
                $isConsolePs =
                    $val -match '(?i)(pwsh|powershell)(\.exe)?' -and
                    $val -notmatch '(?i)-WindowStyle\s+Hidden'

                if ($isBrokenHost -or $isLegacyGuard -or $isConsolePs -or ($name -like 'Exo*' -and -not (Test-ExoSilentCompanionValue -Name $name -Value $val))) {
                    try {
                        Remove-ItemProperty -LiteralPath $run -Name $name -Force -ErrorAction SilentlyContinue
                        $removed++
                    } catch { }
                }
            }
        } catch { }
    }

    # Do NOT wipe StartupApproved for Exo-*-Yield — that re-enables the "disabled at login" bit wrongly.
    try {
        $sa = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run'
        if (Test-Path $sa) {
            $item = Get-Item -LiteralPath $sa -ErrorAction SilentlyContinue
            foreach ($name in @($item.GetValueNames())) {
                if ($name -notlike 'Exo*') { continue }
                if ($name -match '(?i)^Exo-(Riot|Epic)-Yield$') { continue }
                try {
                    Remove-ItemProperty -LiteralPath $sa -Name $name -Force -ErrorAction SilentlyContinue
                    $removed++
                } catch { }
            }
        }
    } catch { }

    if (-not $Quiet) {
        Write-Output ("EXO_REPORT:no-background|ok:purged={0}" -f $removed)
    }
    return $removed
}

function Test-ExoNoBackground {
    # True when no noisy Exo console Run keys. Silent Hidden+File yield companions allowed.
    try {
        foreach ($run in @(
            'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run',
            'HKLM:\Software\Microsoft\Windows\CurrentVersion\Run'
        )) {
            if (-not (Test-Path $run)) { continue }
            $item = Get-Item -LiteralPath $run -ErrorAction SilentlyContinue
            if (-not $item) { continue }
            foreach ($name in @($item.GetValueNames())) {
                $val = [string]$item.GetValue($name)
                if (-not ($name -like 'Exo*' -or $val -match '(?i)LocalAppData\\Exo|yield-guard|MemoryGuard')) {
                    continue
                }
                if (Test-ExoSilentCompanionValue -Name $name -Value $val) { continue }
                # Any other Exo / guard entry is noise
                return $false
            }
        }
    } catch { }
    return $true
}
